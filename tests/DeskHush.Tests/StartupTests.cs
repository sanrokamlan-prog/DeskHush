using System.IO;
using DeskHush.Core.Models;
using DeskHush.Windows.Startup;
using DeskHush.Windows.SystemIntegration;
using Microsoft.Win32;

namespace DeskHush.Tests;

internal static class StartupTests
{
    public static Task TestStartupApprovalSemantics()
    {
        var enabled02 = Binary(0x02);
        var enabled06 = Binary(0x06);
        var enabled08 = Binary(0x08);
        var disabled01 = Binary(0x01);
        var disabled03 = Binary(0x03, 0x2A, 0x7F);
        var disabled07 = Binary(0x07);
        var disabled09 = Binary(0x09);
        var emptyBinary = new RegistryValueSnapshot
        {
            Kind = RegistryValueKind.Binary,
            BytesBase64 = Convert.ToBase64String([])
        };
        var wrongKind = new RegistryValueSnapshot
        {
            Kind = RegistryValueKind.String,
            Text = "03"
        };

        Assert(StartupApproval.GetStatus(null) == StartupApprovalStatus.NotPresent,
            "A missing StartupApproved value should use Windows' default-enabled behavior.");
        Assert(StartupApproval.GetStatus(enabled02) == StartupApprovalStatus.Enabled,
            "State 0x02 should be enabled.");
        Assert(StartupApproval.GetStatus(enabled06) == StartupApprovalStatus.Enabled,
            "State 0x06 should be enabled.");
        Assert(StartupApproval.GetStatus(enabled08) == StartupApprovalStatus.Enabled,
            "State 0x08 should remain enabled even when Task Manager locks the toggle.");
        Assert(StartupApproval.GetStatus(disabled01) == StartupApprovalStatus.Disabled,
            "State 0x01 should be disabled.");
        Assert(StartupApproval.GetStatus(disabled03) == StartupApprovalStatus.Disabled,
            "State 0x03 should be disabled regardless of timestamp bytes.");
        Assert(StartupApproval.GetStatus(disabled07) == StartupApprovalStatus.Disabled,
            "State 0x07 should be disabled.");
        Assert(StartupApproval.GetStatus(disabled09) == StartupApprovalStatus.Disabled,
            "State 0x09 should remain disabled when Task Manager locks the toggle.");
        Assert(!StartupApproval.AllowsStartup(emptyBinary),
            "An empty binary approval value should be treated conservatively as blocked.");
        Assert(!StartupApproval.AllowsStartup(wrongKind),
            "A malformed non-binary approval value should be treated conservatively as blocked.");

        var canonicalEnabled = StartupApproval.CreateEnabledValue();
        var canonicalBytes = Convert.FromBase64String(canonicalEnabled.BytesBase64!);
        Assert(canonicalBytes.Length == 12 && canonicalBytes[0] == 0x02 && canonicalBytes.Skip(1).All(value => value == 0),
            "DeskHush should write the standard 12-byte enabled value.");
        Assert(Convert.FromBase64String(StartupApproval.CreateEnabledValue(disabled07).BytesBase64!)[0] == 0x06,
            "A 0x07 disabled state should retain its 0x06 state family when enabled.");
        Assert(Convert.FromBase64String(StartupApproval.CreateEnabledValue(disabled09).BytesBase64!)[0] == 0x08,
            "A locked 0x09 state should retain its locked 0x08 state family when enabled.");
        Assert(StartupApproval.ValuesEqual(disabled03, Binary(0x03, 0x2A, 0x7F)),
            "Approval comparison should preserve the complete original byte sequence.");
        Assert(!StartupApproval.ValuesEqual(disabled03, Binary(0x03, 0x2A, 0x7E)),
            "Approval comparison should detect timestamp-byte changes.");
        return Task.CompletedTask;
    }

    public static Task TestStartupApprovalMapping()
    {
        Assert(
            StartupApproval.GetRunApprovalSubKeyName(
                RegistryHive.LocalMachine,
                RegistryView.Registry64,
                is64BitOperatingSystem: true) == StartupApproval.RunSubKeyName,
            "64-bit HKLM Run should map to StartupApproved\\Run.");
        Assert(
            StartupApproval.GetRunApprovalSubKeyName(
                RegistryHive.LocalMachine,
                RegistryView.Registry32,
                is64BitOperatingSystem: true) == StartupApproval.Run32SubKeyName,
            "32-bit HKLM Run should map to StartupApproved\\Run32.");
        Assert(
            StartupApproval.GetRunApprovalSubKeyName(
                RegistryHive.CurrentUser,
                RegistryView.Registry32,
                is64BitOperatingSystem: true) == StartupApproval.RunSubKeyName,
            "HKCU Software is shared and should map to StartupApproved\\Run.");
        Assert(
            StartupApproval.GetRunApprovalSubKeyName(
                RegistryHive.LocalMachine,
                RegistryView.Registry32,
                is64BitOperatingSystem: false) == StartupApproval.RunSubKeyName,
            "32-bit Windows should use StartupApproved\\Run rather than Run32.");
        Assert(StartupApproval.GetNativeRegistryView(true) == RegistryView.Registry64,
            "64-bit Windows should read StartupApproved through the native view.");
        Assert(StartupApproval.GetNativeRegistryView(false) == RegistryView.Registry32,
            "32-bit Windows should read StartupApproved through the 32-bit view.");
        Assert(StartupApproval.HasRegistryApproval(@"Software\Microsoft\Windows\CurrentVersion\Run"),
            "Persistent Run entries should use StartupApproved.");
        Assert(!StartupApproval.HasRegistryApproval(@"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
            "RunOnce must not alias StartupApproved\\Run because same-name values can be unrelated.");
        return Task.CompletedTask;
    }

    public static Task TestSelfStartupApprovalSemantics()
    {
        Assert(!StartupRegistrationService.IsDisabledApprovalValue(null),
            "A missing self-start approval value should use the default-enabled behavior.");
        Assert(!StartupRegistrationService.IsDisabledApprovalValue([0x02]),
            "An even self-start approval status should be enabled.");
        Assert(StartupRegistrationService.IsDisabledApprovalValue([0x03]),
            "The standard disabled self-start status should be detected.");
        Assert(StartupRegistrationService.IsDisabledApprovalValue([0x09]),
            "All odd self-start approval status codes should be treated as disabled.");
        return Task.CompletedTask;
    }

    public static Task TestStartupApprovalStateRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeskHush.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var original = Binary(0x03, 0x00, 0x00, 0x00, 0x4A, 0x25, 0x90, 0xD1);
            var store = new StartupStateStore(root);
            var state = store.Load();
            state.ApprovalOverrides.Add(new StartupApprovalOverrideBackup
            {
                Id = "approval-round-trip",
                Kind = StartupEntryKind.RegistryRun,
                OriginalApproval = new StartupApprovalBackup
                {
                    Hive = RegistryHive.CurrentUser,
                    View = StartupApproval.GetNativeRegistryView(Environment.Is64BitOperatingSystem),
                    SubKeyPath = $@"{StartupApproval.BaseSubKeyPath}\{StartupApproval.RunSubKeyName}",
                    ValueName = "DeskHush Test",
                    Value = original
                },
                AppliedValue = StartupApproval.CreateEnabledValue(original)
            });
            state.StartupFolderEntries.Add(new StartupFolderBackup
            {
                Id = "missing-approval-round-trip",
                Scope = EntryScope.CurrentUser,
                OriginalPath = Path.Combine(root, "original.lnk"),
                DisabledPath = Path.Combine(root, "disabled.lnk"),
                IsDirectory = false,
                Approval = new StartupApprovalBackup
                {
                    Hive = RegistryHive.CurrentUser,
                    View = StartupApproval.GetNativeRegistryView(Environment.Is64BitOperatingSystem),
                    SubKeyPath = $@"{StartupApproval.BaseSubKeyPath}\{StartupApproval.StartupFolderSubKeyName}",
                    ValueName = "original.lnk",
                    Value = null
                }
            });

            store.Save(state);
            var loaded = store.Load();
            var restored = loaded.ApprovalOverrides.Single();
            Assert(restored.OriginalApproval.Value is not null &&
                   StartupApproval.ValuesEqual(restored.OriginalApproval.Value, original),
                "The complete original StartupApproved byte sequence should round-trip through JSON.");
            Assert(restored.OriginalApproval.Value!.Kind == RegistryValueKind.Binary,
                "The original StartupApproved registry kind should round-trip through JSON.");
            Assert(restored.OriginalApproval.Value.BytesBase64 == original.BytesBase64,
                "StartupApproved timestamp bytes must not be normalized in recovery state.");
            Assert(loaded.StartupFolderEntries.Single().Approval.Value is null,
                "A missing StartupApproved value must round-trip as missing rather than as zero bytes.");
            return Task.CompletedTask;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static RegistryValueSnapshot Binary(params byte[] bytes)
    {
        return new RegistryValueSnapshot
        {
            Kind = RegistryValueKind.Binary,
            BytesBase64 = Convert.ToBase64String(bytes)
        };
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
