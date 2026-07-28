using System.Diagnostics;
using System.Threading.Channels;
using DeskHush.Core.Interfaces;
using DeskHush.Core.Models;
using DeskHush.Core.Services;
using DeskHush.Windows.Interop;

namespace DeskHush.Windows.Windows;

public sealed class WinEventWindowRecorder : IWindowRecorder
{
    internal const int DefaultQueueCapacity = 512;
    internal const int DefaultConsumerCount = 2;

    private const long QueueDebounceMilliseconds = 500;

    private readonly object stateLock = new();
    private readonly Dictionary<nint, long> recentlyQueued = new();
    private readonly NativeMethods.WinEventDelegate callback;
    private readonly IWindowCatalog windowCatalog;
    private readonly WindowRecordBuffer recordBuffer;
    private readonly IWinEventHookApi hookApi;
    private readonly Channel<WindowWorkItem> workQueue;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task[] consumers;
    private readonly Func<long, int, CancellationToken, ValueTask> waitUntilOffsetAsync;
    private nint hook;
    private long generation;
    private long epoch;
    private bool disposed;

    public WinEventWindowRecorder() : this(new WindowCatalog())
    {
    }

    public WinEventWindowRecorder(
        WindowCatalog windowCatalog,
        WindowRecordBuffer? recordBuffer = null)
        : this(
            windowCatalog,
            recordBuffer,
            new NativeWinEventHookApi(),
            DefaultQueueCapacity,
            DefaultConsumerCount,
            WaitUntilOffsetAsync)
    {
    }

    internal WinEventWindowRecorder(
        IWindowCatalog windowCatalog,
        WindowRecordBuffer? recordBuffer,
        IWinEventHookApi hookApi,
        int queueCapacity,
        int consumerCount,
        Func<long, int, CancellationToken, ValueTask> waitUntilOffsetAsync)
    {
        this.windowCatalog = windowCatalog ?? throw new ArgumentNullException(nameof(windowCatalog));
        this.recordBuffer = recordBuffer ?? new WindowRecordBuffer();
        this.hookApi = hookApi ?? throw new ArgumentNullException(nameof(hookApi));
        this.waitUntilOffsetAsync = waitUntilOffsetAsync ?? throw new ArgumentNullException(nameof(waitUntilOffsetAsync));

        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        }

        if (consumerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(consumerCount));
        }

        callback = HandleWinEvent;
        workQueue = Channel.CreateBounded<WindowWorkItem>(new BoundedChannelOptions(queueCapacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = consumerCount == 1,
            SingleWriter = false
        });
        consumers = Enumerable.Range(0, consumerCount)
            .Select(_ => Task.Run(ConsumeQueueAsync))
            .ToArray();
    }

    public bool IsRunning
    {
        get
        {
            lock (stateLock)
            {
                return !disposed && hook != nint.Zero;
            }
        }
    }

    public IReadOnlyList<WindowRecord> Records => recordBuffer.Records;

    public event EventHandler<WindowRecord>? WindowRecorded;

    public void Start()
    {
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (hook != nint.Zero)
            {
                return;
            }

            recentlyQueued.Clear();
            var registeredHook = hookApi.Register(callback);
            if (registeredHook == nint.Zero)
            {
                throw new InvalidOperationException("无法注册 Windows 窗口记录事件钩子。");
            }

            generation++;
            hook = registeredHook;
        }
    }

    public void Stop()
    {
        lock (stateLock)
        {
            if (disposed)
            {
                return;
            }

            generation++;
            recentlyQueued.Clear();
            UnhookLocked();
        }
    }

    public void Clear()
    {
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            epoch++;
            recentlyQueued.Clear();
            recordBuffer.Clear();
        }
    }

    public void Dispose()
    {
        lock (stateLock)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            generation++;
            epoch++;
            recentlyQueued.Clear();
            UnhookLocked();
            workQueue.Writer.TryComplete();
            shutdown.Cancel();
        }

        GC.SuppressFinalize(this);
    }

    private void HandleWinEvent(
        nint eventHook,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (windowHandle == nint.Zero ||
            objectId != NativeMethods.ObjectIdWindow ||
            childId != NativeMethods.ChildIdSelf)
        {
            return;
        }

        var eventTimestamp = Stopwatch.GetTimestamp();
        lock (stateLock)
        {
            if (disposed || hook == nint.Zero || !TryMarkQueuedLocked(windowHandle, eventTimestamp))
            {
                return;
            }

            _ = workQueue.Writer.TryWrite(new WindowWorkItem(
                windowHandle,
                eventTimestamp,
                generation,
                epoch));
        }
    }

    private bool TryMarkQueuedLocked(nint windowHandle, long now)
    {
        if (recentlyQueued.TryGetValue(windowHandle, out var previous) &&
            Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds < QueueDebounceMilliseconds)
        {
            return false;
        }

        recentlyQueued[windowHandle] = now;
        if (recentlyQueued.Count <= 2048)
        {
            return true;
        }

        foreach (var staleHandle in recentlyQueued
                     .Where(item => Stopwatch.GetElapsedTime(item.Value, now) > TimeSpan.FromMinutes(1))
                     .Take(512)
                     .Select(item => item.Key)
                     .ToArray())
        {
            recentlyQueued.Remove(staleHandle);
        }

        return true;
    }

    private async Task ConsumeQueueAsync()
    {
        try
        {
            await foreach (var workItem in workQueue.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await ProcessWorkItemAsync(workItem, shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // One inaccessible window must not stop the recorder pipeline.
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
    }

    private async ValueTask ProcessWorkItemAsync(WindowWorkItem workItem, CancellationToken cancellationToken)
    {
        if (!IsWorkItemCurrent(workItem))
        {
            return;
        }

        var snapshot = await WindowSnapshotSampler.CaptureAsync(
            workItem.WindowHandle,
            windowCatalog.TryGetWindow,
            (offset, token) => waitUntilOffsetAsync(workItem.EventTimestamp, offset, token),
            cancellationToken).ConfigureAwait(false);

        if (snapshot is null)
        {
            return;
        }

        WindowRecord? record;
        lock (stateLock)
        {
            if (!IsWorkItemCurrentLocked(workItem))
            {
                return;
            }

            record = recordBuffer.TryAdd(snapshot, DateTimeOffset.UtcNow);
        }

        if (record is not null)
        {
            WindowRecorded?.Invoke(this, record);
        }
    }

    private bool IsWorkItemCurrent(WindowWorkItem workItem)
    {
        lock (stateLock)
        {
            return IsWorkItemCurrentLocked(workItem);
        }
    }

    private bool IsWorkItemCurrentLocked(WindowWorkItem workItem) =>
        !disposed &&
        hook != nint.Zero &&
        generation == workItem.Generation &&
        epoch == workItem.Epoch;

    private void UnhookLocked()
    {
        if (hook == nint.Zero)
        {
            return;
        }

        var activeHook = hook;
        hook = nint.Zero;
        _ = hookApi.Unregister(activeHook);
    }

    private static ValueTask WaitUntilOffsetAsync(
        long eventTimestamp,
        int offsetMilliseconds,
        CancellationToken cancellationToken)
    {
        var remaining = TimeSpan.FromMilliseconds(offsetMilliseconds) - Stopwatch.GetElapsedTime(eventTimestamp);
        return remaining <= TimeSpan.Zero
            ? ValueTask.CompletedTask
            : new ValueTask(Task.Delay(remaining, cancellationToken));
    }

    private readonly record struct WindowWorkItem(
        nint WindowHandle,
        long EventTimestamp,
        long Generation,
        long Epoch);
}

internal interface IWinEventHookApi
{
    nint Register(NativeMethods.WinEventDelegate callback);

    bool Unregister(nint hook);
}

internal sealed class NativeWinEventHookApi : IWinEventHookApi
{
    public nint Register(NativeMethods.WinEventDelegate callback) => NativeMethods.SetWinEventHook(
        NativeMethods.EventObjectShow,
        NativeMethods.EventObjectShow,
        nint.Zero,
        callback,
        0,
        0,
        NativeMethods.WineventOutOfContext | NativeMethods.WineventSkipOwnProcess);

    public bool Unregister(nint hook) => NativeMethods.UnhookWinEvent(hook);
}
