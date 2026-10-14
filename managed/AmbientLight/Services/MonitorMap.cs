// MonitorMap.cs — 物理显示器几何 (全屏校准窗定位用)
// 核心的 AC_GetMonitors 只有分辨率没有原点; 这里用 EnumDisplayMonitors 补齐,
// 设备名 (\\.\DISPLAYn) 与核心枚举一致可直接匹配。DIP 换算依赖 PerMonitorV2 manifest。
using System.Runtime.InteropServices;

namespace AmbientLight.Services;

public static class MonitorMap
{
    public readonly record struct Info(string Device, Rect Px, double DpiX, double DpiY, bool Primary);

    public readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref NativeRect rect, int data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, int data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int type, ref uint dpiX, ref uint dpiY);

    /// <summary>全部在位显示器 (顺序 = EnumDisplayMonitors, 与核心枚举顺序可能不同,
    /// 必须按 Device 名匹配); 枚举失败返回空表。</summary>
    public static List<Info> All()
    {
        var list = new List<Info>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, ref _, _) =>
            {
                var mi = new MonitorInfoEx();
                mi.cbSize = Marshal.SizeOf<MonitorInfoEx>();
                if (GetMonitorInfoW(hMon, ref mi))
                {
                    uint dx = 96, dy = 96;
                    GetDpiForMonitor(hMon, 0 /*MDT_EFFECTIVE_DPI*/, ref dx, ref dy);
                    list.Add(new Info(mi.szDevice,
                        new Rect(mi.rcMonitor.L, mi.rcMonitor.T, mi.rcMonitor.R, mi.rcMonitor.B),
                        dx <= 0 ? 96 : dx, dy <= 0 ? 96 : dy,
                        (mi.dwFlags & 1) != 0 /*MONITORINFOF_PRIMARY*/));
                }
                return true;
            }, 0);
        }
        catch { /* 理论不抛; 失败时调用方回退主屏 */ }
        return list;
    }

    /// <summary>枚举数组里主屏的下标 (没有主屏标记 → 0)。
    /// 与核心 capture::CaptureSession::PickOutput 的"配置越界 → 回退主屏"目标保持同一语义 ——
    /// UI 修正"配置的显示器已不在位"时用它, 保证 下拉显示 / 配置 / 核心实际使用 三者一致。</summary>
    public static int PrimaryIndexOf(Interop.ACMonitorInfo[] mons)
    {
        for (int i = 0; i < mons.Length; i++)
            if (mons[i].primary != 0) return i;
        return 0;
    }
}
