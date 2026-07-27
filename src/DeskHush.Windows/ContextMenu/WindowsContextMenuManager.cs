using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using DeskHush.Core.Interfaces;
using DeskHush.Core.Models;
using DeskHush.Core.Services;
using Microsoft.Win32;

namespace DeskHush.Windows.ContextMenu;

public sealed class WindowsContextMenuManager : IContextMenuManager, IDisposable
{
    private const string ClassesRoot = @"Software\Classes";
    private const string BlockedHandlersPath =
        @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string LegacyDisable = "LegacyDisable";

    private static readonly ShellClass[] ShellClasses =
    [
        new(@"*", ContextMenuLocation.File),
        new(@"Directory", ContextMenuLocation.Folder),
        new(@"Folder", ContextMenuLocation.Folder),
        new(@"Directory\Background", ContextMenuLocation.FolderBackground),
        new(@"Drive", ContextMenuLocation.Drive),
        new(@"DesktopBackground", ContextMenuLocation.Desktop),
        new(@"AllFilesystemObjects", ContextMenuLocation.AllObjects)
    ];

    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _stateFilePath;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private IReadOnlyDictionary<string, EntryDescriptor> _descriptors =
        new Dictionary<string, EntryDescriptor>(StringComparer.OrdinalIgnoreCase);
    private ContextMenuState? _state;
    private bool _disposed;

    public WindowsContextMenuManager(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        _stateFilePath = Path.Combine(Path.GetFullPath(appDataDirectory), "context-menu-state.json");
    }

    public async Task<IReadOnlyList<ContextMenuEntry>> GetEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await Task.Run<IReadOnlyList<ContextMenuEntry>>(
                    () => EnumerateEntries(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<OperationResult> SetEnabledAsync(
        ContextMenuEntry entry,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_descriptors.TryGetValue(entry.Id, out var descriptor))
            {
                return OperationResult.Failure("The context-menu list is stale. Refresh it and try again.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = descriptor.Kind == ContextMenuKind.StaticVerb
                ? SetStaticVerbEnabled(descriptor, enabled)
                : SetShellExtensionEnabled(descriptor, enabled);

            if (result.Succeeded)
            {
                entry.IsEnabled = ReadEnabledState(descriptor);
            }

            return result;
        }
        catch (UnauthorizedAccessException ex)
        {
            var requiresElevation = entry.Kind == ContextMenuKind.StaticVerb &&
                                    entry.Scope == EntryScope.LocalMachine;
            return OperationResult.Failure(ex.Message, requiresElevation);
        }
        catch (SecurityException ex)
        {
            var requiresElevation = entry.Kind == ContextMenuKind.StaticVerb &&
                                    entry.Scope == EntryScope.LocalMachine;
            return OperationResult.Failure(ex.Message, requiresElevation);
        }
        catch (IOException ex)
        {
            return OperationResult.Failure(ex.Message);
        }
        catch (JsonException ex)
        {
            return OperationResult.Failure($"The context-menu state file is invalid: {ex.Message}");
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stateGate.Dispose();
    }

    private IReadOnlyList<ContextMenuEntry> EnumerateEntries(CancellationToken cancellationToken)
    {
        var entries = new List<ContextMenuEntry>();
        var descriptors = new Dictionary<string, EntryDescriptor>(StringComparer.OrdinalIgnoreCase);
        var classServerCache = new Dictionary<string, ClassServerInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var scope in Enum.GetValues<EntryScope>())
        {
            foreach (var view in GetRegistryViews())
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnumerateScope(scope, view, entries, descriptors, classServerCache, cancellationToken);
            }
        }

        _descriptors = descriptors;

        return entries
            .OrderBy(entry => entry.Location)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Kind)
            .ThenBy(entry => entry.Scope)
            .ThenBy(entry => entry.RegistryPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void EnumerateScope(
        EntryScope scope,
        RegistryView view,
        ICollection<ContextMenuEntry> entries,
        IDictionary<string, EntryDescriptor> descriptors,
        IDictionary<string, ClassServerInfo> classServerCache,
        CancellationToken cancellationToken)
    {
        var hive = GetRegistryHive(scope);

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            foreach (var shellClass in ShellClasses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnumerateStaticVerbs(
                    baseKey, hive, scope, view, shellClass, entries, descriptors, cancellationToken);
                EnumerateShellExtensions(
                    baseKey,
                    hive,
                    scope,
                    view,
                    shellClass,
                    entries,
                    descriptors,
                    classServerCache,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (IsSkippableRegistryReadError(ex))
        {
            // A protected or concurrently removed registry branch should not hide other entries.
        }
    }

    private static void EnumerateStaticVerbs(
        RegistryKey baseKey,
        RegistryHive hive,
        EntryScope scope,
        RegistryView view,
        ShellClass shellClass,
        ICollection<ContextMenuEntry> entries,
        IDictionary<string, EntryDescriptor> descriptors,
        CancellationToken cancellationToken)
    {
        var shellPath = $@"{ClassesRoot}\{shellClass.RegistryClass}\shell";
        using var shellKey = TryOpenSubKey(baseKey, shellPath);
        if (shellKey is null)
        {
            return;
        }

        foreach (var verbName in TryGetSubKeyNames(shellKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verbPath = $@"{shellPath}\{verbName}";

            try
            {
                using var verbKey = shellKey.OpenSubKey(verbName, writable: false);
                if (verbKey is null)
                {
                    continue;
                }

                using var commandKey = verbKey.OpenSubKey("command", writable: false);
                var command = ReadString(commandKey, null);
                if (string.IsNullOrWhiteSpace(command))
                {
                    command = ReadString(commandKey, "DelegateExecute");
                }

                if (string.IsNullOrWhiteSpace(command))
                {
                    command = ReadString(verbKey, "ExplorerCommandHandler");
                }

                var name = ResolveStaticVerbName(verbKey, verbName);
                var id = CreateEntryId(
                    ContextMenuKind.StaticVerb, hive, view, verbPath, handlerClsid: null);
                var descriptor = new EntryDescriptor(
                    id,
                    ContextMenuKind.StaticVerb,
                    hive,
                    view,
                    scope,
                    verbPath,
                    HandlerClsid: string.Empty);

                descriptors[id] = descriptor;
                entries.Add(new ContextMenuEntry
                {
                    Id = id,
                    Name = name,
                    RegistryPath = FormatRegistryPath(hive, view, verbPath),
                    Kind = ContextMenuKind.StaticVerb,
                    Location = shellClass.Location,
                    Scope = scope,
                    Command = command,
                    IsEnabled = !HasValue(verbKey, LegacyDisable),
                    RequiresElevation = scope == EntryScope.LocalMachine
                });
            }
            catch (Exception ex) when (IsSkippableRegistryReadError(ex))
            {
                // The verb may disappear while Explorer or an installer updates registration.
            }
        }
    }

    private static void EnumerateShellExtensions(
        RegistryKey baseKey,
        RegistryHive hive,
        EntryScope scope,
        RegistryView view,
        ShellClass shellClass,
        ICollection<ContextMenuEntry> entries,
        IDictionary<string, EntryDescriptor> descriptors,
        IDictionary<string, ClassServerInfo> classServerCache,
        CancellationToken cancellationToken)
    {
        var handlersPath = $@"{ClassesRoot}\{shellClass.RegistryClass}\shellex\ContextMenuHandlers";
        using var handlersKey = TryOpenSubKey(baseKey, handlersPath);
        if (handlersKey is null)
        {
            return;
        }

        foreach (var handlerName in TryGetSubKeyNames(handlersKey))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handlerPath = $@"{handlersPath}\{handlerName}";

            try
            {
                using var handlerKey = handlersKey.OpenSubKey(handlerName, writable: false);
                if (handlerKey is null)
                {
                    continue;
                }

                var handlerClsid = NormalizeClsid(ReadString(handlerKey, null)) ??
                                   NormalizeClsid(handlerName);
                if (handlerClsid is null)
                {
                    continue;
                }

                var classServer = ResolveClassServer(handlerClsid, view, classServerCache);
                var name = ResolveShellExtensionName(handlerName, handlerClsid, classServer.FriendlyName);
                var id = CreateEntryId(
                    ContextMenuKind.ShellExtension, hive, view, handlerPath, handlerClsid);
                var descriptor = new EntryDescriptor(
                    id,
                    ContextMenuKind.ShellExtension,
                    hive,
                    view,
                    scope,
                    handlerPath,
                    handlerClsid);

                descriptors[id] = descriptor;
                entries.Add(new ContextMenuEntry
                {
                    Id = id,
                    Name = name,
                    RegistryPath = FormatRegistryPath(hive, view, handlerPath),
                    Kind = ContextMenuKind.ShellExtension,
                    Location = shellClass.Location,
                    Scope = scope,
                    Command = classServer.ServerPath,
                    HandlerClsid = handlerClsid,
                    Publisher = classServer.Publisher,
                    IsEnabled = !IsHandlerBlocked(handlerClsid, view),
                    RequiresElevation = false
                });
            }
            catch (Exception ex) when (IsSkippableRegistryReadError(ex))
            {
                // Keep enumerating when one third-party handler has an unreadable key.
            }
        }
    }

    private OperationResult SetStaticVerbEnabled(EntryDescriptor descriptor, bool enabled)
    {
        using var baseKey = RegistryKey.OpenBaseKey(descriptor.Hive, descriptor.View);
        bool currentMarkerPresent;
        using (var readKey = baseKey.OpenSubKey(descriptor.KeyPath, writable: false))
        {
            if (readKey is null)
            {
                return OperationResult.Failure("The context-menu registry key no longer exists.");
            }

            currentMarkerPresent = HasValue(readKey, LegacyDisable);
        }

        var desiredMarkerPresent = !enabled;
        var state = GetState();
        var stateKey = descriptor.Id;

        if (!PrepareMarkerChange(
                state.StaticVerbs, stateKey, currentMarkerPresent, desiredMarkerPresent, out var originalPresent))
        {
            return OperationResult.Success("No registry change was needed.");
        }

        using var key = baseKey.OpenSubKey(descriptor.KeyPath, writable: true);
        if (key is null)
        {
            return OperationResult.Failure("The context-menu registry key no longer exists.");
        }

        SetMarkerPresence(key, LegacyDisable, desiredMarkerPresent);
        CompleteMarkerChange(state.StaticVerbs, stateKey, desiredMarkerPresent, originalPresent);
        return OperationResult.Success(
            "Context-menu setting updated. Restart Explorer manually if it does not refresh.");
    }

    private OperationResult SetShellExtensionEnabled(EntryDescriptor descriptor, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(descriptor.HandlerClsid))
        {
            return OperationResult.Failure("The shell extension does not have a valid CLSID.");
        }

        using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, descriptor.View);
        var currentMarkerPresent = false;
        using (var existingBlockedKey = currentUser.OpenSubKey(BlockedHandlersPath, writable: false))
        {
            currentMarkerPresent = HasValue(existingBlockedKey, descriptor.HandlerClsid);
        }

        var desiredMarkerPresent = !enabled;
        var state = GetState();
        var stateKey = $"{descriptor.View}:{descriptor.HandlerClsid}";

        if (!PrepareMarkerChange(
                state.ShellExtensions,
                stateKey,
                currentMarkerPresent,
                desiredMarkerPresent,
                out var originalPresent))
        {
            return OperationResult.Success("No registry change was needed.");
        }

        // The per-user Blocked list overrides both HKCU and HKLM handler registrations.
        using var blockedKey = desiredMarkerPresent
            ? currentUser.CreateSubKey(BlockedHandlersPath, writable: true)
            : currentUser.OpenSubKey(BlockedHandlersPath, writable: true);
        if (blockedKey is null)
        {
            return OperationResult.Failure("Could not open the per-user shell-extension block list.");
        }

        SetMarkerPresence(blockedKey, descriptor.HandlerClsid, desiredMarkerPresent);
        CompleteMarkerChange(state.ShellExtensions, stateKey, desiredMarkerPresent, originalPresent);
        return OperationResult.Success(
            "Shell-extension setting updated. Restart Explorer manually if it does not refresh.");
    }

    private bool PrepareMarkerChange(
        IDictionary<string, MarkerState> records,
        string stateKey,
        bool currentPresent,
        bool desiredPresent,
        out bool originalPresent)
    {
        if (records.TryGetValue(stateKey, out var existingState))
        {
            originalPresent = existingState.OriginalPresent;
            if (currentPresent == desiredPresent)
            {
                if (desiredPresent == originalPresent)
                {
                    records.Remove(stateKey);
                    try
                    {
                        SaveState();
                    }
                    catch
                    {
                        records[stateKey] = existingState;
                        throw;
                    }
                }

                return false;
            }

            return true;
        }

        originalPresent = currentPresent;
        if (currentPresent == desiredPresent)
        {
            return false;
        }

        // Persist the baseline before touching the registry so an interrupted write remains reversible.
        records[stateKey] = new MarkerState { OriginalPresent = originalPresent };
        try
        {
            SaveState();
        }
        catch
        {
            records.Remove(stateKey);
            throw;
        }

        return true;
    }

    private void CompleteMarkerChange(
        IDictionary<string, MarkerState> records,
        string stateKey,
        bool desiredPresent,
        bool originalPresent)
    {
        if (desiredPresent != originalPresent)
        {
            return;
        }

        if (!records.Remove(stateKey))
        {
            return;
        }

        try
        {
            SaveState();
        }
        catch
        {
            records[stateKey] = new MarkerState { OriginalPresent = originalPresent };
            throw;
        }
    }

    private ContextMenuState GetState()
    {
        if (_state is not null)
        {
            return _state;
        }

        if (!File.Exists(_stateFilePath))
        {
            return _state = new ContextMenuState();
        }

        using var stream = File.OpenRead(_stateFilePath);
        var state = JsonSerializer.Deserialize<ContextMenuState>(stream, StateJsonOptions) ??
                    new ContextMenuState();
        state.StaticVerbs = new Dictionary<string, MarkerState>(
            state.StaticVerbs ?? new Dictionary<string, MarkerState>(),
            StringComparer.OrdinalIgnoreCase);
        state.ShellExtensions = new Dictionary<string, MarkerState>(
            state.ShellExtensions ?? new Dictionary<string, MarkerState>(),
            StringComparer.OrdinalIgnoreCase);
        return _state = state;
    }

    private void SaveState()
    {
        var state = _state ?? throw new InvalidOperationException("Context-menu state is not loaded.");
        var directory = Path.GetDirectoryName(_stateFilePath) ??
                        throw new InvalidOperationException("The context-menu state path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = _stateFilePath + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, StateJsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _stateFilePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A stale temporary file is harmless; the last committed state remains intact.
            }
        }
    }

    private bool ReadEnabledState(EntryDescriptor descriptor)
    {
        if (descriptor.Kind == ContextMenuKind.ShellExtension)
        {
            return !IsHandlerBlocked(descriptor.HandlerClsid, descriptor.View);
        }

        using var baseKey = RegistryKey.OpenBaseKey(descriptor.Hive, descriptor.View);
        using var key = baseKey.OpenSubKey(descriptor.KeyPath, writable: false);
        return key is not null && !HasValue(key, LegacyDisable);
    }

    private static ClassServerInfo ResolveClassServer(
        string clsid,
        RegistryView view,
        IDictionary<string, ClassServerInfo> cache)
    {
        var cacheKey = $"{view}:{clsid}";
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var fallbackFriendlyName = string.Empty;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var classKey = baseKey.OpenSubKey($@"{ClassesRoot}\CLSID\{clsid}", writable: false);
                if (classKey is null)
                {
                    continue;
                }

                var friendlyName = ReadString(classKey, "LocalizedString");
                if (string.IsNullOrWhiteSpace(friendlyName))
                {
                    friendlyName = ReadString(classKey, null);
                }

                friendlyName = ResolveIndirectString(friendlyName);
                if (string.IsNullOrWhiteSpace(fallbackFriendlyName))
                {
                    fallbackFriendlyName = friendlyName;
                }

                using var inprocKey = classKey.OpenSubKey("InprocServer32", writable: false);
                using var localServerKey = classKey.OpenSubKey("LocalServer32", writable: false);
                var rawServerPath = ReadString(inprocKey, null);
                if (string.IsNullOrWhiteSpace(rawServerPath))
                {
                    rawServerPath = ReadString(localServerKey, null);
                }

                var serverPath = NormalizeServerPath(rawServerPath);
                if (string.IsNullOrWhiteSpace(serverPath))
                {
                    continue;
                }

                var publisher = ResolvePublisher(serverPath);
                var resolved = new ClassServerInfo(
                    string.IsNullOrWhiteSpace(friendlyName) ? fallbackFriendlyName : friendlyName,
                    serverPath,
                    publisher);
                cache[cacheKey] = resolved;
                return resolved;
            }
            catch (Exception ex) when (IsSkippableRegistryReadError(ex))
            {
                // Try the other registration hive.
            }
        }

        var empty = new ClassServerInfo(fallbackFriendlyName, string.Empty, string.Empty);
        cache[cacheKey] = empty;
        return empty;
    }

    private static string ResolveStaticVerbName(RegistryKey verbKey, string verbName)
    {
        var name = ReadString(verbKey, "MUIVerb");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = ReadString(verbKey, null);
        }

        name = ResolveIndirectString(name);
        return string.IsNullOrWhiteSpace(name) ? verbName : name.Replace("&", string.Empty).Trim();
    }

    private static string ResolveShellExtensionName(
        string handlerName,
        string handlerClsid,
        string registeredName)
    {
        if (!string.IsNullOrWhiteSpace(handlerName) && NormalizeClsid(handlerName) is null)
        {
            return handlerName;
        }

        if (!string.IsNullOrWhiteSpace(registeredName))
        {
            return registeredName;
        }

        return handlerClsid;
    }

    private static string ResolveIndirectString(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.TrimStart().StartsWith('@'))
        {
            return value;
        }

        var buffer = new StringBuilder(512);
        try
        {
            return SHLoadIndirectString(value, buffer, (uint)buffer.Capacity, IntPtr.Zero) == 0
                ? buffer.ToString()
                : value;
        }
        catch (DllNotFoundException)
        {
            return value;
        }
        catch (EntryPointNotFoundException)
        {
            return value;
        }
    }

    private static string NormalizeServerPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim());
        if (expanded[0] == '"')
        {
            var closingQuote = expanded.IndexOf('"', 1);
            return closingQuote > 1 ? expanded[1..closingQuote] : expanded.Trim('"');
        }

        var executableEnd = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (executableEnd >= 0)
        {
            return expanded[..(executableEnd + 4)].Trim();
        }

        var libraryEnd = expanded.IndexOf(".dll", StringComparison.OrdinalIgnoreCase);
        if (libraryEnd >= 0)
        {
            return expanded[..(libraryEnd + 4)].Trim();
        }

        return expanded;
    }

    private static string ResolvePublisher(string serverPath)
    {
        if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
        {
            return string.Empty;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(serverPath).CompanyName?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return string.Empty;
        }
    }

    private static bool IsHandlerBlocked(string clsid, RegistryView view)
    {
        try
        {
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var blockedKey = currentUser.OpenSubKey(BlockedHandlersPath, writable: false);
            return HasValue(blockedKey, clsid);
        }
        catch (Exception ex) when (IsSkippableRegistryReadError(ex))
        {
            return false;
        }
    }

    private static RegistryKey? TryOpenSubKey(RegistryKey baseKey, string path)
    {
        try
        {
            return baseKey.OpenSubKey(path, writable: false);
        }
        catch (Exception ex) when (IsSkippableRegistryReadError(ex))
        {
            return null;
        }
    }

    private static string[] TryGetSubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception ex) when (IsSkippableRegistryReadError(ex))
        {
            return [];
        }
    }

    private static string ReadString(RegistryKey? key, string? valueName)
    {
        try
        {
            return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                       ?.ToString()
                       ?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (IsSkippableRegistryReadError(ex))
        {
            return string.Empty;
        }
    }

    private static bool HasValue(RegistryKey? key, string valueName)
    {
        if (key is null)
        {
            return false;
        }

        try
        {
            return key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsSkippableRegistryReadError(ex))
        {
            return false;
        }
    }

    private static void SetMarkerPresence(RegistryKey key, string valueName, bool present)
    {
        if (present)
        {
            if (!HasValue(key, valueName))
            {
                key.SetValue(valueName, string.Empty, RegistryValueKind.String);
            }
        }
        else
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    private static string? NormalizeClsid(string value)
    {
        if (!Guid.TryParse(value?.Trim(), out var guid))
        {
            return null;
        }

        return guid.ToString("B").ToUpperInvariant();
    }

    private static string CreateEntryId(
        ContextMenuKind kind,
        RegistryHive hive,
        RegistryView view,
        string registryPath,
        string? handlerClsid)
    {
        return StableId.Create(
            "context-menu",
            kind.ToString(),
            hive.ToString(),
            view.ToString(),
            registryPath,
            handlerClsid);
    }

    private static string FormatRegistryPath(RegistryHive hive, RegistryView view, string path)
    {
        var hiveName = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
        var viewName = view == RegistryView.Registry64 ? "64-bit" : "32-bit";
        return $@"{hiveName}\{path} ({viewName})";
    }

    private static RegistryHive GetRegistryHive(EntryScope scope) =>
        scope == EntryScope.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    private static IReadOnlyList<RegistryView> GetRegistryViews() =>
        Environment.Is64BitOperatingSystem
            ? [RegistryView.Registry64, RegistryView.Registry32]
            : [RegistryView.Registry32];

    private static bool IsSkippableRegistryReadError(Exception exception) =>
        exception is UnauthorizedAccessException or SecurityException or IOException;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(
        string source,
        StringBuilder outputBuffer,
        uint outputBufferSize,
        IntPtr reserved);

    private sealed record ShellClass(string RegistryClass, ContextMenuLocation Location);

    private sealed record EntryDescriptor(
        string Id,
        ContextMenuKind Kind,
        RegistryHive Hive,
        RegistryView View,
        EntryScope Scope,
        string KeyPath,
        string HandlerClsid);

    private sealed record ClassServerInfo(string FriendlyName, string ServerPath, string Publisher);

    private sealed class ContextMenuState
    {
        public int Version { get; init; } = 1;

        public Dictionary<string, MarkerState> StaticVerbs { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, MarkerState> ShellExtensions { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class MarkerState
    {
        public bool OriginalPresent { get; init; }
    }
}
