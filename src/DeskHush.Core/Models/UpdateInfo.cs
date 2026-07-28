namespace DeskHush.Core.Models;

public sealed record UpdateInfo(
    Version Version,
    string TagName,
    Uri ReleaseUri);
