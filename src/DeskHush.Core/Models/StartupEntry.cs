namespace DeskHush.Core.Models;

public sealed class StartupEntry
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Command { get; init; }

    public required string SourcePath { get; init; }

    public required StartupEntryKind Kind { get; init; }

    public required EntryScope Scope { get; init; }

    public string Publisher { get; init; } = string.Empty;

    public bool IsEnabled { get; set; }

    public bool RequiresElevation { get; init; }
}
