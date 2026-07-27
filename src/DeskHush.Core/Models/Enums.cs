namespace DeskHush.Core.Models;

public enum PopupAction
{
    Close,
    Hide
}

public enum TextMatchMode
{
    Contains,
    Exact,
    Wildcard,
    Regex
}

public enum ContextMenuKind
{
    StaticVerb,
    ShellExtension
}

public enum ContextMenuLocation
{
    File,
    Folder,
    FolderBackground,
    Drive,
    Desktop,
    AllObjects
}

public enum EntryScope
{
    CurrentUser,
    LocalMachine
}

public enum StartupEntryKind
{
    RegistryRun,
    StartupFolder,
    ScheduledTask
}
