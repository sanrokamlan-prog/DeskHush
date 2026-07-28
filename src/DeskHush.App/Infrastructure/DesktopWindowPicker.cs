using System.Windows;
using System.Windows.Threading;
using DeskHush.Core.Interfaces;
using DeskHush.Core.Models;

namespace DeskHush.App.Infrastructure;

public sealed class DesktopWindowPicker
{
    private readonly IWindowCatalog windowCatalog;

    public DesktopWindowPicker(IWindowCatalog windowCatalog)
    {
        this.windowCatalog = windowCatalog;
    }

    public async Task<WindowInfo?> PickWindowAsync(Window owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (!owner.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("桌面抓取必须从界面线程启动。");
        }

        var restoreOwner = owner.IsVisible;
        var reactivateOwner = owner.IsActive;
        if (restoreOwner)
        {
            owner.Hide();
        }

        try
        {
            await owner.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle, cancellationToken);
            await Task.Delay(100, cancellationToken);

            var frame = DesktopCaptureService.CaptureVirtualDesktop();
            var candidates = windowCatalog.GetVisibleWindows();
            var picker = new DesktopWindowPickerWindow(frame, candidates);
            using var registration = cancellationToken.Register(() =>
                picker.Dispatcher.BeginInvoke(picker.Close));

            return picker.ShowDialog() == true ? picker.SelectedWindow : null;
        }
        finally
        {
            if (restoreOwner)
            {
                owner.Show();
                if (reactivateOwner)
                {
                    owner.Activate();
                }
            }
        }
    }
}
