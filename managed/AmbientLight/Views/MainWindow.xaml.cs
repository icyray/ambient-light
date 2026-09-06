// MainWindow.xaml.cs — NavigationView 壳: 主页 / 采集 / 颜色 / 高级 四页切换。
// 页面内容与状态都在各自 Page (Loaded 时从 ConfigStore 重读), 此处无业务逻辑。
using System.Windows;
using Wpf.Ui.Controls;

namespace AmbientLight.Views;

public partial class MainWindow : FluentWindow
{
    private bool _allowClose;

    public bool AllowClose { get => _allowClose; set => _allowClose = value; }

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
        };
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;                    // 关窗 = 收进托盘, 退出走托盘菜单
            Hide();
        };
    }
}
