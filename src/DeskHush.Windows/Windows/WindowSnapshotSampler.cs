using DeskHush.Core.Models;

namespace DeskHush.Windows.Windows;

internal static class WindowSnapshotSampler
{
    internal static readonly int[] RetryOffsetsMilliseconds = [0, 40, 120, 300];

    internal static async ValueTask<WindowInfo?> CaptureAsync(
        nint windowHandle,
        Func<nint, WindowInfo?> resolver,
        Func<int, CancellationToken, ValueTask> waitUntilOffsetAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(waitUntilOffsetAsync);

        WindowInfo? bestSnapshot = null;
        string? previousTitle = null;

        foreach (var offset in RetryOffsetsMilliseconds)
        {
            await waitUntilOffsetAsync(offset, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            WindowInfo? snapshot;
            try
            {
                snapshot = resolver(windowHandle);
            }
            catch (Exception)
            {
                snapshot = null;
            }

            if (snapshot is null)
            {
                previousTitle = null;
                continue;
            }

            if (bestSnapshot is not null && bestSnapshot.ProcessId != snapshot.ProcessId)
            {
                bestSnapshot = null;
                previousTitle = null;
            }

            bestSnapshot = Merge(bestSnapshot, snapshot);
            var currentTitle = NormalizeTitle(snapshot.Title);
            if (currentTitle is not null && string.Equals(previousTitle, currentTitle, StringComparison.Ordinal))
            {
                return bestSnapshot;
            }

            previousTitle = currentTitle;
        }

        return bestSnapshot;
    }

    private static WindowInfo Merge(WindowInfo? previous, WindowInfo current)
    {
        if (previous is null)
        {
            return current;
        }

        var hasCurrentBounds = current.Width > 0 && current.Height > 0;
        return new WindowInfo(
            current.Handle,
            current.ProcessId > 0 ? current.ProcessId : previous.ProcessId,
            PreferCurrent(current.ProcessName, previous.ProcessName),
            PreferCurrent(current.ProcessPath, previous.ProcessPath),
            PreferCurrent(current.Title, previous.Title),
            PreferCurrent(current.ClassName, previous.ClassName),
            hasCurrentBounds ? current.Left : previous.Left,
            hasCurrentBounds ? current.Top : previous.Top,
            hasCurrentBounds ? current.Width : previous.Width,
            hasCurrentBounds ? current.Height : previous.Height);
    }

    private static string PreferCurrent(string current, string previous) =>
        string.IsNullOrWhiteSpace(current) ? previous : current;

    private static string? NormalizeTitle(string title)
    {
        var normalized = title.Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
