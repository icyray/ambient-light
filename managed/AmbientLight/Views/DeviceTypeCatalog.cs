// Views/DeviceTypeCatalog.cs — 设备类型表 (**唯一事实源**): 握手应答里的类型号 → 显示名键 + 图标。
//
// 以前这两件事是**两处并列 switch** (`HomePage.Rows.cs` 的 DevIcon / DevTypeName) ——
// 加一种设备类型要同时改两处, 而"只改名字忘了图标"是很容易发生且**静默**的:
// 图标会退成通用 USB 图标, 看着像"它就是个 USB 设备", 不像 bug。
// 收进一张表后: 加类型 = 表一行 + 两本字典各一个 `Dev.Type.<x>` 键 (名字与图标天生同步)。
using AmbientLight.Interop;
using AmbientLight.Localization;
using Wpf.Ui.Controls;

namespace AmbientLight.Views;

/// <summary>一行设备类型定义。</summary>
/// <param name="Id">握手应答里的类型号 (见 <see cref="AmbientCore.DevScreen"/> 等常量)。</param>
/// <param name="NameKey">显示名的文案键。</param>
/// <param name="Icon">设备行图标。</param>
public sealed record DevTypeDef(int Id, string NameKey, SymbolRegular Icon);

public static class DeviceTypeCatalog
{
    /// <summary>★ **设备类型表 = 唯一事实源**。加一种硬件: 这里加一行 + 两本字典各加一个键。
    /// 类型号来自固件握手应答字节, 我们只能照抄 (不能自动发现) —— 表的价值是"只写一处 + 漏了会响"。</summary>
    public static readonly DevTypeDef[] Types =
    [
        new(AmbientCore.DevScreen, "Dev.Type.Screen", SymbolRegular.Desktop24),
        new(AmbientCore.DevFan, "Dev.Type.Fan", SymbolRegular.PuzzleCube24),
        new(AmbientCore.DevDesk, "Dev.Type.Desk", SymbolRegular.RectangleLandscape24),
        new(AmbientCore.DevSound, "Dev.Type.Sound", SymbolRegular.RectanglePortrait24),
    ];

    /// <summary>表外类型号 (厂商出了新硬件): 显示「未知」+ 通用 USB 图标 —— 不猜它是什么。</summary>
    public const string UnknownNameKey = "Dev.Type.Unknown";
    public static readonly SymbolRegular UnknownIcon = SymbolRegular.UsbPlug24;

    /// <summary>查表; 表外的号返回 null (调用点用下面的 Name/Icon, 不必自己判空)。</summary>
    public static DevTypeDef? Of(int t) => Types.FirstOrDefault(d => d.Id == t);

    /// <summary>类型显示名 (设备行 + banner 端口行共用)。</summary>
    public static string Name(int t) => Loc.T(Of(t)?.NameKey ?? UnknownNameKey);

    /// <summary>类型图标 (设备行)。</summary>
    public static SymbolRegular Icon(int t) => Of(t)?.Icon ?? UnknownIcon;
}
