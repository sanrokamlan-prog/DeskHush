using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Data;
using DeskHush.App.Infrastructure;
using DeskHush.Core.Interfaces;
using DeskHush.Core.Models;
using DeskHush.Core.Services;
using DeskHush.Windows.SystemIntegration;

namespace DeskHush.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore settingsStore;
    private readonly IWindowCatalog windowCatalog;
    private readonly IPopupBlocker popupBlocker;
    private readonly IWindowRecorder windowRecorder;
    private readonly IUpdateChecker updateChecker;
    private readonly Version currentVersion;
    private readonly IContextMenuManager contextMenuManager;
    private readonly IStartupManager startupManager;
    private readonly StartupRegistrationService startupRegistration;
    private readonly string executablePath;
    private readonly string dataDirectory;
    private readonly object settingsSaveLock = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();

    private AppSettings settings = new();
    private Task pendingSettingsSave = Task.CompletedTask;
    private long windowRecordDisplayGeneration;
    private WindowInfo? selectedWindow;
    private WindowRecord? selectedWindowRecord;
    private PopupRule? selectedPopupRuleSource;
    private PopupRule? selectedPopupRule;
    private string windowSearchText = string.Empty;
    private string windowRecordSearchText = string.Empty;
    private string contextSearchText = string.Empty;
    private string startupSearchText = string.Empty;
    private string statusMessage = "正在初始化...";
    private bool isPopupBlockingEnabled;
    private bool isWindowRecordingEnabled = true;
    private bool checkForUpdatesEnabled = true;
    private bool startWithWindows;
    private bool minimizeToTray = true;
    private bool isBusy;
    private bool initialized;
    private bool disposed;
    private DateTimeOffset? lastUpdateCheckAt;
    private UpdateInfo? availableUpdate;
    private string? windowRecorderError;

    public MainViewModel(
        ISettingsStore settingsStore,
        IWindowCatalog windowCatalog,
        IPopupBlocker popupBlocker,
        IWindowRecorder windowRecorder,
        IUpdateChecker updateChecker,
        Version currentVersion,
        IContextMenuManager contextMenuManager,
        IStartupManager startupManager,
        StartupRegistrationService startupRegistration,
        string executablePath,
        string dataDirectory,
        Action restartElevated)
    {
        this.settingsStore = settingsStore;
        this.windowCatalog = windowCatalog;
        this.popupBlocker = popupBlocker;
        this.windowRecorder = windowRecorder;
        this.updateChecker = updateChecker;
        this.currentVersion = currentVersion;
        this.contextMenuManager = contextMenuManager;
        this.startupManager = startupManager;
        this.startupRegistration = startupRegistration;
        this.executablePath = executablePath;
        this.dataDirectory = dataDirectory;

        RefreshWindowsCommand = new RelayCommand(RefreshWindows);
        AddRuleFromWindowCommand = new AsyncRelayCommand(AddRuleFromWindowAsync, () => SelectedWindow is not null, HandleCommandError);
        AddRuleFromRecordCommand = new AsyncRelayCommand(AddRuleFromRecordAsync, () => SelectedWindowRecord is not null, HandleCommandError);
        ClearWindowRecordsCommand = new RelayCommand(ClearWindowRecords, () => WindowRecords.Count > 0);
        CheckForUpdatesCommand = new AsyncRelayCommand(() => CheckForUpdatesAsync(manual: true), onError: HandleCommandError);
        OpenUpdateCommand = new RelayCommand(OpenAvailableUpdate, () => AvailableUpdate is not null);
        SaveRuleCommand = new AsyncRelayCommand(
            SaveSelectedRuleAsync,
            () => SelectedPopupRuleSource is not null && SelectedPopupRule is not null,
            HandleCommandError);
        RemoveRuleCommand = new AsyncRelayCommand(
            RemoveSelectedRuleAsync,
            () => SelectedPopupRuleSource is not null,
            HandleCommandError);
        TogglePopupRuleCommand = new AsyncRelayCommand<PopupRule>(TogglePopupRuleAsync, rule => rule is not null, HandleCommandError);
        RefreshContextMenuCommand = new AsyncRelayCommand(async () => { await RefreshContextMenuAsync(); }, onError: HandleCommandError);
        ToggleContextMenuCommand = new AsyncRelayCommand<ContextMenuEntry>(ToggleContextMenuAsync, entry => entry is not null, HandleCommandError);
        RefreshStartupCommand = new AsyncRelayCommand(async () => { await RefreshStartupAsync(); }, onError: HandleCommandError);
        ToggleStartupCommand = new AsyncRelayCommand<StartupEntry>(ToggleStartupAsync, entry => entry is not null, HandleCommandError);
        OpenDataDirectoryCommand = new RelayCommand(OpenDataDirectory);
        RestartElevatedCommand = new RelayCommand(restartElevated, () => !IsAdministrator);

        popupBlocker.PopupBlocked += OnPopupBlocked;
        windowRecorder.WindowRecorded += OnWindowRecorded;
    }

    public ObservableCollection<WindowInfo> VisibleWindows { get; } = [];

    public ObservableCollection<PopupRule> PopupRules { get; } = [];

    public ObservableCollection<WindowRecord> WindowRecords { get; } = [];

    public ObservableCollection<ContextMenuEntry> ContextMenuEntries { get; } = [];

    public ObservableCollection<StartupEntry> StartupEntries { get; } = [];

    public ObservableCollection<ActivityItem> RecentActivity { get; } = [];

    public event EventHandler<UpdateInfo>? UpdateAvailable;

    public RelayCommand RefreshWindowsCommand { get; }

    public AsyncRelayCommand AddRuleFromWindowCommand { get; }

    public AsyncRelayCommand AddRuleFromRecordCommand { get; }

    public RelayCommand ClearWindowRecordsCommand { get; }

    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public RelayCommand OpenUpdateCommand { get; }

    public AsyncRelayCommand SaveRuleCommand { get; }

    public AsyncRelayCommand RemoveRuleCommand { get; }

    public AsyncRelayCommand<PopupRule> TogglePopupRuleCommand { get; }

    public AsyncRelayCommand RefreshContextMenuCommand { get; }

    public AsyncRelayCommand<ContextMenuEntry> ToggleContextMenuCommand { get; }

    public AsyncRelayCommand RefreshStartupCommand { get; }

    public AsyncRelayCommand<StartupEntry> ToggleStartupCommand { get; }

    public RelayCommand OpenDataDirectoryCommand { get; }

    public RelayCommand RestartElevatedCommand { get; }

    public bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public string AdministratorStatus => IsAdministrator ? "管理员模式" : "标准权限";

    public bool IsPopupBlockingEnabled
    {
        get => isPopupBlockingEnabled;
        set
        {
            if (!SetProperty(ref isPopupBlockingEnabled, value))
            {
                return;
            }

            settings.PopupBlockingEnabled = value;
            OnPropertyChanged(nameof(ProtectionStatus));
            if (!initialized)
            {
                return;
            }

            ApplyPopupEngineState();
            QueueSettingsSave();
        }
    }

    public bool IsWindowRecordingEnabled
    {
        get => isWindowRecordingEnabled;
        set
        {
            if (!SetProperty(ref isWindowRecordingEnabled, value))
            {
                return;
            }

            settings.WindowRecordingEnabled = value;
            OnPropertyChanged(nameof(WindowRecordingStatus));
            if (!initialized)
            {
                return;
            }

            if (!ApplyWindowRecorderState())
            {
                return;
            }

            QueueSettingsSave();
            StatusMessage = value ? "窗口记录已开启。" : "窗口记录已暂停。";
        }
    }

    public bool CheckForUpdatesEnabled
    {
        get => checkForUpdatesEnabled;
        set
        {
            if (!SetProperty(ref checkForUpdatesEnabled, value))
            {
                return;
            }

            settings.CheckForUpdatesEnabled = value;
            OnPropertyChanged(nameof(UpdateStatusText));
            if (!initialized)
            {
                return;
            }

            QueueSettingsSave();
            StatusMessage = value ? "已开启新版本检查。" : "已关闭自动新版本检查。";
            if (value)
            {
                _ = CheckForUpdatesAsync(manual: false);
            }
        }
    }

    public bool StartWithWindows
    {
        get => startWithWindows;
        set
        {
            if (!SetProperty(ref startWithWindows, value))
            {
                return;
            }

            settings.StartWithWindows = value;
            if (!initialized)
            {
                return;
            }

            try
            {
                startupRegistration.SetEnabled(value, executablePath);
                StatusMessage = value ? "已启用开机启动。" : "已关闭开机启动。";
                QueueSettingsSave();
            }
            catch (Exception exception)
            {
                try
                {
                    startWithWindows = startupRegistration.IsEnabled(executablePath);
                }
                catch
                {
                    startWithWindows = !value;
                }

                settings.StartWithWindows = startWithWindows;
                OnPropertyChanged(nameof(StartWithWindows));
                StatusMessage = $"开机启动设置失败：{exception.Message}";
            }
        }
    }

    public bool MinimizeToTray
    {
        get => minimizeToTray;
        set
        {
            if (!SetProperty(ref minimizeToTray, value))
            {
                return;
            }

            settings.MinimizeToTray = value;
            if (initialized)
            {
                QueueSettingsSave();
            }
        }
    }

    public string ProtectionStatus => IsPopupBlockingEnabled ? "拦截运行中" : "拦截已暂停";

    public string WindowRecordingStatus => windowRecorder.IsRunning ? "记录中" : "已暂停";

    public UpdateInfo? AvailableUpdate
    {
        get => availableUpdate;
        private set
        {
            if (SetProperty(ref availableUpdate, value))
            {
                OnPropertyChanged(nameof(HasAvailableUpdate));
                OnPropertyChanged(nameof(UpdateStatusText));
                OpenUpdateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasAvailableUpdate => AvailableUpdate is not null;

    public string CurrentVersionText => $"当前版本 v{currentVersion.Major}.{currentVersion.Minor}.{Math.Max(0, currentVersion.Build)}";

    public string UpdateStatusText => AvailableUpdate is not null
        ? $"发现新版本 {AvailableUpdate.TagName}"
        : CheckForUpdatesEnabled ? "每天自动检查一次" : "自动检查已关闭";

    public string RuleCountText => $"{PopupRules.Count} 条规则";

    public string ContextCountText => $"{ContextMenuEntries.Count} 个菜单项";

    public string StartupCountText => $"{StartupEntries.Count} 个启动项";

    public string BlockCountText => PopupRules.Sum(rule => rule.HitCount).ToString("N0");

    public bool IsBusy
    {
        get => isBusy;
        private set => SetProperty(ref isBusy, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public WindowInfo? SelectedWindow
    {
        get => selectedWindow;
        set
        {
            if (SetProperty(ref selectedWindow, value))
            {
                AddRuleFromWindowCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public WindowRecord? SelectedWindowRecord
    {
        get => selectedWindowRecord;
        set
        {
            if (SetProperty(ref selectedWindowRecord, value))
            {
                AddRuleFromRecordCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public PopupRule? SelectedPopupRule
    {
        get => selectedPopupRule;
        private set
        {
            if (SetProperty(ref selectedPopupRule, value))
            {
                SaveRuleCommand.RaiseCanExecuteChanged();
                RemoveRuleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public PopupRule? SelectedPopupRuleSource
    {
        get => selectedPopupRuleSource;
        set
        {
            if (!SetProperty(ref selectedPopupRuleSource, value))
            {
                return;
            }

            SelectedPopupRule = value is null ? null : ClonePopupRule(value);
            SaveRuleCommand.RaiseCanExecuteChanged();
            RemoveRuleCommand.RaiseCanExecuteChanged();
        }
    }

    public string WindowSearchText
    {
        get => windowSearchText;
        set
        {
            if (SetProperty(ref windowSearchText, value))
            {
                CollectionViewSource.GetDefaultView(VisibleWindows).Refresh();
            }
        }
    }

    public string WindowRecordSearchText
    {
        get => windowRecordSearchText;
        set
        {
            if (SetProperty(ref windowRecordSearchText, value))
            {
                CollectionViewSource.GetDefaultView(WindowRecords).Refresh();
            }
        }
    }

    public string ContextSearchText
    {
        get => contextSearchText;
        set
        {
            if (SetProperty(ref contextSearchText, value))
            {
                CollectionViewSource.GetDefaultView(ContextMenuEntries).Refresh();
            }
        }
    }

    public string StartupSearchText
    {
        get => startupSearchText;
        set
        {
            if (SetProperty(ref startupSearchText, value))
            {
                CollectionViewSource.GetDefaultView(StartupEntries).Refresh();
            }
        }
    }

    public async Task InitializeAsync()
    {
        settings = await settingsStore.LoadAsync();
        foreach (var rule in settings.PopupRules)
        {
            PopupRules.Add(rule);
        }

        isPopupBlockingEnabled = settings.PopupBlockingEnabled;
        isWindowRecordingEnabled = settings.WindowRecordingEnabled;
        checkForUpdatesEnabled = settings.CheckForUpdatesEnabled;
        lastUpdateCheckAt = settings.LastUpdateCheckAt;
        startWithWindows = startupRegistration.IsEnabled(executablePath);
        settings.StartWithWindows = startWithWindows;
        minimizeToTray = settings.MinimizeToTray;
        OnPropertyChanged(nameof(IsPopupBlockingEnabled));
        OnPropertyChanged(nameof(IsWindowRecordingEnabled));
        OnPropertyChanged(nameof(CheckForUpdatesEnabled));
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(MinimizeToTray));
        OnPropertyChanged(nameof(ProtectionStatus));
        OnPropertyChanged(nameof(WindowRecordingStatus));
        OnPropertyChanged(nameof(CurrentVersionText));
        OnPropertyChanged(nameof(UpdateStatusText));
        RaiseSummaryProperties();

        initialized = true;
        ConfigureCollectionFilters();
        ApplyPopupEngineState();
        ApplyWindowRecorderState();
        RefreshWindows();

        IsBusy = true;
        try
        {
            var contextMenuLoaded = await RefreshContextMenuAsync();
            var startupLoaded = await RefreshStartupAsync();
            if (contextMenuLoaded && startupLoaded)
            {
                StatusMessage = "就绪。右键菜单与启动项变更均可恢复。";
            }
            else if (!contextMenuLoaded && !startupLoaded)
            {
                StatusMessage = "右键菜单与启动项均读取失败；未执行任何系统变更。";
            }
        }
        finally
        {
            IsBusy = false;
        }

        if (windowRecorderError is not null)
        {
            StatusMessage = $"窗口记录未启动：{windowRecorderError}；其他模块已继续加载。";
        }

        if (CheckForUpdatesEnabled)
        {
            _ = CheckForUpdatesAsync(manual: false);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Interlocked.Increment(ref windowRecordDisplayGeneration);
        lifetimeCancellation.Cancel();

        popupBlocker.PopupBlocked -= OnPopupBlocked;
        popupBlocker.Dispose();
        windowRecorder.WindowRecorded -= OnWindowRecorded;
        windowRecorder.Dispose();

        Task pendingSave;
        lock (settingsSaveLock)
        {
            pendingSave = pendingSettingsSave;
        }

        try
        {
            pendingSave.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"DeskHush 退出前未能保存最新设置：{exception.Message}",
                "DeskHush",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

    }

    private void ConfigureCollectionFilters()
    {
        CollectionViewSource.GetDefaultView(VisibleWindows).Filter = item =>
            item is WindowInfo window && MatchesSearch(WindowSearchText, window.ProcessName, window.Title, window.ClassName);

        CollectionViewSource.GetDefaultView(WindowRecords).Filter = item =>
            item is WindowRecord record && MatchesSearch(
                WindowRecordSearchText,
                record.ProcessName,
                record.Title,
                record.ClassName,
                record.SizeText,
                record.PositionText);

        CollectionViewSource.GetDefaultView(ContextMenuEntries).Filter = item =>
            item is ContextMenuEntry entry && MatchesSearch(ContextSearchText, entry.Name, entry.Command, entry.Publisher);

        CollectionViewSource.GetDefaultView(StartupEntries).Filter = item =>
            item is StartupEntry entry && MatchesSearch(StartupSearchText, entry.Name, entry.Command, entry.Publisher);
    }

    private void RefreshWindows()
    {
        var selectedHandle = SelectedWindow?.Handle;
        ReplaceCollection(VisibleWindows, windowCatalog.GetVisibleWindows());
        SelectedWindow = VisibleWindows.FirstOrDefault(window => window.Handle == selectedHandle);
        StatusMessage = $"发现 {VisibleWindows.Count} 个可见顶层窗口。";
    }

    private async Task AddRuleFromWindowAsync()
    {
        if (SelectedWindow is null)
        {
            return;
        }

        await CreateRuleFromWindowAsync(SelectedWindow);
    }

    private async Task AddRuleFromRecordAsync()
    {
        if (SelectedWindowRecord is null)
        {
            return;
        }

        await CreateRuleFromWindowAsync(SelectedWindowRecord.Window);
    }

    public async Task CreateRuleFromWindowAsync(WindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var title = window.Title;
        var hasSpecificTitle = !string.IsNullOrWhiteSpace(title);
        var shortTitle = title.Length > 28 ? $"{title[..28]}..." : title;
        var rule = new PopupRule
        {
            Name = string.IsNullOrWhiteSpace(shortTitle)
                ? $"{window.ProcessName} 窗口"
                : $"{window.ProcessName} - {shortTitle}",
            ProcessName = window.ProcessName,
            ProcessPath = window.ProcessPath,
            WindowClass = window.ClassName,
            TitlePattern = title,
            TitleMatchMode = TextMatchMode.Exact,
            Action = PopupAction.Hide,
            IsEnabled = hasSpecificTitle
        };

        var validation = PopupRuleValidator.Validate(rule);
        if (!validation.Succeeded)
        {
            StatusMessage = validation.Message;
            return;
        }

        PopupRules.Add(rule);
        SelectedPopupRuleSource = rule;
        popupBlocker.UpdateRules(PopupRules);
        await SaveSettingsAsync();
        RaiseSummaryProperties();
        StatusMessage = hasSpecificTitle
            ? "已创建规则，默认使用隐藏动作。"
            : "窗口标题尚未生成，已创建暂停规则；补充标题后再启用。";
    }

    private void ClearWindowRecords()
    {
        Interlocked.Increment(ref windowRecordDisplayGeneration);
        windowRecorder.Clear();
        WindowRecords.Clear();
        SelectedWindowRecord = null;
        ClearWindowRecordsCommand.RaiseCanExecuteChanged();
        StatusMessage = "窗口记录已清空。";
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        var now = DateTimeOffset.UtcNow;
        if (!manual && lastUpdateCheckAt is not null && now - lastUpdateCheckAt < TimeSpan.FromDays(1))
        {
            return;
        }

        lastUpdateCheckAt = now;
        settings.LastUpdateCheckAt = now;
        QueueSettingsSave();

        try
        {
            var update = await updateChecker.CheckAsync(currentVersion, lifetimeCancellation.Token);
            AvailableUpdate = update;
            if (update is not null)
            {
                StatusMessage = $"发现新版本 {update.TagName}。";
                UpdateAvailable?.Invoke(this, update);
            }
            else if (manual)
            {
                StatusMessage = "当前已是最新版本。";
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (manual)
            {
                StatusMessage = $"检查更新失败：{exception.Message}";
            }
        }
    }

    private void OpenAvailableUpdate()
    {
        if (AvailableUpdate is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = AvailableUpdate.ReleaseUri.AbsoluteUri,
            UseShellExecute = true
        });
    }

    private async Task SaveSelectedRuleAsync()
    {
        if (SelectedPopupRuleSource is null || SelectedPopupRule is null)
        {
            return;
        }

        var editedRule = SelectedPopupRule;
        var sourceRule = SelectedPopupRuleSource;
        var validation = PopupRuleValidator.Validate(editedRule);
        if (!validation.Succeeded)
        {
            StatusMessage = validation.Message;
            return;
        }

        ApplyRuleEdits(editedRule, sourceRule);
        popupBlocker.UpdateRules(PopupRules);
        await SaveSettingsAsync();
        CollectionViewSource.GetDefaultView(PopupRules).Refresh();
        StatusMessage = "规则已保存并立即生效。";
    }

    private async Task RemoveSelectedRuleAsync()
    {
        if (SelectedPopupRuleSource is null)
        {
            return;
        }

        PopupRules.Remove(SelectedPopupRuleSource);
        SelectedPopupRuleSource = null;
        popupBlocker.UpdateRules(PopupRules);
        await SaveSettingsAsync();
        RaiseSummaryProperties();
        StatusMessage = "规则已删除。";
    }

    private async Task TogglePopupRuleAsync(PopupRule? rule)
    {
        if (rule is null)
        {
            return;
        }

        rule.IsEnabled = !rule.IsEnabled;
        popupBlocker.UpdateRules(PopupRules);
        await SaveSettingsAsync();
        CollectionViewSource.GetDefaultView(PopupRules).Refresh();
        StatusMessage = rule.IsEnabled ? "规则已启用。" : "规则已暂停。";
    }

    private async Task<bool> RefreshContextMenuAsync()
    {
        try
        {
            var entries = await contextMenuManager.GetEntriesAsync();
            ReplaceCollection(ContextMenuEntries, entries);
            RaiseSummaryProperties();
            return true;
        }
        catch (Exception exception)
        {
            StatusMessage = $"读取右键菜单失败：{exception.Message}";
            return false;
        }
    }

    private async Task ToggleContextMenuAsync(ContextMenuEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        var result = await contextMenuManager.SetEnabledAsync(entry, !entry.IsEnabled);
        StatusMessage = FormatOperationResult(result, entry.Name, !entry.IsEnabled);
        if (result.Succeeded)
        {
            await RefreshContextMenuAsync();
        }
    }

    private async Task<bool> RefreshStartupAsync()
    {
        try
        {
            var entries = await startupManager.GetEntriesAsync();
            ReplaceCollection(StartupEntries, entries);
            RaiseSummaryProperties();
            return true;
        }
        catch (Exception exception)
        {
            StatusMessage = $"读取启动项失败：{exception.Message}";
            return false;
        }
    }

    private async Task ToggleStartupAsync(StartupEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        var result = await startupManager.SetEnabledAsync(entry, !entry.IsEnabled);
        StatusMessage = FormatOperationResult(result, entry.Name, !entry.IsEnabled);
        if (result.Succeeded)
        {
            await RefreshStartupAsync();
        }
    }

    private void ApplyPopupEngineState()
    {
        if (IsPopupBlockingEnabled)
        {
            if (popupBlocker.IsRunning)
            {
                popupBlocker.UpdateRules(PopupRules);
            }
            else
            {
                popupBlocker.Start(PopupRules);
            }
        }
        else
        {
            popupBlocker.Stop();
        }
    }

    private bool ApplyWindowRecorderState()
    {
        try
        {
            if (IsWindowRecordingEnabled)
            {
                windowRecorder.Start();
            }
            else
            {
                windowRecorder.Stop();
            }

            windowRecorderError = null;
            OnPropertyChanged(nameof(WindowRecordingStatus));
            return true;
        }
        catch (Exception exception)
        {
            isWindowRecordingEnabled = false;
            settings.WindowRecordingEnabled = false;
            windowRecorderError = exception.Message;
            OnPropertyChanged(nameof(IsWindowRecordingEnabled));
            OnPropertyChanged(nameof(WindowRecordingStatus));
            if (initialized)
            {
                QueueSettingsSave();
            }

            StatusMessage = $"窗口记录启动失败：{exception.Message}";
            return false;
        }
    }

    private void OnPopupBlocked(object? sender, PopupBlockedEvent popupEvent)
    {
        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            RecentActivity.Insert(0, new ActivityItem(
                popupEvent.BlockedAt,
                popupEvent.Rule.Name,
                $"{popupEvent.Window.ProcessName} · {popupEvent.Window.Title}"));

            while (RecentActivity.Count > 20)
            {
                RecentActivity.RemoveAt(RecentActivity.Count - 1);
            }

            RaiseSummaryProperties();
            StatusMessage = $"已处理窗口：{popupEvent.Window.Title}";
            QueueSettingsSave();
        });
    }

    private void OnWindowRecorded(object? sender, WindowRecord record)
    {
        var displayGeneration = Volatile.Read(ref windowRecordDisplayGeneration);
        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (displayGeneration != Volatile.Read(ref windowRecordDisplayGeneration))
            {
                return;
            }

            WindowRecords.Insert(0, record);
            while (WindowRecords.Count > WindowRecordBuffer.DefaultCapacity)
            {
                WindowRecords.RemoveAt(WindowRecords.Count - 1);
            }

            ClearWindowRecordsCommand.RaiseCanExecuteChanged();
        });
    }

    private Task SaveSettingsAsync()
    {
        var snapshot = CreateSettingsSnapshot();
        lock (settingsSaveLock)
        {
            pendingSettingsSave = SaveSettingsAfterAsync(pendingSettingsSave, snapshot);
            return pendingSettingsSave;
        }
    }

    private async Task SaveSettingsAfterAsync(Task previousSave, AppSettings snapshot)
    {
        try
        {
            await previousSave.ConfigureAwait(false);
        }
        catch
        {
            // A later complete snapshot can recover from an earlier failed write.
        }

        await settingsStore.SaveAsync(snapshot).ConfigureAwait(false);
    }

    private void QueueSettingsSave()
    {
        if (!disposed)
        {
            _ = ObserveSettingsSaveAsync(SaveSettingsAsync());
        }
    }

    private async Task ObserveSettingsSaveAsync(Task saveTask)
    {
        try
        {
            await saveTask;
        }
        catch (Exception exception)
        {
            StatusMessage = $"设置保存失败：{exception.Message}";
        }
    }

    private AppSettings CreateSettingsSnapshot()
    {
        return new AppSettings
        {
            PopupBlockingEnabled = isPopupBlockingEnabled,
            WindowRecordingEnabled = isWindowRecordingEnabled,
            CheckForUpdatesEnabled = checkForUpdatesEnabled,
            LastUpdateCheckAt = lastUpdateCheckAt,
            StartWithWindows = startWithWindows,
            MinimizeToTray = minimizeToTray,
            Language = settings.Language,
            PopupRules = PopupRules.Select(ClonePopupRule).ToList()
        };
    }

    private static PopupRule ClonePopupRule(PopupRule rule)
    {
        return new PopupRule
        {
            Id = rule.Id,
            Name = rule.Name,
            ProcessName = rule.ProcessName,
            ProcessPath = rule.ProcessPath,
            WindowClass = rule.WindowClass,
            TitlePattern = rule.TitlePattern,
            TitleMatchMode = rule.TitleMatchMode,
            Action = rule.Action,
            IsEnabled = rule.IsEnabled,
            HitCount = rule.HitCount,
            LastHitAt = rule.LastHitAt,
            CreatedAt = rule.CreatedAt
        };
    }

    private static void ApplyRuleEdits(PopupRule editedRule, PopupRule sourceRule)
    {
        sourceRule.Name = editedRule.Name;
        sourceRule.ProcessName = editedRule.ProcessName;
        sourceRule.ProcessPath = editedRule.ProcessPath;
        sourceRule.WindowClass = editedRule.WindowClass;
        sourceRule.TitlePattern = editedRule.TitlePattern;
        sourceRule.TitleMatchMode = editedRule.TitleMatchMode;
        sourceRule.Action = editedRule.Action;
    }

    private void OpenDataDirectory()
    {
        Directory.CreateDirectory(dataDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = dataDirectory,
            UseShellExecute = true
        });
    }

    private void RaiseSummaryProperties()
    {
        OnPropertyChanged(nameof(RuleCountText));
        OnPropertyChanged(nameof(ContextCountText));
        OnPropertyChanged(nameof(StartupCountText));
        OnPropertyChanged(nameof(BlockCountText));
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private static bool MatchesSearch(string searchText, params string[] values)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return values.Any(value => value?.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) == true);
    }

    private static string FormatOperationResult(OperationResult result, string entryName, bool enabled)
    {
        if (result.Succeeded)
        {
            return enabled ? $"已启用：{entryName}" : $"已禁用并保存恢复信息：{entryName}";
        }

        return result.RequiresElevation
            ? "此项目需要管理员权限，请在设置页以管理员身份重启。"
            : result.Message;
    }

    private void HandleCommandError(Exception exception)
    {
        StatusMessage = $"操作失败：{exception.Message}";
    }
}
