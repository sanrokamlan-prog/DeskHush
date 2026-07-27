using System.Text.Json;
using DeskHush.Core.Interfaces;
using DeskHush.Core.Models;

namespace DeskHush.Core.Services;

public sealed class JsonSettingsStore(string filePath) : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SemaphoreSlim fileGate = new(1, 1);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(filePath))
            {
                return new AppSettings();
            }

            try
            {
                await using var stream = File.OpenRead(filePath);
                var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
                               ?? new AppSettings();
                settings.PopupRules ??= [];
                settings.Language = string.IsNullOrWhiteSpace(settings.Language) ? "zh-CN" : settings.Language;
                return settings;
            }
            catch (JsonException)
            {
                var backupPath = $"{filePath}.invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
                File.Move(filePath, backupPath, overwrite: false);
                return new AppSettings();
            }
        }
        finally
        {
            fileGate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            fileGate.Release();
        }
    }
}
