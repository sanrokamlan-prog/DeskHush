using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeskHush.Core.Models;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using DrawingRectangle = System.Drawing.Rectangle;
using Image = System.Windows.Controls.Image;
using Panel = System.Windows.Controls.Panel;
using ShapeRectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;

namespace DeskHush.App.Infrastructure;

internal sealed class DesktopWindowPickerWindow : Window
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly nint HwndTopmost = new(-1);

    private readonly DesktopCaptureFrame frame;
    private readonly IReadOnlyList<WindowInfo> candidates;
    private readonly Canvas overlay = new();
    private readonly Border candidateLabel;
    private readonly TextBlock candidateLabelText;
    private readonly Dictionary<nint, ShapeRectangle> outlines = [];
    private WindowInfo? hoveredWindow;

    internal DesktopWindowPickerWindow(DesktopCaptureFrame frame, IReadOnlyList<WindowInfo> candidates)
    {
        this.frame = frame;
        this.candidates = OrderByZIndex(candidates)
            .Where(IsInsideCapturedDesktop)
            .ToArray();

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = Math.Max(1, SystemParameters.VirtualScreenWidth);
        Height = Math.Max(1, SystemParameters.VirtualScreenHeight);

        var root = new Grid
        {
            Background = Brushes.Black,
            SnapsToDevicePixels = true
        };
        root.Children.Add(new Image
        {
            Source = frame.Image,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        });

        overlay.Background = Brushes.Transparent;
        overlay.Cursor = Cursors.Cross;
        overlay.Focusable = true;
        root.Children.Add(overlay);

        var title = new Border
        {
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(225, 25, 29, 30)),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "选择窗口",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            }
        };
        root.Children.Add(title);

        candidateLabelText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 520
        };
        candidateLabel = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            Background = new SolidColorBrush(Color.FromArgb(235, 25, 29, 30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(21, 143, 131)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = candidateLabelText
        };
        overlay.Children.Add(candidateLabel);

        Content = root;
        SourceInitialized += (_, _) => ApplyPhysicalDesktopBounds();
        Loaded += OnLoaded;
        SizeChanged += (_, _) => ArrangeOutlines();
        PreviewKeyDown += OnPreviewKeyDown;
        overlay.MouseMove += OnMouseMove;
        overlay.MouseLeftButtonDown += OnMouseLeftButtonDown;
        overlay.MouseRightButtonDown += (_, _) => Cancel();
    }

    internal WindowInfo? SelectedWindow { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        CreateOutlines();
        Activate();
        _ = overlay.Focus();
    }

    private void CreateOutlines()
    {
        foreach (var candidate in candidates.Reverse())
        {
            var outline = new ShapeRectangle
            {
                Fill = Brushes.Transparent,
                Stroke = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            outlines[candidate.Handle] = outline;
            overlay.Children.Insert(Math.Max(0, overlay.Children.Count - 1), outline);
        }

        ArrangeOutlines();
    }

    private void ArrangeOutlines()
    {
        foreach (var candidate in candidates)
        {
            if (!outlines.TryGetValue(candidate.Handle, out var outline))
            {
                continue;
            }

            var bounds = GetDisplayBounds(candidate);
            Canvas.SetLeft(outline, bounds.Left);
            Canvas.SetTop(outline, bounds.Top);
            outline.Width = Math.Max(0, bounds.Width);
            outline.Height = Math.Max(0, bounds.Height);
        }

        if (hoveredWindow is not null)
        {
            PositionCandidateLabel(hoveredWindow);
        }
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs eventArgs)
    {
        var point = eventArgs.GetPosition(overlay);
        var candidate = candidates.FirstOrDefault(window => GetDisplayBounds(window).Contains(point));
        if (candidate?.Handle == hoveredWindow?.Handle)
        {
            return;
        }

        SetHoveredWindow(candidate);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (hoveredWindow is null)
        {
            return;
        }

        SelectedWindow = hoveredWindow;
        DialogResult = true;
        eventArgs.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape)
        {
            return;
        }

        Cancel();
        eventArgs.Handled = true;
    }

    private void Cancel()
    {
        DialogResult = false;
    }

    private void SetHoveredWindow(WindowInfo? candidate)
    {
        if (hoveredWindow is not null && outlines.TryGetValue(hoveredWindow.Handle, out var oldOutline))
        {
            oldOutline.Fill = Brushes.Transparent;
            oldOutline.Stroke = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
            oldOutline.StrokeThickness = 1;
        }

        hoveredWindow = candidate;
        if (candidate is null)
        {
            candidateLabel.Visibility = Visibility.Collapsed;
            return;
        }

        if (outlines.TryGetValue(candidate.Handle, out var outline))
        {
            outline.Fill = new SolidColorBrush(Color.FromArgb(45, 21, 143, 131));
            outline.Stroke = new SolidColorBrush(Color.FromRgb(21, 143, 131));
            outline.StrokeThickness = 3;
        }

        var title = string.IsNullOrWhiteSpace(candidate.Title) ? candidate.ClassName : candidate.Title;
        candidateLabelText.Text = $"{candidate.ProcessName}  {title}\n{candidate.Width} x {candidate.Height}  ({candidate.Left}, {candidate.Top})";
        candidateLabel.Visibility = Visibility.Visible;
        PositionCandidateLabel(candidate);
    }

    private void PositionCandidateLabel(WindowInfo candidate)
    {
        var bounds = GetDisplayBounds(candidate);
        candidateLabel.Measure(new Size(Math.Max(1, overlay.ActualWidth), Math.Max(1, overlay.ActualHeight)));
        var labelWidth = candidateLabel.DesiredSize.Width;
        var labelHeight = candidateLabel.DesiredSize.Height;
        var left = Math.Clamp(bounds.Left, 8, Math.Max(8, overlay.ActualWidth - labelWidth - 8));
        var preferredTop = bounds.Top - labelHeight - 8;
        var top = preferredTop >= 8
            ? preferredTop
            : Math.Min(bounds.Bottom + 8, Math.Max(8, overlay.ActualHeight - labelHeight - 8));

        Canvas.SetLeft(candidateLabel, left);
        Canvas.SetTop(candidateLabel, top);
        Panel.SetZIndex(candidateLabel, int.MaxValue);
    }

    private Rect GetDisplayBounds(WindowInfo candidate)
    {
        if (overlay.ActualWidth <= 0 || overlay.ActualHeight <= 0)
        {
            return Rect.Empty;
        }

        var captured = frame.Bounds;
        var left = Math.Max(candidate.Left, captured.Left);
        var top = Math.Max(candidate.Top, captured.Top);
        var right = Math.Min((long)candidate.Left + candidate.Width, captured.Right);
        var bottom = Math.Min((long)candidate.Top + candidate.Height, captured.Bottom);
        if (right <= left || bottom <= top)
        {
            return Rect.Empty;
        }

        var scaleX = overlay.ActualWidth / captured.Width;
        var scaleY = overlay.ActualHeight / captured.Height;
        return new Rect(
            (left - captured.Left) * scaleX,
            (top - captured.Top) * scaleY,
            (right - left) * scaleX,
            (bottom - top) * scaleY);
    }

    private bool IsInsideCapturedDesktop(WindowInfo candidate)
    {
        if (candidate.Width <= 0 || candidate.Height <= 0)
        {
            return false;
        }

        var candidateBounds = new DrawingRectangle(candidate.Left, candidate.Top, candidate.Width, candidate.Height);
        return candidateBounds.IntersectsWith(frame.Bounds);
    }

    private void ApplyPhysicalDesktopBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        _ = SetWindowPos(
            handle,
            HwndTopmost,
            frame.Bounds.Left,
            frame.Bounds.Top,
            frame.Bounds.Width,
            frame.Bounds.Height,
            SwpNoActivate | SwpNoOwnerZOrder);
    }

    private static IReadOnlyList<WindowInfo> OrderByZIndex(IReadOnlyList<WindowInfo> candidates)
    {
        var remaining = candidates
            .GroupBy(window => window.Handle)
            .ToDictionary(group => group.Key, group => group.First());
        var ordered = new List<WindowInfo>(remaining.Count);
        _ = EnumWindows((windowHandle, _) =>
        {
            if (remaining.Remove(windowHandle, out var candidate))
            {
                ordered.Add(candidate);
            }

            return true;
        }, nint.Zero);
        ordered.AddRange(remaining.Values);
        return ordered;
    }

    private delegate bool EnumWindowsProc(nint windowHandle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int left,
        int top,
        int width,
        int height,
        uint flags);
}
