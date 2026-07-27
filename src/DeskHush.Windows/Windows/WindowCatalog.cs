using System.Diagnostics;
using System.Text;
using DeskHush.Core.Interfaces;
using DeskHush.Core.Models;
using DeskHush.Windows.Interop;

namespace DeskHush.Windows.Windows;

public sealed class WindowCatalog : IWindowCatalog
{
    private readonly int currentProcessId = Environment.ProcessId;

    public IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();
        NativeMethods.EnumWindows((windowHandle, _) =>
        {
            var window = TryGetWindow(windowHandle);
            if (window is not null)
            {
                windows.Add(window);
            }

            return true;
        }, nint.Zero);

        return windows
            .OrderBy(window => window.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public WindowInfo? TryGetWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero ||
            !NativeMethods.IsWindow(windowHandle) ||
            !NativeMethods.IsWindowVisible(windowHandle) ||
            NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRoot) != windowHandle ||
            IsCloaked(windowHandle))
        {
            return null;
        }

        _ = NativeMethods.GetWindowThreadProcessId(windowHandle, out var rawProcessId);
        if (rawProcessId == 0 || rawProcessId == currentProcessId || rawProcessId > int.MaxValue)
        {
            return null;
        }

        var title = ReadWindowText(windowHandle);
        var className = ReadClassName(windowHandle);
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(className))
        {
            return null;
        }

        if (!NativeMethods.GetWindowRect(windowHandle, out var rectangle))
        {
            return null;
        }

        var processId = (int)rawProcessId;
        var processPath = ReadProcessPath(rawProcessId);
        var processName = Path.GetFileNameWithoutExtension(processPath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            try
            {
                processName = Process.GetProcessById(processId).ProcessName;
            }
            catch (ArgumentException)
            {
                processName = $"PID {processId}";
            }
        }

        return new WindowInfo(
            windowHandle,
            processId,
            processName,
            processPath,
            title,
            className,
            rectangle.Left,
            rectangle.Top,
            Math.Max(0, rectangle.Right - rectangle.Left),
            Math.Max(0, rectangle.Bottom - rectangle.Top));
    }

    private static bool IsCloaked(nint windowHandle)
    {
        try
        {
            return NativeMethods.DwmGetWindowAttribute(
                       windowHandle,
                       NativeMethods.DwmwaCloaked,
                       out var cloaked,
                       sizeof(int)) == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static string ReadWindowText(nint windowHandle)
    {
        var length = Math.Min(NativeMethods.GetWindowTextLength(windowHandle), 4096);
        var text = new StringBuilder(length + 1);
        _ = NativeMethods.GetWindowText(windowHandle, text, text.Capacity);
        return text.ToString().Trim();
    }

    private static string ReadClassName(nint windowHandle)
    {
        var className = new StringBuilder(512);
        _ = NativeMethods.GetClassName(windowHandle, className, className.Capacity);
        return className.ToString().Trim();
    }

    private static string ReadProcessPath(uint processId)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (process == nint.Zero)
        {
            return string.Empty;
        }

        try
        {
            var capacity = 32768u;
            var fileName = new StringBuilder((int)capacity);
            return NativeMethods.QueryFullProcessImageName(process, 0, fileName, ref capacity)
                ? fileName.ToString()
                : string.Empty;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(process);
        }
    }
}
