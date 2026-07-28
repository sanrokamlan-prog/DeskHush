using DeskHush.Core.Models;

namespace DeskHush.Core.Interfaces;

public interface IWindowRecorder : IDisposable
{
    bool IsRunning { get; }

    IReadOnlyList<WindowRecord> Records { get; }

    event EventHandler<WindowRecord>? WindowRecorded;

    void Start();

    void Stop();

    void Clear();
}
