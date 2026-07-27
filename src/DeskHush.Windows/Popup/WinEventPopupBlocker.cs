using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DeskHush.Core.Interfaces;
using DeskHush.Core.Models;
using DeskHush.Core.Services;
using DeskHush.Windows.Interop;
using DeskHush.Windows.Windows;

namespace DeskHush.Windows.Popup;

public sealed class WinEventPopupBlocker : IPopupBlocker
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(750);

    private readonly object stateLock = new();
    private readonly ConcurrentDictionary<nint, long> recentlyHandled = new();
    private readonly NativeMethods.WinEventDelegate callback;
    private readonly WindowCatalog windowCatalog;
    private RuleSnapshot[] rules = [];
    private nint hook;
    private long generation;
    private bool disposed;

    public WinEventPopupBlocker() : this(new WindowCatalog())
    {
    }

    public WinEventPopupBlocker(WindowCatalog windowCatalog)
    {
        this.windowCatalog = windowCatalog;
        callback = HandleWinEvent;
    }

    public bool IsRunning
    {
        get
        {
            lock (stateLock)
            {
                return hook != nint.Zero;
            }
        }
    }

    public event EventHandler<PopupBlockedEvent>? PopupBlocked;

    public void Start(IReadOnlyCollection<PopupRule> popupRules)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var snapshots = CreateRuleSnapshots(popupRules);

        lock (stateLock)
        {
            rules = snapshots;
            generation++;
            if (hook != nint.Zero)
            {
                return;
            }

            hook = NativeMethods.SetWinEventHook(
                NativeMethods.EventObjectShow,
                NativeMethods.EventObjectShow,
                nint.Zero,
                callback,
                0,
                0,
                NativeMethods.WineventOutOfContext | NativeMethods.WineventSkipOwnProcess);

            if (hook == nint.Zero)
            {
                throw new InvalidOperationException("无法注册 Windows 窗口事件钩子。");
            }
        }
    }

    public void UpdateRules(IReadOnlyCollection<PopupRule> popupRules)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var snapshots = CreateRuleSnapshots(popupRules);
        lock (stateLock)
        {
            rules = snapshots;
            generation++;
        }
    }

    public void Stop()
    {
        lock (stateLock)
        {
            generation++;
            rules = [];
            if (hook == nint.Zero)
            {
                return;
            }

            _ = NativeMethods.UnhookWinEvent(hook);
            hook = nint.Zero;
            recentlyHandled.Clear();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Stop();
        disposed = true;
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
        if (windowHandle == nint.Zero || objectId != NativeMethods.ObjectIdWindow || childId != NativeMethods.ChildIdSelf)
        {
            return;
        }

        var now = Environment.TickCount64;
        while (true)
        {
            if (!recentlyHandled.TryGetValue(windowHandle, out var previous))
            {
                if (recentlyHandled.TryAdd(windowHandle, now))
                {
                    break;
                }

                continue;
            }

            if (now - previous < DebounceInterval.TotalMilliseconds)
            {
                return;
            }

            if (recentlyHandled.TryUpdate(windowHandle, now, previous))
            {
                break;
            }
        }

        if (recentlyHandled.Count > 2048)
        {
            foreach (var stale in recentlyHandled.Where(item => now - item.Value > 60_000).Take(512))
            {
                recentlyHandled.TryRemove(stale.Key, out _);
            }
        }

        long queuedGeneration;
        lock (stateLock)
        {
            if (hook == nint.Zero || disposed)
            {
                return;
            }

            queuedGeneration = generation;
        }

        ThreadPool.QueueUserWorkItem(_ => EvaluateWindowSafely(windowHandle, queuedGeneration));
    }

    private void EvaluateWindowSafely(nint windowHandle, long queuedGeneration)
    {
        try
        {
            EvaluateWindow(windowHandle, queuedGeneration);
        }
        catch (Exception)
        {
            // Native event callbacks must never terminate the application process.
        }
    }

    private void EvaluateWindow(nint windowHandle, long queuedGeneration)
    {
        Thread.Sleep(120);
        var window = windowCatalog.TryGetWindow(windowHandle);
        if (window is null || PopupRuleValidator.IsProtectedProcess(window.ProcessName, window.ProcessPath))
        {
            return;
        }

        RuleSnapshot[] snapshot;
        lock (stateLock)
        {
            if (hook == nint.Zero || queuedGeneration != generation)
            {
                return;
            }

            snapshot = rules;
        }

        var matchedRule = snapshot.FirstOrDefault(candidate => IsMatch(candidate.MatchRule, window));
        if (matchedRule is null)
        {
            return;
        }

        PopupBlockedEvent? popupEvent = null;
        lock (stateLock)
        {
            if (hook == nint.Zero || queuedGeneration != generation)
            {
                return;
            }

            var succeeded = matchedRule.MatchRule.Action switch
            {
                PopupAction.Close => NativeMethods.PostMessage(window.Handle, NativeMethods.WmClose, nint.Zero, nint.Zero),
                PopupAction.Hide => NativeMethods.ShowWindowAsync(window.Handle, NativeMethods.SwHide),
                _ => false
            };

            if (!succeeded)
            {
                return;
            }

            var blockedAt = DateTimeOffset.UtcNow;
            matchedRule.SourceRule.HitCount++;
            matchedRule.SourceRule.LastHitAt = blockedAt;
            matchedRule.MatchRule.HitCount = matchedRule.SourceRule.HitCount;
            matchedRule.MatchRule.LastHitAt = blockedAt;
            popupEvent = new PopupBlockedEvent(matchedRule.MatchRule, window, blockedAt);
        }

        PopupBlocked?.Invoke(this, popupEvent);
    }

    private static bool IsMatch(PopupRule rule, WindowInfo window)
    {
        try
        {
            return PopupRuleMatcher.IsMatch(rule, window);
        }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static RuleSnapshot[] CreateRuleSnapshots(IReadOnlyCollection<PopupRule> popupRules)
    {
        ArgumentNullException.ThrowIfNull(popupRules);

        return popupRules
            .Where(rule => rule.IsEnabled && PopupRuleValidator.Validate(rule).Succeeded)
            .Select(rule => new RuleSnapshot(rule, new PopupRule
            {
                Id = rule.Id,
                Name = rule.Name,
                ProcessName = rule.ProcessName,
                ProcessPath = rule.ProcessPath,
                WindowClass = rule.WindowClass,
                TitlePattern = rule.TitlePattern,
                TitleMatchMode = rule.TitleMatchMode,
                Action = rule.Action,
                IsEnabled = true,
                HitCount = rule.HitCount,
                LastHitAt = rule.LastHitAt,
                CreatedAt = rule.CreatedAt
            }))
            .ToArray();
    }

    private sealed record RuleSnapshot(PopupRule SourceRule, PopupRule MatchRule);
}
