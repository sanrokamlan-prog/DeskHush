namespace DeskHush.Core.Models;

public sealed class AppSettings
{
    public bool PopupBlockingEnabled { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public string Language { get; set; } = "zh-CN";

    public List<PopupRule> PopupRules { get; set; } = [];
}
