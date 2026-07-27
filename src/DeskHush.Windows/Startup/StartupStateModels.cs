using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace DeskHush.Windows.Startup;

internal sealed class StartupStateDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<RegistryStartupBackup> RegistryEntries { get; set; } = [];

    public List<StartupFolderBackup> StartupFolderEntries { get; set; } = [];

    public List<StartupApprovalOverrideBackup> ApprovalOverrides { get; set; } = [];

    [JsonIgnore]
    public string? SourceFingerprint { get; set; }
}

internal sealed class RegistryStartupBackup
{
    public required string Id { get; set; }

    public required RegistryHive Hive { get; set; }

    public required RegistryView View { get; set; }

    public required string SubKeyPath { get; set; }

    public required string ValueName { get; set; }

    public required RegistryValueSnapshot Value { get; set; }

    public StartupApprovalBackup? Approval { get; set; }
}

internal sealed class StartupFolderBackup
{
    public required string Id { get; set; }

    public required Core.Models.EntryScope Scope { get; set; }

    public required string OriginalPath { get; set; }

    public required string DisabledPath { get; set; }

    public required bool IsDirectory { get; set; }

    public required StartupApprovalBackup Approval { get; set; }
}

internal sealed class StartupApprovalOverrideBackup
{
    public required string Id { get; set; }

    public required Core.Models.StartupEntryKind Kind { get; set; }

    public required StartupApprovalBackup OriginalApproval { get; set; }

    public required RegistryValueSnapshot AppliedValue { get; set; }
}

internal sealed class StartupApprovalBackup
{
    public required RegistryHive Hive { get; set; }

    public required RegistryView View { get; set; }

    public required string SubKeyPath { get; set; }

    public required string ValueName { get; set; }

    public RegistryValueSnapshot? Value { get; set; }
}

internal sealed class RegistryValueSnapshot
{
    public required RegistryValueKind Kind { get; set; }

    public string? Text { get; set; }

    public string[]? MultiText { get; set; }

    public string? BytesBase64 { get; set; }

    public int? DWord { get; set; }

    public long? QWord { get; set; }

    public static RegistryValueSnapshot Capture(RegistryKey key, string valueName)
    {
        ArgumentNullException.ThrowIfNull(key);

        var kind = key.GetValueKind(valueName);
        var value = key.GetValue(
            valueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);

        return kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString when value is string text =>
                new RegistryValueSnapshot { Kind = kind, Text = text },
            RegistryValueKind.MultiString when value is string[] multiText =>
                new RegistryValueSnapshot { Kind = kind, MultiText = multiText.ToArray() },
            RegistryValueKind.Binary or RegistryValueKind.None when value is byte[] bytes =>
                new RegistryValueSnapshot { Kind = kind, BytesBase64 = Convert.ToBase64String(bytes) },
            RegistryValueKind.DWord when value is int dword =>
                new RegistryValueSnapshot { Kind = kind, DWord = dword },
            RegistryValueKind.QWord when value is long qword =>
                new RegistryValueSnapshot { Kind = kind, QWord = qword },
            _ => throw new InvalidDataException(
                $"Registry value '{valueName}' has unsupported kind or data ({kind}).")
        };
    }

    public void Validate()
    {
        var isValid = Kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => Text is not null,
            RegistryValueKind.MultiString => MultiText is not null && MultiText.All(item => item is not null),
            RegistryValueKind.Binary or RegistryValueKind.None => IsValidBase64(BytesBase64),
            RegistryValueKind.DWord => DWord.HasValue,
            RegistryValueKind.QWord => QWord.HasValue,
            _ => false
        };

        if (!isValid)
        {
            throw new InvalidDataException($"The registry backup contains invalid data for value kind '{Kind}'.");
        }
    }

    public void Restore(RegistryKey key, string valueName)
    {
        ArgumentNullException.ThrowIfNull(key);
        Validate();
        key.SetValue(valueName, GetRawValue(), Kind);
    }

    public bool ContentEquals(RegistryValueSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString =>
                string.Equals(Text, other.Text, StringComparison.Ordinal),
            RegistryValueKind.MultiString =>
                MultiText is not null && other.MultiText is not null && MultiText.SequenceEqual(other.MultiText),
            RegistryValueKind.Binary or RegistryValueKind.None =>
                string.Equals(BytesBase64, other.BytesBase64, StringComparison.Ordinal),
            RegistryValueKind.DWord => DWord == other.DWord,
            RegistryValueKind.QWord => QWord == other.QWord,
            _ => false
        };
    }

    public string ToDisplayString()
    {
        return Kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => Text ?? string.Empty,
            RegistryValueKind.MultiString => string.Join(" ", MultiText ?? []),
            RegistryValueKind.DWord => DWord?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            RegistryValueKind.QWord => QWord?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            RegistryValueKind.Binary or RegistryValueKind.None =>
                $"[{GetByteCount()} bytes, {Kind}]",
            _ => $"[{Kind}]"
        };
    }

    private object GetRawValue()
    {
        return Kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => Text!,
            RegistryValueKind.MultiString => MultiText!.ToArray(),
            RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(BytesBase64!),
            RegistryValueKind.DWord => DWord!.Value,
            RegistryValueKind.QWord => QWord!.Value,
            _ => throw new InvalidDataException($"Unsupported registry value kind '{Kind}'.")
        };
    }

    private int GetByteCount()
    {
        try
        {
            return BytesBase64 is null ? 0 : Convert.FromBase64String(BytesBase64).Length;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static bool IsValidBase64(string? value)
    {
        if (value is null)
        {
            return false;
        }

        try
        {
            _ = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
