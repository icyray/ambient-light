// SerialPorts.cs — 枚举本机所有 COM 口 (给高级页「连接 · 端口」的手动指定用)。
// 为什么不复用 native 的 serial::EnumerateVidPidPorts: 那个是**按 VID_2E3C&PID_5740 过滤**的,
// 而"手动指定端口"要解决的恰恰是"认不到 VID/PID"的场景 (换芯片、USB 转串口桥接、两根同型灯带)。
// 也不引 System.IO.Ports: 那是额外 NuGet 包, 而我们只要名字。
using Microsoft.Win32;

namespace AmbientLight.Services;

public static class SerialPorts
{
    /// <summary>本机所有 COM 口名 (形如 "COM5"), 按**数值**排序 (COM10 排在 COM9 之后, 而不是字典序)。
    /// 数据源 `HKLM\HARDWARE\DEVICEMAP\SERIALCOMM` —— 这是"能被 CreateFile 打开的串口"的权威列表,
    /// 比按类枚举设备节点更省事; 读不到时返回空 (下拉里只剩"自动探测", 不影响其它功能)。</summary>
    public static string[] All()
    {
        var list = new List<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key != null)
            {
                foreach (var valueName in key.GetValueNames())
                    if (key.GetValue(valueName) is string port && port.Length > 0)
                        list.Add(port);
            }
        }
        catch
        {
            // 注册表不可读 (极少数受限环境) → 空列表, 由调用方兜底
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(PortNumber)
                   .ToArray();
    }

    /// <summary>COM 号 (非 "COMxx" 形状的排到最后, 保持稳定顺序)</summary>
    private static int PortNumber(string port)
        => port.Length > 3 && int.TryParse(port.AsSpan(3), out int n) ? n : int.MaxValue;
}
