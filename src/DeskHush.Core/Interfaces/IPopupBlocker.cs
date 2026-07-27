using DeskHush.Core.Models;

namespace DeskHush.Core.Interfaces;

public interface IPopupBlocker : IDisposable
{
    bool IsRunning { get; }

    event EventHandler<PopupBlockedEvent>? PopupBlocked;

    void Start(IReadOnlyCollection<PopupRule> rules);

    void UpdateRules(IReadOnlyCollection<PopupRule> rules);

    void Stop();
}
