using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
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
    private MainWindow? mainWindow;
    private MainViewModel? viewModel;
    private bool exiting;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (!WaitForPreviousProcess(eventArgs.Args))
        {
            Shutdown();
            return;
        }

        instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(ShowEventName);
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
        var contextMenuManager = new WindowsContextMenuManager(dataDirectory);
        var startupManager = new WindowsStartupManager(dataDirectory);
        var settingsStore = new JsonSettingsStore(Path.Combine(dataDirectory, "settings.json"));
        var startupRegistration = new StartupRegistrationService();

        viewModel = new MainViewModel(
            settingsStore,
            windowCatalog,
            popupBlocker,
            contextMenuManager,
            startupManager,
            startupRegistration,
            executablePath,
            dataDirectory,
            () => RestartElevated(executablePath));

        var startHidden = eventArgs.Args.Any(argument => argument.Equals("--background", StringComparison.OrdinalIgnoreCase));
        var screenshotPath = eventArgs.Args
            .FirstOrDefault(argument => argument.StartsWith("--qa-screenshot=", StringComparison.OrdinalIgnoreCase))?
            ["--qa-screenshot=".Length..];
        var tabArgument = eventArgs.Args
            .FirstOrDefault(argument => argument.StartsWith("--qa-tab=", StringComparison.OrdinalIgnoreCase));
        var qaTab = tabArgument is not null && int.TryParse(tabArgument["--qa-tab=".Length..], out var parsedTab)
            ? parsedTab
            : 0;
        mainWindow = new MainWindow(viewModel, startHidden: startHidden && screenshotPath is null, screenshotPath, qaTab);
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
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        showEventRegistration = ThreadPool.RegisterWaitForSingleObject(
            showEvent,
            (_, _) => Dispatcher.BeginInvoke(ShowMainWindow),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    private void CreateTrayIcon()
    {
        notifyIcon = new WinForms.NotifyIcon
        {
            Icon = SystemIcons.Shield,
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
