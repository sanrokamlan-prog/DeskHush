using DeskHush.Core.Models;

namespace DeskHush.Core.Interfaces;

public interface IStartupManager
{
    Task<IReadOnlyList<StartupEntry>> GetEntriesAsync(CancellationToken cancellationToken = default);

    Task<OperationResult> SetEnabledAsync(StartupEntry entry, bool enabled, CancellationToken cancellationToken = default);
}
