namespace DeskHush.Core.Models;

public sealed record WindowInfo(
    nint Handle,
    int ProcessId,
    string ProcessName,
    string ProcessPath,
    string Title,
    string ClassName,
    int Left,
    int Top,
    int Width,
    int Height)
{
    public string SizeText => $"{Width} x {Height}";

    public string PositionText => $"{Left}, {Top}";
}

public sealed record PopupBlockedEvent(
    PopupRule Rule,
    WindowInfo Window,
    DateTimeOffset BlockedAt);
