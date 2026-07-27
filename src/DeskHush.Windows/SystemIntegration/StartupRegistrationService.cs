using Microsoft.Win32;

namespace DeskHush.Windows.SystemIntegration;

public sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "DeskHush";

    public bool IsEnabled(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = runKey?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (!string.Equals(value, BuildCommand(executablePath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, writable: false);
        return !IsDisabledApprovalValue(approvedKey?.GetValue(ValueName) as byte[]);
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        using var approvedKey = Registry.CurrentUser.CreateSubKey(StartupApprovedPath, writable: true)
                                ?? throw new InvalidOperationException("无法打开当前用户启动批准状态注册表。");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("无法打开当前用户启动项注册表。");

        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(executablePath), RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        approvedKey.DeleteValue(ValueName, throwOnMissingValue: false);

        if (IsEnabled(executablePath) != enabled)
        {
            throw new IOException("Windows 未保存预期的 DeskHush 开机启动状态。");
        }
    }

    private static string BuildCommand(string executablePath) =>
        $"\"{Path.GetFullPath(executablePath)}\" --background";

    internal static bool IsDisabledApprovalValue(byte[]? value) =>
        value is { Length: > 0 } && (value[0] & 1) != 0;
}
