namespace DeskHush.App.ViewModels;

public sealed record ActivityItem(DateTimeOffset Timestamp, string Title, string Detail)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss");
}
