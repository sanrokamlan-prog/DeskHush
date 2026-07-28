namespace DeskHush.Core.Models;

public sealed record WindowRecord(
    Guid Id,
    WindowInfo Window,
    DateTimeOffset RecordedAt)
{
    public string ProcessName => Window.ProcessName;

    public string Title => Window.Title;

    public string ClassName => Window.ClassName;

    public string SizeText => $"{Window.Width} x {Window.Height}";

    public string PositionText => $"{Window.Left}, {Window.Top}";

    public string TimeText => RecordedAt.ToLocalTime().ToString("HH:mm:ss");
}
