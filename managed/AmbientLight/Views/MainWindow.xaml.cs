// MainWindow.xaml.cs — NavigationView 壳: 主页 / 采集 / 拾音 / 颜色 / 高级 页切换。
// 页面内容与状态都在各自 Page (Loaded 时从 ConfigStore 重读), 壳层只做四件事:
// ① 导航接线; ② Mica 背景; ③ 关窗进托盘; ④ 窗口变窄时自适应折叠左侧菜单 (WPF UI Gallery 同款)。
using System.Windows;
using Wpf.Ui.Controls;

namespace AmbientLight.Views;

public partial class MainWindow : FluentWindow
{
    private bool _allowClose;

    public bool AllowClose { get => _allowClose; set => _allowClose = value; }

    // ---- 自适应折叠左侧菜单 (轮 20; WPF UI 4.3.0 的 PaneDisplayMode 只有
    //      Left/LeftMinimal/LeftFluent/Top/Bottom, **没有 Auto**, 所以宽度自适应得自己写) ----
    // 规则: 窗口宽度**跨过**阈值时 —— 变窄则自动折叠 (仅当当前是展开的), 变宽则恢复;
    // **恢复只对"我们自动折的那一次"生效**, 用户自己折起来的菜单永不被自动展开。
    // 只认"跨过阈值"这个边沿、不在每次 SizeChanged 里强制判定: 否则用户在窄窗口下手动展开菜单,
    // 会被下一次尺寸变化立刻折回去 —— 那是和用户抢控制权。
    private const double NavAutoCollapseWidth = 740;   // 阈值 (窗口/内容区宽度, 想调手感改这里)
    private bool _paneAutoCollapsed;    // 当前的折叠态是"自动折的" → 变宽时该恢复
    private bool? _navWasWide;          // 上一次判定落在阈值哪一侧; null = 还没判定过

    public MainWindow()
    {
        InitializeComponent();
        // WPF-UI 的 TargetPageType 不自动导航: 点击时手动 Navigate; 启动先落到主页
        RootNav.ItemInvoked += (_, e) =>
        {
            if (e.OriginalSource is Wpf.Ui.Controls.NavigationViewItem { TargetPageType: { } page })
                RootNav.Navigate(page);
        };
        Loaded += (_, _) =>
        {
            if (RootNav.MenuItems[0] is Wpf.Ui.Controls.NavigationViewItem home)
                home.IsActive = true;           // SelectedItem setter 内部不可达, IsActive 等效
            RootNav.Navigate(typeof(HomePage));
            // M5.1 半透明: 按 WPF-UI 官方 WindowBackgroundManager.UpdateBackground 序列
            // (RemoveBackdrop → 窗口层透明 → ApplyBackdrop); Background 用本地值 Transparent
            // 直接盖过 FluentWindow 样式的 DynamicResource WindowBackground (不透明)。
            // 不支持 Mica 时保持不透明。浅色主题可读性在 5.2 主题轮走查。
            if (WindowBackdrop.IsSupported(WindowBackdropType.Mica))
            {
                WindowBackdrop.RemoveBackdrop(this);
                Background = System.Windows.Media.Brushes.Transparent;
                WindowBackdrop.RemoveBackground(this);
                WindowBackdrop.ApplyBackdrop(this, WindowBackdropType.Mica);
            }
        };
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;                    // 关窗 = 收进托盘, 退出走托盘菜单
            Hide();
        };
    }

    /// <summary>窗口宽度变化 → 自适应折叠/恢复左侧菜单。用 sender 取控件 (不在解析期碰生成字段)。</summary>
    private void OnRootNavSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not NavigationView nav) return;
        double w = e.NewSize.Width;
        if (w <= 1) return;                     // 布局完成前会报 0 宽, 不能当"变窄"
        bool wide = w >= NavAutoCollapseWidth;
        if (_navWasWide == wide) return;        // 只在跨过阈值的边沿动作
        _navWasWide = wide;

        var (setOpen, auto) = DecidePaneForWidth(wide, nav.IsPaneOpen, _paneAutoCollapsed);
        _paneAutoCollapsed = auto;
        if (setOpen is { } open) nav.IsPaneOpen = open;
    }

    /// <summary>自适应折叠的纯决策。单独抽出来是为了让 UiLoadProbe 断言这张真值表 ——
    /// 这类规则写错**不会抛异常**, 只会静默变成错的交互 (正是最该被回归网住的东西)。
    /// 返回 SetOpen = 要写进 IsPaneOpen 的值 (null = 不动), AutoCollapsed = 新的"这次是自动折的"标记。</summary>
    public static (bool? SetOpen, bool AutoCollapsed) DecidePaneForWidth(
        bool wide, bool paneOpen, bool autoCollapsed)
    {
        if (!wide)
            // 变窄: 只有展开着才折, 并记账 (自己折的 → 以后变宽才恢复); 本来就折着 → 那是用户的折叠, 不动
            return paneOpen ? (false, true) : (null, autoCollapsed);

        // 变宽: 只恢复自动折的那次; 用户自己折的 (autoCollapsed = false) 保持折叠
        return autoCollapsed ? (true, false) : (null, autoCollapsed);
    }
}
