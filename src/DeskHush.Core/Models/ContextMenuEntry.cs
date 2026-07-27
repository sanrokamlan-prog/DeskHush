namespace DeskHush.Core.Models;

public sealed class ContextMenuEntry
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string RegistryPath { get; init; }

    public required ContextMenuKind Kind { get; init; }

    public required ContextMenuLocation Location { get; init; }

    public required EntryScope Scope { get; init; }

    public string Command { get; init; } = string.Empty;

    public string HandlerClsid { get; init; } = string.Empty;

    public string Publisher { get; init; } = string.Empty;

    public bool IsEnabled { get; set; }

    public bool RequiresElevation { get; init; }
}
