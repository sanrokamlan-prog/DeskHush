using System.Security;
using System.Security.Principal;
using DeskHush.Core.Interfaces;
using DeskHush.Core.Models;
using DeskHush.Core.Services;
using Microsoft.Win32;

namespace DeskHush.Windows.Startup;

/// <summary>
/// Enumerates and safely toggles Windows Run keys and Startup-folder entries.
/// </summary>
public sealed class WindowsStartupManager : IStartupManager, IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    private readonly StartupStateStore _stateStore;
    private readonly IReadOnlyList<StartupFolderLocation> _startupFolders;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public WindowsStartupManager(string stateDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows startup management is only available on Windows.");
        }

        _stateStore = new StartupStateStore(stateDirectory);
        _startupFolders = CreateStartupFolderLocations();

        foreach (var location in _startupFolders)
        {
            if (IsSamePathOrChild(_stateStore.StateDirectory, location.Path))
            {
                throw new ArgumentException(
                    "The state directory cannot be inside a Windows Startup folder.",
                    nameof(stateDirectory));
            }
        }
    }

    public async Task<IReadOnlyList<StartupEntry>> GetEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await Task.Run(
                    () => GetEntriesCore(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> SetEnabledAsync(
        StartupEntry entry,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (entry is null)
        {
            return OperationResult.Failure("Startup entry cannot be null.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await Task.Run(
                    () => SetEnabledCore(entry, enabled, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            return PermissionFailure(entry, exception.Message);
        }
        catch (SecurityException exception)
        {
            return PermissionFailure(entry, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            return OperationResult.Failure($"Startup operation failed: {exception.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private IReadOnlyList<StartupEntry> GetEntriesCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _stateStore.Load();
        ValidateStateRecords(state);

        var entries = new Dictionary<string, StartupEntry>(StringComparer.Ordinal);

        foreach (var location in CreateRegistryLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
            using var key = baseKey.OpenSubKey(location.SubKeyPath, writable: false);
            if (key is null)
            {
                continue;
            }

            foreach (var valueName in key.GetValueNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = RegistryValueSnapshot.Capture(key, valueName);
                var approval = CaptureRegistryApproval(location, valueName);
                var entry = CreateRegistryEntry(
                    location,
                    valueName,
                    value,
                    isEnabled: approval is null || StartupApproval.AllowsStartup(approval.Value));
                entries.TryAdd(entry.Id, entry);
            }
        }

        foreach (var location in _startupFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(location.Path))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(location.Path, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsDesktopIni(path))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(path);
                var approval = CaptureStartupFolderApproval(location, fullPath);
                var entry = CreateStartupFolderEntry(
                    location,
                    fullPath,
                    isEnabled: StartupApproval.AllowsStartup(approval.Value));
                entries.TryAdd(entry.Id, entry);
            }
        }

        foreach (var backup in state.RegistryEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.ContainsKey(backup.Id))
            {
                var location = ToRegistryLocation(backup);
                entries.Add(
                    backup.Id,
                    CreateRegistryEntry(location, backup.ValueName, backup.Value, isEnabled: false));
            }
        }

        foreach (var backup in state.StartupFolderEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.ContainsKey(backup.Id))
            {
                var location = GetStartupFolderLocation(backup.Scope);
                entries.Add(
                    backup.Id,
                    CreateStartupFolderEntry(location, backup.OriginalPath, isEnabled: false));
            }
        }

        return entries.Values
            .OrderBy(entry => entry.Scope)
            .ThenBy(entry => entry.Kind)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private OperationResult SetEnabledCore(
        StartupEntry entry,
        bool enabled,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            return OperationResult.Failure("Startup entry id cannot be empty.");
        }

        var state = _stateStore.Load();
        ValidateStateRecords(state);

        return entry.Kind switch
        {
            StartupEntryKind.RegistryRun => enabled
                ? EnableRegistryEntry(entry, state, cancellationToken)
                : DisableRegistryEntry(entry, state, cancellationToken),
            StartupEntryKind.StartupFolder => enabled
                ? EnableStartupFolderEntry(entry, state, cancellationToken)
                : DisableStartupFolderEntry(entry, state, cancellationToken),
            _ => OperationResult.Failure("This startup entry type is not managed by WindowsStartupManager.")
        };
    }

    private OperationResult DisableRegistryEntry(
        StartupEntry entry,
        StartupStateDocument state,
        CancellationToken cancellationToken)
    {
        var existingBackup = state.RegistryEntries.SingleOrDefault(item => item.Id == entry.Id);
        var approvalOverride = state.ApprovalOverrides.SingleOrDefault(item => item.Id == entry.Id);
        var liveValue = FindLiveRegistryValue(entry.Id, cancellationToken);

        if (liveValue is null)
        {
            if (approvalOverride is not null)
            {
                return OperationResult.Failure(
                    "The registry source for a saved StartupApproved override is missing. " +
                    "The recovery record was retained.");
            }

            if (existingBackup is not null)
            {
                entry.IsEnabled = false;
                return OperationResult.Success("The registry startup entry is already disabled.");
            }

            return OperationResult.Failure("The registry startup entry no longer exists.");
        }

        if (existingBackup is not null)
        {
            return OperationResult.Failure(
                "A backup already exists while the registry value is still present. " +
                "The entry was not changed to avoid overwriting recovery data.",
                liveValue.Location.RequiresElevation);
        }

        var approval = CaptureRegistryApproval(liveValue.Location, liveValue.ValueName);
        if (approvalOverride is not null)
        {
            if (approval is null)
            {
                return OperationResult.Failure(
                    "RunOnce entries do not have a StartupApproved value; the unexpected recovery record was retained.");
            }

            return RestoreApprovalOverride(
                entry,
                state,
                approvalOverride,
                approval,
                liveValue.Location.RequiresElevation);
        }

        if (approval is not null && !StartupApproval.AllowsStartup(approval.Value))
        {
            entry.IsEnabled = false;
            return OperationResult.Success("The registry startup entry is already disabled by StartupApproved.");
        }

        var elevationFailure = RequireElevationIfNeeded(liveValue.Location.RequiresElevation);
        if (elevationFailure is not null)
        {
            return elevationFailure;
        }

        using var baseKey = RegistryKey.OpenBaseKey(liveValue.Location.Hive, liveValue.Location.View);
        using var key = baseKey.OpenSubKey(liveValue.Location.SubKeyPath, writable: true);
        if (key is null)
        {
            return OperationResult.Failure("The registry startup location no longer exists.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = RegistryValueSnapshot.Capture(key, liveValue.ValueName);
        var backup = new RegistryStartupBackup
        {
            Id = entry.Id,
            Hive = liveValue.Location.Hive,
            View = liveValue.Location.View,
            SubKeyPath = liveValue.Location.SubKeyPath,
            ValueName = liveValue.ValueName,
            Value = snapshot,
            Approval = approval
        };

        state.RegistryEntries.Add(backup);
        _stateStore.Save(state);

        try
        {
            var currentValue = RegistryValueSnapshot.Capture(key, liveValue.ValueName);
            if (!snapshot.ContentEquals(currentValue))
            {
                var cleanupNote = RollBackRegistryBackup(state, backup);
                return OperationResult.Failure(
                    "The registry value changed while it was being backed up; it was left enabled." + cleanupNote);
            }

            if (approval is not null && !ApprovalMatchesCurrent(approval))
            {
                var cleanupNote = RollBackRegistryBackup(state, backup);
                return OperationResult.Failure(
                    "StartupApproved changed while the registry value was being backed up; " +
                    "the entry was left enabled." + cleanupNote);
            }

            key.DeleteValue(liveValue.ValueName, throwOnMissingValue: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            var valueStillExists = TryRegistryValueExists(key, liveValue.ValueName);
            if (valueStillExists is not true)
            {
                entry.IsEnabled = valueStillExists is null;
                var status = valueStillExists is false
                    ? "The registry value is no longer present, so its backup was retained."
                    : "The registry value status could not be verified, so its backup was retained.";
                return OperationResult.Failure(
                    $"The registry operation reported an error: {exception.Message} {status}",
                    liveValue.Location.RequiresElevation);
            }

            var cleanupNote = RollBackRegistryBackup(state, backup);
            return OperationResult.Failure(
                $"The registry value could not be disabled and was left unchanged: {exception.Message}{cleanupNote}",
                liveValue.Location.RequiresElevation);
        }

        entry.IsEnabled = false;
        return OperationResult.Success("The registry startup entry was disabled and backed up.");
    }

    private OperationResult EnableRegistryEntry(
        StartupEntry entry,
        StartupStateDocument state,
        CancellationToken cancellationToken)
    {
        var backup = state.RegistryEntries.SingleOrDefault(item => item.Id == entry.Id);
        if (backup is null)
        {
            var liveValue = FindLiveRegistryValue(entry.Id, cancellationToken);
            if (liveValue is not null)
            {
                var approval = CaptureRegistryApproval(liveValue.Location, liveValue.ValueName);
                if (approval is null || StartupApproval.AllowsStartup(approval.Value))
                {
                    entry.IsEnabled = true;
                    return OperationResult.Success("The registry startup entry is already enabled.");
                }

                return ApplyEnabledApprovalOverride(
                    entry,
                    state,
                    approval,
                    StartupEntryKind.RegistryRun,
                    liveValue.Location.RequiresElevation);
            }

            return OperationResult.Failure("No registry backup exists for this startup entry.");
        }

        ValidateRegistryBackup(backup);
        var location = ToRegistryLocation(backup);
        var elevationFailure = RequireElevationIfNeeded(location.RequiresElevation);
        if (elevationFailure is not null)
        {
            return elevationFailure;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (backup.Approval is not null && !ApprovalMatchesCurrent(backup.Approval))
        {
            return OperationResult.Failure(
                "StartupApproved changed while this entry was disabled. " +
                "The registry source was not restored to avoid overwriting that change.",
                location.RequiresElevation);
        }

        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = baseKey.OpenSubKey(location.SubKeyPath, writable: true)
            ?? baseKey.CreateSubKey(location.SubKeyPath, writable: true);

        if (RegistryValueExists(key, backup.ValueName))
        {
            return OperationResult.Failure(
                "A registry value with the same name already exists. " +
                "It was not overwritten; the DeskHush backup is still available.",
                location.RequiresElevation);
        }

        backup.Value.Restore(key, backup.ValueName);
        var restoredValue = RegistryValueSnapshot.Capture(key, backup.ValueName);
        if (!backup.Value.ContentEquals(restoredValue))
        {
            return OperationResult.Failure(
                "The registry value was written but verification failed. The backup was retained.",
                location.RequiresElevation);
        }

        var restoredApproval = backup.Approval is null ? null : ReadApprovalValue(backup.Approval);
        if (backup.Approval is not null &&
            (!StartupApproval.ValuesEqual(backup.Approval.Value, restoredApproval) ||
             !StartupApproval.AllowsStartup(restoredApproval)))
        {
            entry.IsEnabled = false;
            return OperationResult.Failure(
                "The registry value was restored, but StartupApproved changed or still blocks it. " +
                "The recovery record was retained.",
                location.RequiresElevation);
        }

        state.RegistryEntries.Remove(backup);
        try
        {
            _stateStore.Save(state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            entry.IsEnabled = true;
            return OperationResult.Failure(
                "The registry value was restored, but the recovery-state file could not be updated. " +
                $"The existing backup was retained: {exception.Message}");
        }

        entry.IsEnabled = true;
        return OperationResult.Success("The registry startup entry was restored exactly from backup.");
    }

    private OperationResult DisableStartupFolderEntry(
        StartupEntry entry,
        StartupStateDocument state,
        CancellationToken cancellationToken)
    {
        var existingBackup = state.StartupFolderEntries.SingleOrDefault(item => item.Id == entry.Id);
        var approvalOverride = state.ApprovalOverrides.SingleOrDefault(item => item.Id == entry.Id);
        var liveItem = FindLiveStartupFolderItem(entry.Id, cancellationToken);

        if (liveItem is null)
        {
            if (approvalOverride is not null)
            {
                return OperationResult.Failure(
                    "The Startup-folder source for a saved StartupApproved override is missing. " +
                    "The recovery record was retained.");
            }

            if (existingBackup is not null)
            {
                entry.IsEnabled = false;
                return OperationResult.Success("The Startup-folder item is already disabled.");
            }

            return OperationResult.Failure("The Startup-folder item no longer exists.");
        }

        if (existingBackup is not null)
        {
            return OperationResult.Failure(
                "A disabled-item record already exists while the original item is still present. " +
                "Nothing was moved to avoid overwriting recovery data.",
                liveItem.Location.RequiresElevation);
        }

        var approval = CaptureStartupFolderApproval(liveItem.Location, liveItem.Path);
        if (approvalOverride is not null)
        {
            return RestoreApprovalOverride(
                entry,
                state,
                approvalOverride,
                approval,
                liveItem.Location.RequiresElevation);
        }

        if (!StartupApproval.AllowsStartup(approval.Value))
        {
            entry.IsEnabled = false;
            return OperationResult.Success("The Startup-folder item is already disabled by StartupApproved.");
        }

        var elevationFailure = RequireElevationIfNeeded(liveItem.Location.RequiresElevation);
        if (elevationFailure is not null)
        {
            return elevationFailure;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(liveItem.Path);
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        var containerPath = Path.Combine(_stateStore.DisabledItemsDirectory, entry.Id);
        var disabledPath = Path.Combine(containerPath, Path.GetFileName(liveItem.Path));

        if (FileSystemEntryExists(containerPath) || FileSystemEntryExists(disabledPath))
        {
            return OperationResult.Failure(
                $"The disabled-item destination already exists and was left untouched: {containerPath}");
        }

        var backup = new StartupFolderBackup
        {
            Id = entry.Id,
            Scope = liveItem.Location.Scope,
            OriginalPath = liveItem.Path,
            DisabledPath = disabledPath,
            IsDirectory = isDirectory,
            Approval = approval
        };

        state.StartupFolderEntries.Add(backup);
        _stateStore.Save(state);

        try
        {
            if (!ApprovalMatchesCurrent(approval))
            {
                var cleanupNote = RollBackFolderBackup(state, backup, containerPath);
                return OperationResult.Failure(
                    "StartupApproved changed while the Startup-folder item was being backed up; " +
                    "the item was left enabled." + cleanupNote);
            }

            Directory.CreateDirectory(containerPath);
            MoveFileSystemEntry(liveItem.Path, disabledPath, isDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            var sourceExists = TryFileSystemEntryExists(liveItem.Path);
            var destinationExists = TryFileSystemEntryExists(disabledPath);

            if (sourceExists is not true || destinationExists is not false)
            {
                entry.IsEnabled = sourceExists is not false;
                return OperationResult.Failure(
                    $"The Startup-folder move reported an error and its final state is uncertain: " +
                    $"{exception.Message} The recovery record was retained.",
                    liveItem.Location.RequiresElevation);
            }

            var cleanupNote = RollBackFolderBackup(state, backup, containerPath);
            return OperationResult.Failure(
                $"The Startup-folder item could not be moved and was left enabled: {exception.Message}{cleanupNote}",
                liveItem.Location.RequiresElevation);
        }

        if (FileSystemEntryExists(liveItem.Path) || !FileSystemEntryExists(disabledPath))
        {
            return OperationResult.Failure(
                "The Startup-folder move could not be verified. Recovery state was retained; no overwrite was attempted.",
                liveItem.Location.RequiresElevation);
        }

        entry.IsEnabled = false;
        return OperationResult.Success("The Startup-folder item was moved to DeskHush's disabled-items directory.");
    }

    private OperationResult EnableStartupFolderEntry(
        StartupEntry entry,
        StartupStateDocument state,
        CancellationToken cancellationToken)
    {
        var backup = state.StartupFolderEntries.SingleOrDefault(item => item.Id == entry.Id);
        if (backup is null)
        {
            var liveItem = FindLiveStartupFolderItem(entry.Id, cancellationToken);
            if (liveItem is not null)
            {
                var approval = CaptureStartupFolderApproval(liveItem.Location, liveItem.Path);
                if (StartupApproval.AllowsStartup(approval.Value))
                {
                    entry.IsEnabled = true;
                    return OperationResult.Success("The Startup-folder item is already enabled.");
                }

                return ApplyEnabledApprovalOverride(
                    entry,
                    state,
                    approval,
                    StartupEntryKind.StartupFolder,
                    liveItem.Location.RequiresElevation);
            }

            return OperationResult.Failure("No disabled-item record exists for this Startup-folder item.");
        }

        ValidateStartupFolderBackup(backup);
        var location = GetStartupFolderLocation(backup.Scope);
        var elevationFailure = RequireElevationIfNeeded(location.RequiresElevation);
        if (elevationFailure is not null)
        {
            return elevationFailure;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!ApprovalMatchesCurrent(backup.Approval))
        {
            return OperationResult.Failure(
                "StartupApproved changed while this item was disabled. " +
                "The Startup-folder item was not restored to avoid overwriting that change.",
                location.RequiresElevation);
        }

        if (FileSystemEntryExists(backup.OriginalPath))
        {
            return OperationResult.Failure(
                "An item already exists at the original Startup-folder path. " +
                "It was not overwritten; the disabled item remains available.",
                location.RequiresElevation);
        }

        if (!FileSystemEntryExists(backup.DisabledPath))
        {
            return OperationResult.Failure(
                $"The disabled Startup-folder item is missing: {backup.DisabledPath}",
                location.RequiresElevation);
        }

        var actualIsDirectory = File.GetAttributes(backup.DisabledPath).HasFlag(FileAttributes.Directory);
        if (actualIsDirectory != backup.IsDirectory)
        {
            return OperationResult.Failure(
                "The disabled Startup-folder item type does not match its recovery record; it was left untouched.");
        }

        Directory.CreateDirectory(location.Path);
        MoveFileSystemEntry(backup.DisabledPath, backup.OriginalPath, backup.IsDirectory);

        if (!FileSystemEntryExists(backup.OriginalPath) || FileSystemEntryExists(backup.DisabledPath))
        {
            return OperationResult.Failure(
                "The Startup-folder restore could not be verified. Recovery state was retained.",
                location.RequiresElevation);
        }

        var restoredApproval = ReadApprovalValue(backup.Approval);
        if (!StartupApproval.ValuesEqual(backup.Approval.Value, restoredApproval) ||
            !StartupApproval.AllowsStartup(restoredApproval))
        {
            entry.IsEnabled = false;
            return OperationResult.Failure(
                "The Startup-folder item was restored, but StartupApproved changed or still blocks it. " +
                "The recovery record was retained.",
                location.RequiresElevation);
        }

        state.StartupFolderEntries.Remove(backup);
        try
        {
            _stateStore.Save(state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            entry.IsEnabled = true;
            return OperationResult.Failure(
                "The Startup-folder item was restored, but the recovery-state file could not be updated. " +
                $"The existing record was retained: {exception.Message}");
        }

        TryDeleteEmptyDirectory(Path.GetDirectoryName(backup.DisabledPath));
        entry.IsEnabled = true;
        return OperationResult.Success("The Startup-folder item was restored to its original path.");
    }

    private OperationResult ApplyEnabledApprovalOverride(
        StartupEntry entry,
        StartupStateDocument state,
        StartupApprovalBackup originalApproval,
        StartupEntryKind kind,
        bool requiresElevation)
    {
        var elevationFailure = RequireElevationIfNeeded(requiresElevation);
        if (elevationFailure is not null)
        {
            return elevationFailure;
        }

        var existingOverride = state.ApprovalOverrides.SingleOrDefault(item => item.Id == entry.Id);
        StartupApprovalOverrideBackup approvalOverride;
        var addedStateRecord = false;

        if (existingOverride is not null)
        {
            ValidateApprovalOverride(existingOverride);
            if (existingOverride.Kind != kind ||
                !ApprovalLocationsEqual(existingOverride.OriginalApproval, originalApproval))
            {
                return OperationResult.Failure(
                    "The saved StartupApproved recovery record does not match this startup source.",
                    requiresElevation);
            }

            approvalOverride = existingOverride;
            var current = ReadApprovalValue(originalApproval);
            if (StartupApproval.ValuesEqual(current, approvalOverride.AppliedValue))
            {
                entry.IsEnabled = true;
                return OperationResult.Success("The startup entry is already enabled through StartupApproved.");
            }

            if (!StartupApproval.ValuesEqual(current, approvalOverride.OriginalApproval.Value))
            {
                return OperationResult.Failure(
                    "StartupApproved changed after DeskHush saved its recovery record. " +
                    "The current value was not overwritten.",
                    requiresElevation);
            }
        }
        else
        {
            if (StartupApproval.AllowsStartup(originalApproval.Value))
            {
                entry.IsEnabled = true;
                return OperationResult.Success("The startup entry is already enabled through StartupApproved.");
            }

            approvalOverride = new StartupApprovalOverrideBackup
            {
                Id = entry.Id,
                Kind = kind,
                OriginalApproval = originalApproval,
                AppliedValue = StartupApproval.CreateEnabledValue(originalApproval.Value)
            };
            state.ApprovalOverrides.Add(approvalOverride);
            _stateStore.Save(state);
            addedStateRecord = true;
        }

        try
        {
            WriteApprovalValue(
                approvalOverride.OriginalApproval,
                approvalOverride.OriginalApproval.Value,
                approvalOverride.AppliedValue);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            var current = TryReadApprovalValue(approvalOverride.OriginalApproval, out var readSucceeded);
            if (readSucceeded && StartupApproval.ValuesEqual(current, approvalOverride.AppliedValue))
            {
                entry.IsEnabled = true;
                return OperationResult.Success(
                    "StartupApproved reported an error, but the enabled value was verified and the recovery record was retained.");
            }

            var cleanupNote = addedStateRecord && readSucceeded &&
                StartupApproval.ValuesEqual(current, approvalOverride.OriginalApproval.Value)
                    ? RollBackApprovalOverride(state, approvalOverride)
                    : " The recovery record was retained because the final StartupApproved state is uncertain.";
            return OperationResult.Failure(
                $"StartupApproved could not be enabled: {exception.Message}{cleanupNote}",
                requiresElevation);
        }

        var applied = ReadApprovalValue(approvalOverride.OriginalApproval);
        if (!StartupApproval.ValuesEqual(applied, approvalOverride.AppliedValue) ||
            !StartupApproval.AllowsStartup(applied))
        {
            return OperationResult.Failure(
                "StartupApproved enable verification failed. The original-value recovery record was retained.",
                requiresElevation);
        }

        entry.IsEnabled = true;
        return OperationResult.Success(
            "The startup entry was enabled through StartupApproved; its original disabled value was preserved.");
    }

    private OperationResult RestoreApprovalOverride(
        StartupEntry entry,
        StartupStateDocument state,
        StartupApprovalOverrideBackup approvalOverride,
        StartupApprovalBackup currentLocation,
        bool requiresElevation)
    {
        ValidateApprovalOverride(approvalOverride);
        if (!ApprovalLocationsEqual(approvalOverride.OriginalApproval, currentLocation))
        {
            return OperationResult.Failure(
                "The saved StartupApproved recovery record does not match this startup source.",
                requiresElevation);
        }

        var elevationFailure = RequireElevationIfNeeded(requiresElevation);
        if (elevationFailure is not null)
        {
            return elevationFailure;
        }

        var current = ReadApprovalValue(currentLocation);
        if (!StartupApproval.ValuesEqual(current, approvalOverride.OriginalApproval.Value))
        {
            if (!StartupApproval.ValuesEqual(current, approvalOverride.AppliedValue))
            {
                return OperationResult.Failure(
                    "StartupApproved was changed outside DeskHush. " +
                    "The current value was not overwritten and the recovery record was retained.",
                    requiresElevation);
            }

            WriteApprovalValue(
                approvalOverride.OriginalApproval,
                approvalOverride.AppliedValue,
                approvalOverride.OriginalApproval.Value);

            current = ReadApprovalValue(currentLocation);
            if (!StartupApproval.ValuesEqual(current, approvalOverride.OriginalApproval.Value))
            {
                return OperationResult.Failure(
                    "StartupApproved restore verification failed. The recovery record was retained.",
                    requiresElevation);
            }
        }

        state.ApprovalOverrides.Remove(approvalOverride);
        try
        {
            _stateStore.Save(state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            entry.IsEnabled = StartupApproval.AllowsStartup(current);
            return OperationResult.Failure(
                "The original StartupApproved value was restored, but the recovery-state file could not be updated. " +
                $"The record was retained on disk: {exception.Message}",
                requiresElevation);
        }

        entry.IsEnabled = StartupApproval.AllowsStartup(current);
        return OperationResult.Success("The original StartupApproved value was restored exactly.");
    }

    private static void WriteApprovalValue(
        StartupApprovalBackup location,
        RegistryValueSnapshot? expectedValue,
        RegistryValueSnapshot? replacementValue)
    {
        using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
        RegistryKey? key = baseKey.OpenSubKey(location.SubKeyPath, writable: true);

        if (key is null)
        {
            if (expectedValue is not null)
            {
                throw new IOException("The StartupApproved registry location no longer exists.");
            }

            if (replacementValue is null)
            {
                return;
            }

            key = baseKey.CreateSubKey(location.SubKeyPath, writable: true);
        }

        using (key)
        {
            var currentValue = RegistryValueExists(key, location.ValueName)
                ? RegistryValueSnapshot.Capture(key, location.ValueName)
                : null;
            if (!StartupApproval.ValuesEqual(currentValue, expectedValue))
            {
                throw new IOException(
                    "StartupApproved changed before it could be written; the current value was not overwritten.");
            }

            if (replacementValue is null)
            {
                if (currentValue is not null)
                {
                    key.DeleteValue(location.ValueName, throwOnMissingValue: true);
                }
            }
            else
            {
                replacementValue.Restore(key, location.ValueName);
            }
        }
    }

    private static bool ApprovalMatchesCurrent(StartupApprovalBackup approval)
    {
        return StartupApproval.ValuesEqual(approval.Value, ReadApprovalValue(approval));
    }

    private static RegistryValueSnapshot? TryReadApprovalValue(
        StartupApprovalBackup approval,
        out bool succeeded)
    {
        try
        {
            var value = ReadApprovalValue(approval);
            succeeded = true;
            return value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            succeeded = false;
            return null;
        }
    }

    private static bool ApprovalLocationsEqual(
        StartupApprovalBackup left,
        StartupApprovalBackup right)
    {
        return left.Hive == right.Hive &&
               left.View == right.View &&
               string.Equals(left.SubKeyPath, right.SubKeyPath, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.ValueName, right.ValueName, StringComparison.OrdinalIgnoreCase);
    }

    private RegistryLiveValue? FindLiveRegistryValue(string id, CancellationToken cancellationToken)
    {
        foreach (var location in CreateRegistryLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var baseKey = RegistryKey.OpenBaseKey(location.Hive, location.View);
            using var key = baseKey.OpenSubKey(location.SubKeyPath, writable: false);
            if (key is null)
            {
                continue;
            }

            foreach (var valueName in key.GetValueNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(CreateRegistryId(location, valueName), id, StringComparison.Ordinal))
                {
                    return new RegistryLiveValue(location, valueName);
                }
            }
        }

        return null;
    }

    private StartupFolderLiveItem? FindLiveStartupFolderItem(
        string id,
        CancellationToken cancellationToken)
    {
        foreach (var location in _startupFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(location.Path))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(location.Path, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsDesktopIni(path))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(path);
                if (string.Equals(CreateStartupFolderId(location.Scope, fullPath), id, StringComparison.Ordinal))
                {
                    return new StartupFolderLiveItem(location, fullPath);
                }
            }
        }

        return null;
    }

    private void ValidateStateRecords(StartupStateDocument state)
    {
        foreach (var backup in state.RegistryEntries)
        {
            ValidateRegistryBackup(backup);
        }

        foreach (var backup in state.StartupFolderEntries)
        {
            ValidateStartupFolderBackup(backup);
        }

        foreach (var approvalOverride in state.ApprovalOverrides)
        {
            ValidateApprovalOverride(approvalOverride);
        }
    }

    private static void ValidateRegistryBackup(RegistryStartupBackup backup)
    {
        if (backup is null || string.IsNullOrWhiteSpace(backup.Id) ||
            string.IsNullOrWhiteSpace(backup.SubKeyPath) || backup.ValueName is null || backup.Value is null)
        {
            throw new InvalidDataException("A registry startup backup is incomplete.");
        }

        var location = ToRegistryLocation(backup);
        if (!string.Equals(CreateRegistryId(location, backup.ValueName), backup.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Registry startup backup id '{backup.Id}' is invalid.");
        }

        backup.Value.Validate();
        var expectedApproval = CreateRegistryApprovalLocation(location, backup.ValueName);
        if (expectedApproval is null)
        {
            if (backup.Approval is not null)
            {
                throw new InvalidDataException(
                    $"RunOnce backup '{backup.Id}' must not contain a StartupApproved record.");
            }

            return;
        }

        if (backup.Approval is null)
        {
            throw new InvalidDataException(
                $"Registry startup backup '{backup.Id}' is missing its StartupApproved record.");
        }

        ValidateApprovalBackup(backup.Approval);
        if (!ApprovalLocationsEqual(expectedApproval, backup.Approval) ||
            !StartupApproval.AllowsStartup(backup.Approval.Value))
        {
            throw new InvalidDataException(
                $"Registry startup backup '{backup.Id}' has an invalid or blocking StartupApproved record.");
        }
    }

    private void ValidateStartupFolderBackup(StartupFolderBackup backup)
    {
        if (backup is null || string.IsNullOrWhiteSpace(backup.Id) ||
            string.IsNullOrWhiteSpace(backup.OriginalPath) || string.IsNullOrWhiteSpace(backup.DisabledPath) ||
            backup.Approval is null)
        {
            throw new InvalidDataException("A Startup-folder backup is incomplete.");
        }

        var location = GetStartupFolderLocation(backup.Scope);
        var originalPath = Path.GetFullPath(backup.OriginalPath);
        var originalParent = Path.GetDirectoryName(originalPath);
        var fileName = Path.GetFileName(originalPath);

        if (string.IsNullOrWhiteSpace(fileName) || originalParent is null ||
            !PathsEqual(originalParent, location.Path))
        {
            throw new InvalidDataException(
                $"Startup-folder backup '{backup.Id}' points outside its Startup folder.");
        }

        if (!string.Equals(CreateStartupFolderId(backup.Scope, originalPath), backup.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Startup-folder backup id '{backup.Id}' is invalid.");
        }

        var expectedDisabledPath = Path.Combine(_stateStore.DisabledItemsDirectory, backup.Id, fileName);
        if (!PathsEqual(expectedDisabledPath, backup.DisabledPath))
        {
            throw new InvalidDataException(
                $"Startup-folder backup '{backup.Id}' has an invalid disabled-item path.");
        }


        ValidateApprovalBackup(backup.Approval);
        var expectedApproval = CreateStartupFolderApprovalLocation(location, originalPath);
        if (!ApprovalLocationsEqual(expectedApproval, backup.Approval) ||
            !StartupApproval.AllowsStartup(backup.Approval.Value))
        {
            throw new InvalidDataException(
                $"Startup-folder backup '{backup.Id}' has an invalid or blocking StartupApproved record.");
        }
    }

    private static void ValidateApprovalOverride(StartupApprovalOverrideBackup approvalOverride)
    {
        if (approvalOverride is null || string.IsNullOrWhiteSpace(approvalOverride.Id) ||
            approvalOverride.OriginalApproval is null || approvalOverride.AppliedValue is null ||
            approvalOverride.Kind is not (StartupEntryKind.RegistryRun or StartupEntryKind.StartupFolder))
        {
            throw new InvalidDataException("A StartupApproved override recovery record is incomplete.");
        }

        ValidateApprovalBackup(approvalOverride.OriginalApproval);
        approvalOverride.AppliedValue.Validate();
        var expectedAppliedValue = StartupApproval.CreateEnabledValue(approvalOverride.OriginalApproval.Value);

        if (StartupApproval.AllowsStartup(approvalOverride.OriginalApproval.Value) ||
            !StartupApproval.AllowsStartup(approvalOverride.AppliedValue) ||
            approvalOverride.AppliedValue.Kind != RegistryValueKind.Binary ||
            !approvalOverride.AppliedValue.ContentEquals(expectedAppliedValue))
        {
            throw new InvalidDataException(
                $"StartupApproved override '{approvalOverride.Id}' has inconsistent state values.");
        }
    }

    private static void ValidateApprovalBackup(StartupApprovalBackup approval)
    {
        var nativeView = StartupApproval.GetNativeRegistryView(Environment.Is64BitOperatingSystem);
        var runPath = $@"{StartupApproval.BaseSubKeyPath}\{StartupApproval.RunSubKeyName}";
        var run32Path = $@"{StartupApproval.BaseSubKeyPath}\{StartupApproval.Run32SubKeyName}";
        var folderPath = $@"{StartupApproval.BaseSubKeyPath}\{StartupApproval.StartupFolderSubKeyName}";

        if (approval.Hive is not (RegistryHive.CurrentUser or RegistryHive.LocalMachine) ||
            approval.View != nativeView || approval.ValueName is null ||
            (!string.Equals(approval.SubKeyPath, runPath, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(approval.SubKeyPath, folderPath, StringComparison.OrdinalIgnoreCase) &&
             !(approval.Hive == RegistryHive.LocalMachine && Environment.Is64BitOperatingSystem &&
               string.Equals(approval.SubKeyPath, run32Path, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidDataException("A StartupApproved backup points to an unsupported registry location.");
        }

        approval.Value?.Validate();
    }

    private static RegistryStartupLocation ToRegistryLocation(RegistryStartupBackup backup)
    {
        if (backup.Hive is not (RegistryHive.CurrentUser or RegistryHive.LocalMachine) ||
            backup.View is not (RegistryView.Registry32 or RegistryView.Registry64) ||
            !IsSupportedRunKey(backup.SubKeyPath) ||
            (backup.View == RegistryView.Registry64 && !Environment.Is64BitOperatingSystem))
        {
            throw new InvalidDataException("The registry startup backup points to an unsupported location.");
        }

        return new RegistryStartupLocation(backup.Hive, backup.View, backup.SubKeyPath);
    }

    private static IReadOnlyList<RegistryStartupLocation> CreateRegistryLocations()
    {
        var machineViews = Environment.Is64BitOperatingSystem
            ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
            : new[] { RegistryView.Registry32 };
        var currentUserViews = new[]
        {
            StartupApproval.GetNativeRegistryView(Environment.Is64BitOperatingSystem)
        };

        // HKCU\Software is shared across WOW64 views; reading both produces duplicate startup rows.
        var locations = new List<RegistryStartupLocation>(machineViews.Length * 2 + 2);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            var views = hive == RegistryHive.CurrentUser ? currentUserViews : machineViews;
            foreach (var view in views)
            {
                locations.Add(new RegistryStartupLocation(hive, view, RunKeyPath));
                locations.Add(new RegistryStartupLocation(hive, view, RunOnceKeyPath));
            }
        }

        return locations;
    }

    private static StartupApprovalBackup? CaptureRegistryApproval(
        RegistryStartupLocation sourceLocation,
        string valueName)
    {
        var approval = CreateRegistryApprovalLocation(sourceLocation, valueName);
        if (approval is null)
        {
            return null;
        }

        CaptureApprovalValue(approval);
        return approval;
    }

    private static StartupApprovalBackup? CreateRegistryApprovalLocation(
        RegistryStartupLocation sourceLocation,
        string valueName)
    {
        if (!StartupApproval.HasRegistryApproval(sourceLocation.SubKeyPath))
        {
            return null;
        }

        return new StartupApprovalBackup
        {
            Hive = sourceLocation.Hive,
            View = StartupApproval.GetNativeRegistryView(Environment.Is64BitOperatingSystem),
            SubKeyPath = $@"{StartupApproval.BaseSubKeyPath}\{StartupApproval.GetRunApprovalSubKeyName(
                sourceLocation.Hive,
                sourceLocation.View,
                Environment.Is64BitOperatingSystem)}",
            ValueName = valueName
        };
    }

    private static StartupApprovalBackup CaptureStartupFolderApproval(
        StartupFolderLocation sourceLocation,
        string originalPath)
    {
        var approval = CreateStartupFolderApprovalLocation(sourceLocation, originalPath);
        CaptureApprovalValue(approval);
        return approval;
    }

    private static StartupApprovalBackup CreateStartupFolderApprovalLocation(
        StartupFolderLocation sourceLocation,
        string originalPath)
    {
        return new StartupApprovalBackup
        {
            Hive = sourceLocation.Scope == EntryScope.LocalMachine
                ? RegistryHive.LocalMachine
                : RegistryHive.CurrentUser,
            View = StartupApproval.GetNativeRegistryView(Environment.Is64BitOperatingSystem),
            SubKeyPath = $@"{StartupApproval.BaseSubKeyPath}\{StartupApproval.StartupFolderSubKeyName}",
            ValueName = Path.GetFileName(originalPath)
        };
    }

    private static RegistryValueSnapshot? ReadApprovalValue(StartupApprovalBackup approval)
    {
        using var baseKey = RegistryKey.OpenBaseKey(approval.Hive, approval.View);
        using var key = baseKey.OpenSubKey(approval.SubKeyPath, writable: false);
        if (key is null || !RegistryValueExists(key, approval.ValueName))
        {
            return null;
        }

        return RegistryValueSnapshot.Capture(key, approval.ValueName);
    }

    private static void CaptureApprovalValue(StartupApprovalBackup approval)
    {
        using var baseKey = RegistryKey.OpenBaseKey(approval.Hive, approval.View);
        using var key = baseKey.OpenSubKey(approval.SubKeyPath, writable: false);
        if (key is null)
        {
            approval.Value = null;
            return;
        }

        var actualValueName = key.GetValueNames()
            .SingleOrDefault(name => string.Equals(name, approval.ValueName, StringComparison.OrdinalIgnoreCase));
        if (actualValueName is null)
        {
            approval.Value = null;
            return;
        }

        approval.ValueName = actualValueName;
        approval.Value = RegistryValueSnapshot.Capture(key, actualValueName);
    }

    private static IReadOnlyList<StartupFolderLocation> CreateStartupFolderLocations()
    {
        var locations = new List<StartupFolderLocation>(2);
        AddStartupFolderLocation(
            locations,
            EntryScope.CurrentUser,
            Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        AddStartupFolderLocation(
            locations,
            EntryScope.LocalMachine,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
        return locations;
    }

    private static void AddStartupFolderLocation(
        ICollection<StartupFolderLocation> locations,
        EntryScope scope,
        string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            locations.Add(new StartupFolderLocation(scope, NormalizePath(path)));
        }
    }

    private StartupFolderLocation GetStartupFolderLocation(EntryScope scope)
    {
        var location = _startupFolders.SingleOrDefault(item => item.Scope == scope);
        return location ?? throw new InvalidDataException(
            $"Windows did not provide a Startup-folder path for scope '{scope}'.");
    }

    private static StartupEntry CreateRegistryEntry(
        RegistryStartupLocation location,
        string valueName,
        RegistryValueSnapshot value,
        bool isEnabled)
    {
        return new StartupEntry
        {
            Id = CreateRegistryId(location, valueName),
            Name = string.IsNullOrEmpty(valueName) ? "(Default)" : valueName,
            Command = value.ToDisplayString(),
            SourcePath = location.DisplayPath,
            Kind = StartupEntryKind.RegistryRun,
            Scope = location.Scope,
            IsEnabled = isEnabled,
            RequiresElevation = location.RequiresElevation
        };
    }

    private static StartupEntry CreateStartupFolderEntry(
        StartupFolderLocation location,
        string originalPath,
        bool isEnabled)
    {
        var fullPath = Path.GetFullPath(originalPath);
        return new StartupEntry
        {
            Id = CreateStartupFolderId(location.Scope, fullPath),
            Name = Path.GetFileName(fullPath),
            Command = $"\"{fullPath}\"",
            SourcePath = fullPath,
            Kind = StartupEntryKind.StartupFolder,
            Scope = location.Scope,
            IsEnabled = isEnabled,
            RequiresElevation = location.RequiresElevation
        };
    }

    private static string CreateRegistryId(RegistryStartupLocation location, string valueName)
    {
        return StableId.Create(
            "windows-startup-registry",
            location.Hive.ToString(),
            location.View.ToString(),
            location.SubKeyPath,
            valueName);
    }

    private static string CreateStartupFolderId(EntryScope scope, string originalPath)
    {
        return StableId.Create("windows-startup-folder", scope.ToString(), Path.GetFullPath(originalPath));
    }

    private static bool RegistryValueExists(RegistryKey key, string valueName)
    {
        return key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase);
    }

    private static bool? TryRegistryValueExists(RegistryKey key, string valueName)
    {
        try
        {
            return RegistryValueExists(key, valueName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    private static OperationResult? RequireElevationIfNeeded(bool requiresElevation)
    {
        if (!requiresElevation || IsProcessElevated())
        {
            return null;
        }

        return OperationResult.Failure(
            "This all-users startup entry requires administrator privileges.",
            requiresElevation: true);
    }

    private static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (SecurityException)
        {
            return false;
        }
    }

    private static OperationResult PermissionFailure(StartupEntry entry, string detail)
    {
        var requiresElevation = entry.Scope == EntryScope.LocalMachine || entry.RequiresElevation;
        var message = requiresElevation
            ? $"Administrator privileges are required for this all-users startup entry: {detail}"
            : $"Access to the startup entry was denied: {detail}";
        return OperationResult.Failure(message, requiresElevation);
    }

    private string RollBackRegistryBackup(
        StartupStateDocument state,
        RegistryStartupBackup backup)
    {
        state.RegistryEntries.Remove(backup);

        try
        {
            _stateStore.Save(state);
            return string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return " The pending backup record could not be removed from the state file: " + exception.Message;
        }
    }

    private string RollBackFolderBackup(
        StartupStateDocument state,
        StartupFolderBackup backup,
        string containerPath)
    {
        state.StartupFolderEntries.Remove(backup);
        var details = string.Empty;

        try
        {
            _stateStore.Save(state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            details = " The pending recovery record could not be removed: " + exception.Message;
        }

        TryDeleteEmptyDirectory(containerPath);
        return details;
    }

    private string RollBackApprovalOverride(
        StartupStateDocument state,
        StartupApprovalOverrideBackup approvalOverride)
    {
        state.ApprovalOverrides.Remove(approvalOverride);

        try
        {
            _stateStore.Save(state);
            return string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return " The pending StartupApproved recovery record could not be removed: " + exception.Message;
        }
    }

    private static void MoveFileSystemEntry(string source, string destination, bool isDirectory)
    {
        if (FileSystemEntryExists(destination))
        {
            throw new IOException($"Destination already exists: {destination}");
        }

        if (isDirectory)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static bool FileSystemEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static bool? TryFileSystemEntryExists(string path)
    {
        try
        {
            return FileSystemEntryExists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    private static void TryDeleteEmptyDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (IOException)
        {
            // The directory is non-empty or unavailable; preserving it is safer.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is optional after the item itself has been restored.
        }
    }

    private static bool IsDesktopIni(string path)
    {
        return string.Equals(Path.GetFileName(path), "desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedRunKey(string path)
    {
        return string.Equals(path, RunKeyPath, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(path, RunOnceKeyPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePathOrChild(string path, string possibleParent)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedParent = NormalizePath(possibleParent);
        return PathsEqual(normalizedPath, normalizedParent) ||
               normalizedPath.StartsWith(
                   normalizedParent + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record RegistryStartupLocation(
        RegistryHive Hive,
        RegistryView View,
        string SubKeyPath)
    {
        public EntryScope Scope => Hive == RegistryHive.LocalMachine
            ? EntryScope.LocalMachine
            : EntryScope.CurrentUser;

        public bool RequiresElevation => Scope == EntryScope.LocalMachine;

        public string DisplayPath =>
            $"{(Hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\\{SubKeyPath} " +
            $"[{(View == RegistryView.Registry64 ? "64-bit" : "32-bit")}]";
    }

    private sealed record StartupFolderLocation(EntryScope Scope, string Path)
    {
        public bool RequiresElevation => Scope == EntryScope.LocalMachine;
    }

    private sealed record RegistryLiveValue(RegistryStartupLocation Location, string ValueName);

    private sealed record StartupFolderLiveItem(StartupFolderLocation Location, string Path);
}
