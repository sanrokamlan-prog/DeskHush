using System.Security.Cryptography;
using System.Text.Json;

namespace DeskHush.Windows.Startup;

internal sealed class StartupStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public StartupStateStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        StateDirectory = Path.GetFullPath(stateDirectory);
        StateFilePath = Path.Combine(StateDirectory, "startup-state.json");
        DisabledItemsDirectory = Path.Combine(StateDirectory, "disabled-startup");
    }

    public string StateDirectory { get; }

    public string StateFilePath { get; }

    public string DisabledItemsDirectory { get; }

    public StartupStateDocument Load()
    {
        if (!File.Exists(StateFilePath))
        {
            return new StartupStateDocument();
        }

        try
        {
            var bytes = File.ReadAllBytes(StateFilePath);
            var state = JsonSerializer.Deserialize<StartupStateDocument>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The startup state file is empty.");

            ValidateDocument(state);
            state.SourceFingerprint = CreateFingerprint(bytes);
            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The startup state file is invalid and was left untouched: {StateFilePath}",
                exception);
        }
    }

    public void Save(StartupStateDocument state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateDocument(state);

        var currentFingerprint = File.Exists(StateFilePath)
            ? CreateFingerprint(File.ReadAllBytes(StateFilePath))
            : null;

        if (!string.Equals(currentFingerprint, state.SourceFingerprint, StringComparison.Ordinal))
        {
            throw new IOException(
                "The startup state file changed after it was read. It was left untouched to avoid losing another update.");
        }

        Directory.CreateDirectory(StateDirectory);
        var temporaryPath = Path.Combine(StateDirectory, $".startup-state.{Guid.NewGuid():N}.tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, StateFilePath, overwrite: true);
            state.SourceFingerprint = CreateFingerprint(bytes);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A failed cleanup must not hide the state write result.
            }
            catch (UnauthorizedAccessException)
            {
                // A failed cleanup must not hide the state write result.
            }
        }
    }

    private static string CreateFingerprint(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void ValidateDocument(StartupStateDocument state)
    {
        if (state.SchemaVersion != StartupStateDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported startup state schema version '{state.SchemaVersion}'.");
        }

        if (state.RegistryEntries is null || state.StartupFolderEntries is null || state.ApprovalOverrides is null)
        {
            throw new InvalidDataException("The startup state file is missing required collections.");
        }

        if (state.RegistryEntries.Any(item => item is null) ||
            state.StartupFolderEntries.Any(item => item is null) ||
            state.ApprovalOverrides.Any(item => item is null))
        {
            throw new InvalidDataException("The startup state file contains an empty backup record.");
        }

        if (state.RegistryEntries.Any(item => string.IsNullOrWhiteSpace(item.Id)) ||
            state.StartupFolderEntries.Any(item => string.IsNullOrWhiteSpace(item.Id)) ||
            state.ApprovalOverrides.Any(item => string.IsNullOrWhiteSpace(item.Id)))
        {
            throw new InvalidDataException("The startup state file contains a backup record without an id.");
        }

        var duplicateId = state.RegistryEntries.Select(item => item.Id)
            .Concat(state.StartupFolderEntries.Select(item => item.Id))
            .Concat(state.ApprovalOverrides.Select(item => item.Id))
            .GroupBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateId is not null)
        {
            throw new InvalidDataException($"The startup state file contains duplicate id '{duplicateId.Key}'.");
        }
    }
}
