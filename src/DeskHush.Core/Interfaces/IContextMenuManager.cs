using DeskHush.Core.Models;

namespace DeskHush.Core.Interfaces;

public interface IContextMenuManager
{
    Task<IReadOnlyList<ContextMenuEntry>> GetEntriesAsync(CancellationToken cancellationToken = default);

    Task<OperationResult> SetEnabledAsync(ContextMenuEntry entry, bool enabled, CancellationToken cancellationToken = default);
}
