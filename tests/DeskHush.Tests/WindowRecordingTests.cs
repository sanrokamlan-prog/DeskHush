using DeskHush.Core.Interfaces;
using DeskHush.Core.Models;
using DeskHush.Core.Services;
using DeskHush.Windows.Interop;
using DeskHush.Windows.Windows;

namespace DeskHush.Tests;

internal static class WindowRecordingTests
{
    internal static Task TestDeduplicationAndOrdering()
    {
        var buffer = new WindowRecordBuffer(capacity: 3, deduplicationInterval: TimeSpan.FromSeconds(1));
        var now = new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);

        var first = buffer.TryAdd(CreateWindow(1, "First"), now);
        var duplicate = buffer.TryAdd(CreateWindow(1, "First updated"), now.AddMilliseconds(999));
        var repeated = buffer.TryAdd(CreateWindow(1, "First repeated"), now.AddSeconds(1));
        var second = buffer.TryAdd(CreateWindow(2, "Second"), now.AddSeconds(2));

        Assert(first is not null, "The first event should be recorded.");
        Assert(duplicate is null, "Repeated events for the same window should be deduplicated.");
        Assert(repeated is not null, "The same window should be recordable after the deduplication interval.");
        Assert(second is not null, "A different window should be recorded.");
        Assert(buffer.Records.Select(record => record.Title)
            .SequenceEqual(["Second", "First repeated", "First"]), "Records should be newest first.");
        return Task.CompletedTask;
    }

    internal static Task TestCapacityAndClear()
    {
        var buffer = new WindowRecordBuffer(capacity: 3, deduplicationInterval: TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);

        for (var index = 1; index <= 4; index++)
        {
            _ = buffer.TryAdd(CreateWindow(index, $"Window {index}"), now.AddSeconds(index));
        }

        Assert(buffer.Records.Count == 3, "The record buffer should enforce its capacity.");
        Assert(buffer.Records.Select(record => record.Title)
            .SequenceEqual(["Window 4", "Window 3", "Window 2"]), "The oldest record should be evicted first.");

        buffer.Clear();
        Assert(buffer.Records.Count == 0, "Clear should remove all records.");
        Assert(buffer.TryAdd(CreateWindow(4, "Window 4 again"), now.AddSeconds(5)) is not null,
            "Clear should also reset deduplication state.");
        return Task.CompletedTask;
    }

    internal static Task TestConcurrentDeduplication()
    {
        var buffer = new WindowRecordBuffer(capacity: 20);
        var now = new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero);

        Parallel.For(0, 100, _ => buffer.TryAdd(CreateWindow(99, "Concurrent"), now));

        Assert(buffer.Records.Count == 1, "Concurrent duplicate events should produce one record.");
        return Task.CompletedTask;
    }

    internal static async Task TestSnapshotScheduleAndStableTitle()
    {
        var snapshots = new Queue<WindowInfo?>(
        [
            CreateWindow(1, "Loading") with { ProcessPath = @"C:\Apps\sample.exe" },
            CreateWindow(1, "Ready") with { ProcessPath = string.Empty },
            CreateWindow(1, "Ready") with { ProcessPath = string.Empty },
            CreateWindow(1, "Unused")
        ]);
        var offsets = new List<int>();

        var result = await WindowSnapshotSampler.CaptureAsync(
            (nint)1,
            _ => snapshots.Dequeue(),
            (offset, _) =>
            {
                offsets.Add(offset);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert(offsets.SequenceEqual([0, 40, 120]), "Sampling should use absolute retry offsets and stop after two stable titles.");
        Assert(result is { Title: "Ready", ProcessPath: @"C:\Apps\sample.exe" },
            "A stable snapshot should retain richer metadata from earlier samples.");
        Assert(snapshots.Count == 1, "Stable-title detection should avoid the final retry.");
    }

    internal static async Task TestSnapshotRetainsBestMetadata()
    {
        var rich = CreateWindow(2, "Initial") with { ClassName = string.Empty };
        var degraded = CreateWindow(2, "Changed") with
        {
            ProcessPath = string.Empty,
            ClassName = "PopupWindow",
            Width = 0,
            Height = 0
        };
        var final = degraded with { Title = "Final" };
        var snapshots = new Queue<WindowInfo?>([rich, null, degraded, final]);
        var offsets = new List<int>();

        var result = await WindowSnapshotSampler.CaptureAsync(
            (nint)2,
            _ => snapshots.Dequeue(),
            (offset, _) =>
            {
                offsets.Add(offset);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert(offsets.SequenceEqual([0, 40, 120, 300]), "An unstable title should consume all scheduled retries.");
        Assert(result is
        {
            Title: "Final",
            ClassName: "PopupWindow",
            ProcessPath: @"C:\Apps\sample.exe",
            Width: 640,
            Height: 480
        }, "The final snapshot should combine the newest title with the best available metadata.");
    }

    internal static async Task TestClearInvalidatesQueuedWork()
    {
        var firstWaitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSampling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoSamplesRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sampleCount = 0;
        var catalog = new DelegateWindowCatalog(_ =>
        {
            if (Interlocked.Increment(ref sampleCount) >= 2)
            {
                twoSamplesRead.TrySetResult();
            }

            return CreateWindow(3, "Stable");
        });
        var hookApi = new FakeWinEventHookApi();
        using var recorder = CreateRecorder(
            catalog,
            hookApi,
            consumerCount: 1,
            waitUntilOffsetAsync: (_, offset, token) =>
            {
                if (offset != 0)
                {
                    return ValueTask.CompletedTask;
                }

                firstWaitEntered.TrySetResult();
                return new ValueTask(releaseSampling.Task.WaitAsync(token));
            });
        var raisedEvents = 0;
        recorder.WindowRecorded += (_, _) => Interlocked.Increment(ref raisedEvents);

        recorder.Start();
        hookApi.Raise((nint)3);
        await firstWaitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        recorder.Clear();
        releaseSampling.TrySetResult();
        await twoSamplesRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(20);

        Assert(recorder.Records.Count == 0, "Clear should reject work queued under the previous epoch.");
        Assert(raisedEvents == 0, "Cleared work should not raise a record notification.");
    }

    internal static async Task TestBoundedConsumerConcurrency()
    {
        var catalog = new BlockingWindowCatalog(requiredConcurrentCalls: 2);
        var hookApi = new FakeWinEventHookApi();
        using var recorder = CreateRecorder(catalog, hookApi, queueCapacity: 4, consumerCount: 2);

        try
        {
            recorder.Start();
            for (var handle = 10; handle < 110; handle++)
            {
                hookApi.Raise((nint)handle);
            }

            Assert(catalog.WaitUntilConsumersBlocked(TimeSpan.FromSeconds(2)), "Both fixed consumers should begin processing.");
            await Task.Delay(30);
            Assert(catalog.MaximumConcurrency == 2, "A burst must not create more work consumers than configured.");

            catalog.Release();
            await WaitUntilAsync(() => recorder.Records.Count >= 4, TimeSpan.FromSeconds(2));
            await Task.Delay(30);
            Assert(recorder.Records.Count <= 6, "The bounded queue should retain at most its capacity plus active consumers.");
        }
        finally
        {
            catalog.Release();
        }
    }

    internal static async Task TestLifecycleSerializesStartAndDispose()
    {
        var hookApi = new BlockingRegisterHookApi();
        var recorder = CreateRecorder(new DelegateWindowCatalog(_ => null), hookApi, consumerCount: 1);

        var start = Task.Run(recorder.Start);
        await hookApi.RegisterEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var dispose = Task.Run(recorder.Dispose);
        await Task.Delay(30);
        Assert(!dispose.IsCompleted, "Dispose should wait for the in-progress registration state transition.");

        hookApi.ReleaseRegistration.TrySetResult();
        await Task.WhenAll(start, dispose).WaitAsync(TimeSpan.FromSeconds(2));

        Assert(hookApi.ActiveHookCount == 0, "Dispose racing with Start must leave no active hook.");
        Assert(hookApi.UnregisterCount == 1, "The hook registered by Start should be unregistered exactly once.");
        Assert(!recorder.IsRunning, "A disposed recorder cannot remain running.");
        AssertThrows<ObjectDisposedException>(recorder.Start, "Start after Dispose should be rejected.");
    }

    private static WinEventWindowRecorder CreateRecorder(
        IWindowCatalog catalog,
        IWinEventHookApi hookApi,
        int queueCapacity = 16,
        int consumerCount = 2,
        Func<long, int, CancellationToken, ValueTask>? waitUntilOffsetAsync = null) =>
        new(
            catalog,
            new WindowRecordBuffer(),
            hookApi,
            queueCapacity,
            consumerCount,
            waitUntilOffsetAsync ?? ((_, _, _) => ValueTask.CompletedTask));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected recorder state was not reached in time.");
            }

            await Task.Delay(10);
        }
    }

    private static WindowInfo CreateWindow(int handle, string title) => new(
        (nint)handle,
        handle + 100,
        "sample",
        @"C:\Apps\sample.exe",
        title,
        "SampleWindow",
        10,
        20,
        640,
        480);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class DelegateWindowCatalog(Func<nint, WindowInfo?> resolver) : IWindowCatalog
    {
        public IReadOnlyList<WindowInfo> GetVisibleWindows() => [];

        public WindowInfo? TryGetWindow(nint windowHandle) => resolver(windowHandle);
    }

    private sealed class BlockingWindowCatalog(int requiredConcurrentCalls) : IWindowCatalog
    {
        private readonly CountdownEvent consumersBlocked = new(requiredConcurrentCalls);
        private readonly ManualResetEventSlim release = new();
        private int currentConcurrency;
        private int maximumConcurrency;

        internal int MaximumConcurrency => Volatile.Read(ref maximumConcurrency);

        public IReadOnlyList<WindowInfo> GetVisibleWindows() => [];

        public WindowInfo? TryGetWindow(nint windowHandle)
        {
            var current = Interlocked.Increment(ref currentConcurrency);
            UpdateMaximum(current);
            if (consumersBlocked.CurrentCount > 0)
            {
                consumersBlocked.Signal();
            }

            try
            {
                release.Wait();
                return CreateWindow((int)windowHandle, "Stable");
            }
            finally
            {
                Interlocked.Decrement(ref currentConcurrency);
            }
        }

        internal bool WaitUntilConsumersBlocked(TimeSpan timeout) => consumersBlocked.Wait(timeout);

        internal void Release() => release.Set();

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var previous = Volatile.Read(ref maximumConcurrency);
                if (candidate <= previous || Interlocked.CompareExchange(ref maximumConcurrency, candidate, previous) == previous)
                {
                    return;
                }
            }
        }
    }

    private class FakeWinEventHookApi : IWinEventHookApi
    {
        private NativeMethods.WinEventDelegate? callback;

        public virtual nint Register(NativeMethods.WinEventDelegate eventCallback)
        {
            callback = eventCallback;
            return (nint)1;
        }

        public virtual bool Unregister(nint hook)
        {
            callback = null;
            return true;
        }

        internal void Raise(nint windowHandle)
        {
            var eventCallback = callback ?? throw new InvalidOperationException("The test hook is not active.");
            eventCallback(
                (nint)1,
                NativeMethods.EventObjectShow,
                windowHandle,
                NativeMethods.ObjectIdWindow,
                NativeMethods.ChildIdSelf,
                0,
                0);
        }
    }

    private sealed class BlockingRegisterHookApi : IWinEventHookApi
    {
        private int activeHookCount;
        private int unregisterCount;

        internal TaskCompletionSource RegisterEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseRegistration { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ActiveHookCount => Volatile.Read(ref activeHookCount);

        internal int UnregisterCount => Volatile.Read(ref unregisterCount);

        public nint Register(NativeMethods.WinEventDelegate callback)
        {
            RegisterEntered.TrySetResult();
            ReleaseRegistration.Task.GetAwaiter().GetResult();
            Interlocked.Increment(ref activeHookCount);
            return (nint)7;
        }

        public bool Unregister(nint hook)
        {
            Interlocked.Increment(ref unregisterCount);
            Interlocked.Decrement(ref activeHookCount);
            return true;
        }
    }
}
