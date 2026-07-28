using DeskHush.Core.Models;

namespace DeskHush.Core.Interfaces;

public interface IUpdateChecker
{
    Task<UpdateInfo?> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default);
}
