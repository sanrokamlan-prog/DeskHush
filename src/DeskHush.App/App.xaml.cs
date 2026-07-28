using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using DeskHush.App.Infrastructure;
using DeskHush.App.ViewModels;
using DeskHush.Core.Services;
using DeskHush.Windows.ContextMenu;
using DeskHush.Windows.Popup;
using DeskHush.Windows.Startup;
using DeskHush.Windows.SystemIntegration;
using DeskHush.Windows.Windows;
using WinForms = System.Windows.Forms;

namespace DeskHush.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\DeskHush.SingleInstance";
    private const string ShowEventName = @"Local\DeskHush.ShowWindow";

    private Mutex? instanceMutex;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showEventRegistration;
    private WinForms.NotifyIcon? notifyIcon;
    private Icon? trayIcon;
    private MainWindow? mainWindow;
    private MainViewModel? viewModel;
    private Uri? availableUpdateUri;
    private string activeMutexName = MutexName;
    private string activeShowEventName = ShowEventName;
    private bool exiting;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (eventArgs.Args.Any(argument => argument.StartsWith("--qa-", StringComparison.OrdinalIgnoreCase)))
        {
            var qaSuffix = $".QA.{Environment.ProcessId}";
            activeMutexName += qaSuffix;
            activeShowEventName += qaSuffix;
        }

        if (!WaitForPreviousProcess(eventArgs.Args))
        {
            Shutdown();
            return;
        }

        instanceMutex = new Mutex(initiallyOwned: true, activeMutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(activeShowEventName);
                existingEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                System.Windows.MessageBox.Show("DeskHush 已在运行。", "DeskHush", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        var configuredDataDirectory = Environment.GetEnvironmentVariable("DESKHUSH_DATA_DIR");
        var dataDirectory = string.IsNullOrWhiteSpace(configuredDataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskHush")
            : Path.GetFullPath(configuredDataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var executablePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DeskHush.exe");

        var windowCatalog = new WindowCatalog();
        var popupBlocker = new WinEventPopupBlocker(windowCatalog);
        var windowRecorder = new WinEventWindowRecorder(windowCatalog);
        var desktopWindowPicker = new DesktopWindowPicker(windowCatalog);
        var contextMenuManager = new WindowsContextMenuManager(dataDirectory);
        var startupManager = new WindowsStartupManager(dataDirectory);
        var settingsStore = new JsonSettingsStore(Path.Combine(dataDirectory, "settings.json"));
        var updateChecker = new GitHubReleaseUpdateChecker();
        var startupRegistration = new StartupRegistrationService();
        var currentVersion = typeof(App).Assembly.GetName().Version ?? new Version(0, 2, 0);

        viewModel = new MainViewModel(
            settingsStore,
            windowCatalog,
            popupBlocker,
            windowRecorder,
            updateChecker,
            currentVersion,
            contextMenuManager,
            startupManager,
            startupRegistration,
            executablePath,
            dataDirectory,
            () => RestartElevated(executablePath));
        viewModel.UpdateAvailable += OnUpdateAvailable;

        var startHidden = eventArgs.Args.Any(argument => argument.Equals("--background", StringComparison.OrdinalIgnoreCase));
        var screenshotPath = eventArgs.Args
            .FirstOrDefault(argument => argument.StartsWith("--qa-screenshot=", StringComparison.OrdinalIgnoreCase))?
            ["--qa-screenshot=".Length..];
        var tabArgument = eventArgs.Args
            .FirstOrDefault(argument => argument.StartsWith("--qa-tab=", StringComparison.OrdinalIgnoreCase));
        var qaTab = tabArgument is not null && int.TryParse(tabArgument["--qa-tab=".Length..], out var parsedTab)
            ? parsedTab
            : 0;
        mainWindow = new MainWindow(
            viewModel,
            desktopWindowPicker,
            startHidden: startHidden && screenshotPath is null,
            screenshotPath,
            qaTab);
        if (eventArgs.Args.Any(argument => argument.Equals("--qa-compact", StringComparison.OrdinalIgnoreCase)))
        {
            mainWindow.Width = mainWindow.MinWidth;
            mainWindow.Height = mainWindow.MinHeight;
        }
        MainWindow = mainWindow;

        CreateTrayIcon();
        RegisterShowEvent();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        showEventRegistration?.Unregister(null);
        showEvent?.Dispose();
        notifyIcon?.Dispose();
        trayIcon?.Dispose();
        if (viewModel is not null)
        {
            viewModel.UpdateAvailable -= OnUpdateAvailable;
        }
        viewModel?.Dispose();

        if (instanceMutex is not null)
        {
            try
            {
                instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            instanceMutex.Dispose();
        }

        base.OnExit(eventArgs);
    }

    private void RegisterShowEvent()
    {
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, activeShowEventName);
        showEventRegistration = ThreadPool.RegisterWaitForSingleObject(
            showEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowMainWindow),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    private void CreateTrayIcon()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            trayIcon = Icon.ExtractAssociatedIcon(processPath);
        }

        notifyIcon = new WinForms.NotifyIcon
        {
            Icon = trayIcon ?? SystemIcons.Application,
            Text = "DeskHush",
            Visible = true
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开 DeskHush", null, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow));
        menu.Items.Add("切换弹窗拦截", null, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (viewModel is not null)
            {
                viewModel.IsPopupBlockingEnabled = !viewModel.IsPopupBlockingEnabled;
            }
        }));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(ExitApplication));
        notifyIcon.ContextMenuStrip = menu;
        notifyIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowMainWindow);
        notifyIcon.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(OpenAvailableUpdate);
    }

    private void OnUpdateAvailable(object? sender, DeskHush.Core.Models.UpdateInfo update)
    {
        availableUpdateUri = update.ReleaseUri;
        notifyIcon?.ShowBalloonTip(
            5000,
            "DeskHush 有新版本",
            $"{update.TagName} 已发布，点击查看。",
            WinForms.ToolTipIcon.Info);
    }

    private void OpenAvailableUpdate()
    {
        if (availableUpdateUri is null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = availableUpdateUri.AbsoluteUri,
            UseShellExecute = true
        });
    }

    private void ShowMainWindow() => mainWindow?.ShowAndActivate();

    internal void ExitAfterQaCapture() => ExitApplication();

    private void RestartElevated(string executablePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--wait-for-process={Environment.ProcessId}"
            });
            ExitApplication();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user cancelled the UAC prompt.
        }
    }

    private static bool WaitForPreviousProcess(IReadOnlyCollection<string> arguments)
    {
        const string prefix = "--wait-for-process=";
        var argument = arguments.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (argument is null || !int.TryParse(argument[prefix.Length..], out var processId) || processId <= 0 || processId == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var previousProcess = Process.GetProcessById(processId);
            if (previousProcess.WaitForExit(15_000))
            {
                return true;
            }

            System.Windows.MessageBox.Show(
                "等待原 DeskHush 进程退出超时，管理员实例未启动。",
                "DeskHush",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    internal void ExitApplication()
    {
        if (exiting)
        {
            return;
        }

        exiting = true;
        if (notifyIcon is not null)
        {
            notifyIcon.Visible = false;
        }

        mainWindow?.ForceClose();
        Shutdown();
    }
}
