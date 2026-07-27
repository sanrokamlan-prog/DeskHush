namespace DeskHush.Core.Models;

public sealed class PopupRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string ProcessName { get; set; } = string.Empty;

    public string ProcessPath { get; set; } = string.Empty;

    public string WindowClass { get; set; } = string.Empty;

    public string TitlePattern { get; set; } = string.Empty;

    public TextMatchMode TitleMatchMode { get; set; } = TextMatchMode.Contains;

    public PopupAction Action { get; set; } = PopupAction.Close;

    public bool IsEnabled { get; set; } = true;

    public long HitCount { get; set; }

    public DateTimeOffset? LastHitAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
