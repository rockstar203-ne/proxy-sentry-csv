using System.Runtime.InteropServices;
using ProxySentryCsv.Models;

namespace ProxySentryCsv.Services;

public static class SyncMonitorProvider
{
    public static IReadOnlyList<SyncMonitorOption> GetMonitors()
    {
        var monitors = new List<SyncMonitorOption>();
        var index = 1;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx();
            info.cbSize = Marshal.SizeOf<MonitorInfoEx>();
            info.szDevice = new string('\0', DeviceNameChars);
            if (!GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            var width = info.rcMonitor.Right - info.rcMonitor.Left;
            var height = info.rcMonitor.Bottom - info.rcMonitor.Top;
            var workWidth = info.rcWork.Right - info.rcWork.Left;
            var workHeight = info.rcWork.Bottom - info.rcWork.Top;
            var isPrimary = (info.dwFlags & MonitorInfoPrimary) == MonitorInfoPrimary;
            var deviceName = (info.szDevice ?? "").TrimEnd('\0');
            var id = string.IsNullOrWhiteSpace(deviceName) ? $"monitor-{index}" : deviceName;

            monitors.Add(new SyncMonitorOption
            {
                Id = id,
                DisplayName = $"Monitor {index}{(isPrimary ? " - Primary" : "")}: {width}x{height} @ {info.rcMonitor.Left},{info.rcMonitor.Top}",
                AvailLeft = info.rcWork.Left,
                AvailTop = info.rcWork.Top,
                AvailWidth = workWidth,
                AvailHeight = workHeight,
                Width = width,
                Height = height,
                IsPrimary = isPrimary
            });

            index++;
            return true;
        }, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            monitors.Add(new SyncMonitorOption
            {
                Id = "primary",
                DisplayName = "Primary monitor",
                AvailLeft = 0,
                AvailTop = 0,
                AvailWidth = 1920,
                AvailHeight = 1080,
                Width = 1920,
                Height = 1080,
                IsPrimary = true
            });
        }

        return monitors;
    }

    private const int MonitorInfoPrimary = 1;
    private const int DeviceNameChars = 32;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdcMonitor, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public int dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DeviceNameChars)]
        public string szDevice;
    }
}
