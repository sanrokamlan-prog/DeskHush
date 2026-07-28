using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskHush.App.Infrastructure;
using DeskHush.App.ViewModels;

namespace DeskHush.App;

public partial class MainWindow : Window
{
    private static readonly string[] PageTitles = ["概览", "弹窗拦截", "右键菜单", "启动项", "设置"];
    private static readonly string[] PageSummaryBindings = ["ProtectionStatus", "RuleCountText", "ContextCountText", "StartupCountText", "AdministratorStatus"];

    private readonly MainViewModel viewModel;
    private readonly DesktopWindowPicker desktopWindowPicker;
    private readonly bool startHidden;
    private readonly string? screenshotPath;
    private readonly int initialPage;
    private bool allowClose;
    private bool showRequested;

    public MainWindow(
        MainViewModel viewModel,
        DesktopWindowPicker desktopWindowPicker,
        bool startHidden,
        string? screenshotPath = null,
        int initialPage = 0)
    {
        this.viewModel = viewModel;
        this.desktopWindowPicker = desktopWindowPicker;
        this.startHidden = startHidden;
        this.screenshotPath = screenshotPath;
        this.initialPage = Math.Clamp(initialPage, 0, PageTitles.Length - 1);

        InitializeComponent();
        DataContext = viewModel;
        if (startHidden)
        {
            Opacity = 0;
            ShowActivated = false;
            ShowInTaskbar = false;
        }

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    public void ShowAndActivate()
    {
        showRequested = true;
        Opacity = 1;
        ShowActivated = true;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void ForceClose()
    {
        allowClose = true;
        Close();
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            await viewModel.InitializeAsync();
            SelectPage(initialPage);
            if (!string.IsNullOrWhiteSpace(screenshotPath))
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                CaptureClientArea(screenshotPath);
                ((App)System.Windows.Application.Current).ExitAfterQaCapture();
                return;
            }

            if (startHidden && !showRequested)
            {
                Hide();
                Opacity = 1;
                ShowActivated = true;
                ShowInTaskbar = true;
            }
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(this, exception.Message, "DeskHush 初始化失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Navigation_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string tag } || !int.TryParse(tag, out var index) || index < 0 || index >= MainTabs.Items.Count)
        {
            return;
        }

        SelectPage(index);
    }

    private async void CaptureWindow_Click(object sender, RoutedEventArgs eventArgs)
    {
        var captureButton = sender as System.Windows.Controls.Button;
        if (captureButton is not null)
        {
            captureButton.IsEnabled = false;
        }

        try
        {
            var selectedWindow = await desktopWindowPicker.PickWindowAsync(this);
            if (selectedWindow is not null)
            {
                await viewModel.CreateRuleFromWindowAsync(selectedWindow);
            }
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                this,
                $"桌面抓取失败：{exception.Message}",
                "DeskHush",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            if (captureButton is not null)
            {
                captureButton.IsEnabled = true;
            }
        }
    }

    private void SelectPage(int index)
    {
        MainTabs.SelectedIndex = index;
        PageTitle.Text = PageTitles[index];
        BindingOperations.SetBinding(PageSummary, System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(PageSummaryBindings[index]));
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (viewModel.MinimizeToTray)
        {
            Hide();
            return;
        }

        _ = Dispatcher.BeginInvoke(() => ((App)System.Windows.Application.Current).ExitApplication());
    }

    private void OnStateChanged(object? sender, EventArgs eventArgs)
    {
        if (WindowState == WindowState.Minimized && viewModel.MinimizeToTray)
        {
            Hide();
        }
    }

    private void CaptureClientArea(string path)
    {
        UpdateLayout();
        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1d;
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1d;
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * scaleX));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * scaleY));
        var bitmap = new RenderTargetBitmap(width, height, 96 * scaleX, 96 * scaleY, PixelFormats.Pbgra32);
        bitmap.Render(this);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
