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
    private readonly IContextMenuManager contextMenuManager;
    private readonly IStartupManager startupManager;
    private readonly StartupRegistrationService startupRegistration;
    private readonly string executablePath;
    private readonly string dataDirectory;
    private readonly object settingsSaveLock = new();

    private AppSettings settings = new();
    private Task pendingSettingsSave = Task.CompletedTask;
    private WindowInfo? selectedWindow;
    private PopupRule? selectedPopupRuleSource;
    private PopupRule? selectedPopupRule;
    private string windowSearchText = string.Empty;
    private string contextSearchText = string.Empty;
    private string startupSearchText = string.Empty;
    private string statusMessage = "正在初始化...";
    private bool isPopupBlockingEnabled;
    private bool startWithWindows;
    private bool minimizeToTray = true;
    private bool isBusy;
    private bool initialized;
    private bool disposed;

    public MainViewModel(
        ISettingsStore settingsStore,
        IWindowCatalog windowCatalog,
        IPopupBlocker popupBlocker,
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
        this.contextMenuManager = contextMenuManager;
        this.startupManager = startupManager;
        this.startupRegistration = startupRegistration;
        this.executablePath = executablePath;
        this.dataDirectory = dataDirectory;

        RefreshWindowsCommand = new RelayCommand(RefreshWindows);
        AddRuleFromWindowCommand = new AsyncRelayCommand(AddRuleFromWindowAsync, () => SelectedWindow is not null, HandleCommandError);
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
    }

    public ObservableCollection<WindowInfo> VisibleWindows { get; } = [];

    public ObservableCollection<PopupRule> PopupRules { get; } = [];

    public ObservableCollection<ContextMenuEntry> ContextMenuEntries { get; } = [];

    public ObservableCollection<StartupEntry> StartupEntries { get; } = [];

    public ObservableCollection<ActivityItem> RecentActivity { get; } = [];

    public RelayCommand RefreshWindowsCommand { get; }

    public AsyncRelayCommand AddRuleFromWindowCommand { get; }

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
        startWithWindows = startupRegistration.IsEnabled(executablePath);
        settings.StartWithWindows = startWithWindows;
        minimizeToTray = settings.MinimizeToTray;
        OnPropertyChanged(nameof(IsPopupBlockingEnabled));
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(MinimizeToTray));
        OnPropertyChanged(nameof(ProtectionStatus));
        RaiseSummaryProperties();

        initialized = true;
        ApplyPopupEngineState();
        ConfigureCollectionFilters();
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
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        popupBlocker.PopupBlocked -= OnPopupBlocked;
        popupBlocker.Dispose();

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

        disposed = true;
    }

    private void ConfigureCollectionFilters()
    {
        CollectionViewSource.GetDefaultView(VisibleWindows).Filter = item =>
            item is WindowInfo window && MatchesSearch(WindowSearchText, window.ProcessName, window.Title, window.ClassName);

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

        var title = SelectedWindow.Title;
        var shortTitle = title.Length > 28 ? $"{title[..28]}..." : title;
        var rule = new PopupRule
        {
            Name = string.IsNullOrWhiteSpace(shortTitle)
                ? $"{SelectedWindow.ProcessName} 窗口"
                : $"{SelectedWindow.ProcessName} - {shortTitle}",
            ProcessName = SelectedWindow.ProcessName,
            ProcessPath = SelectedWindow.ProcessPath,
            WindowClass = SelectedWindow.ClassName,
            TitlePattern = title,
            TitleMatchMode = TextMatchMode.Exact,
            Action = PopupAction.Close
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
        StatusMessage = "已创建规则。可在下方调整匹配方式和动作。";
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
