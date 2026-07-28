using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace DeskHush.App.Infrastructure;

internal sealed record DesktopCaptureFrame(BitmapSource Image, Rectangle Bounds);

internal static class DesktopCaptureService
{
    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    internal static DesktopCaptureFrame CaptureVirtualDesktop()
    {
        var bounds = Forms.SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidOperationException("无法读取桌面显示区域。");
        }

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var destinationDeviceContext = graphics.GetHdc();
            try
            {
                var desktopDeviceContext = GetDC(nint.Zero);
                if (desktopDeviceContext == nint.Zero)
                {
                    throw new InvalidOperationException("无法访问桌面显示区域。");
                }

                try
                {
                    if (!BitBlt(
                            destinationDeviceContext,
                            0,
                            0,
                            bounds.Width,
                            bounds.Height,
                            desktopDeviceContext,
                            bounds.Left,
                            bounds.Top,
                            SourceCopy | CaptureBlt))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "无法捕获桌面画面。");
                    }
                }
                finally
                {
                    _ = ReleaseDC(nint.Zero, desktopDeviceContext);
                }
            }
            finally
            {
                graphics.ReleaseHdc(destinationDeviceContext);
            }
        }

        var bitmapHandle = bitmap.GetHbitmap();
        try
        {
            var image = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                nint.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return new DesktopCaptureFrame(image, bounds);
        }
        finally
        {
            _ = DeleteObject(bitmapHandle);
        }
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        nint destinationDeviceContext,
        int destinationX,
        int destinationY,
        int width,
        int height,
        nint sourceDeviceContext,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint windowHandle, nint deviceContext);
}
