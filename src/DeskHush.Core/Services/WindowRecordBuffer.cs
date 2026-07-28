using DeskHush.Core.Models;

namespace DeskHush.Core.Services;

public sealed class WindowRecordBuffer
{
    public const int DefaultCapacity = 500;

    public static readonly TimeSpan DefaultDeduplicationInterval = TimeSpan.FromSeconds(1);

    private readonly object stateLock = new();
    private readonly LinkedList<WindowRecord> records = new();
    private readonly Dictionary<nint, DateTimeOffset> recentlyRecorded = new();
    private readonly int capacity;
    private readonly TimeSpan deduplicationInterval;

    public WindowRecordBuffer(
        int capacity = DefaultCapacity,
        TimeSpan? deduplicationInterval = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "记录容量必须大于 0。");
        }

        var interval = deduplicationInterval ?? DefaultDeduplicationInterval;
        if (interval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(deduplicationInterval), "去重间隔不能小于 0。");
        }

        this.capacity = capacity;
        this.deduplicationInterval = interval;
    }

    public IReadOnlyList<WindowRecord> Records
    {
        get
        {
            lock (stateLock)
            {
                return records.ToArray();
            }
        }
    }

    public WindowRecord? TryAdd(WindowInfo window, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(window);

        lock (stateLock)
        {
            if (recentlyRecorded.TryGetValue(window.Handle, out var previous) &&
                recordedAt >= previous &&
                recordedAt - previous < deduplicationInterval)
            {
                return null;
            }

            recentlyRecorded[window.Handle] = recordedAt;
            var record = new WindowRecord(Guid.NewGuid(), window, recordedAt);
            records.AddFirst(record);

            while (records.Count > capacity)
            {
                records.RemoveLast();
            }

            TrimDeduplicationIndex(recordedAt);
            return record;
        }
    }

    public void Clear()
    {
        lock (stateLock)
        {
            records.Clear();
            recentlyRecorded.Clear();
        }
    }

    private void TrimDeduplicationIndex(DateTimeOffset now)
    {
        if (recentlyRecorded.Count <= Math.Max(capacity * 2, 128))
        {
            return;
        }

        var cutoff = now - deduplicationInterval - TimeSpan.FromSeconds(1);
        foreach (var staleHandle in recentlyRecorded
                     .Where(item => item.Value < cutoff)
                     .Select(item => item.Key)
                     .ToArray())
        {
            recentlyRecorded.Remove(staleHandle);
        }
    }
}
