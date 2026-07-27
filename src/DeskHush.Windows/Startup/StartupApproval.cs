using Microsoft.Win32;

namespace DeskHush.Windows.Startup;

internal enum StartupApprovalStatus
{
    NotPresent,
    Enabled,
    Disabled,
    Invalid
}

internal static class StartupApproval
{
    internal const string BaseSubKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    internal const string RunSubKeyName = "Run";
    internal const string Run32SubKeyName = "Run32";
    internal const string StartupFolderSubKeyName = "StartupFolder";

    public static StartupApprovalStatus GetStatus(RegistryValueSnapshot? value)
    {
        if (value is null)
        {
            return StartupApprovalStatus.NotPresent;
        }

        if (value.Kind != RegistryValueKind.Binary || string.IsNullOrWhiteSpace(value.BytesBase64))
        {
            return StartupApprovalStatus.Invalid;
        }

        try
        {
            var bytes = Convert.FromBase64String(value.BytesBase64);
            if (bytes.Length == 0)
            {
                return StartupApprovalStatus.Invalid;
            }

            return (bytes[0] & 1) == 0
                ? StartupApprovalStatus.Enabled
                : StartupApprovalStatus.Disabled;
        }
        catch (FormatException)
        {
            return StartupApprovalStatus.Invalid;
        }
    }

    public static bool AllowsStartup(RegistryValueSnapshot? value)
    {
        return GetStatus(value) is StartupApprovalStatus.NotPresent or StartupApprovalStatus.Enabled;
    }

    public static RegistryValueSnapshot CreateEnabledValue(RegistryValueSnapshot? originalValue = null)
    {
        var bytes = new byte[12];
        bytes[0] = GetPairedEnabledState(originalValue);
        return new RegistryValueSnapshot
        {
            Kind = RegistryValueKind.Binary,
            BytesBase64 = Convert.ToBase64String(bytes)
        };
    }

    public static bool ValuesEqual(RegistryValueSnapshot? left, RegistryValueSnapshot? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.ContentEquals(right);
    }

    public static string GetRunApprovalSubKeyName(
        RegistryHive sourceHive,
        RegistryView sourceView,
        bool is64BitOperatingSystem)
    {
        if (is64BitOperatingSystem &&
            sourceHive == RegistryHive.LocalMachine &&
            sourceView == RegistryView.Registry32)
        {
            return Run32SubKeyName;
        }

        return RunSubKeyName;
    }

    public static RegistryView GetNativeRegistryView(bool is64BitOperatingSystem)
    {
        return is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;
    }

    public static bool HasRegistryApproval(string sourceSubKeyPath)
    {
        return sourceSubKeyPath.EndsWith(@"\Run", StringComparison.OrdinalIgnoreCase);
    }

    private static byte GetPairedEnabledState(RegistryValueSnapshot? originalValue)
    {
        if (originalValue?.Kind != RegistryValueKind.Binary ||
            string.IsNullOrWhiteSpace(originalValue.BytesBase64))
        {
            return 0x02;
        }

        try
        {
            var bytes = Convert.FromBase64String(originalValue.BytesBase64);
            return bytes.FirstOrDefault() switch
            {
                0x07 => 0x06,
                0x09 => 0x08,
                _ => 0x02
            };
        }
        catch (FormatException)
        {
            return 0x02;
        }
    }
}
