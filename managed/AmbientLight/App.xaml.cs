// App.xaml.cs — 单实例守卫 (named mutex) + 托盘 + 核心生命周期
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AmbientLight.Interop;
using AmbientLight.Services;
using AmbientLight.Views;
using Hardcodet.Wpf.TaskbarNotification;

namespace AmbientLight;

public partial class App : Application
{
    public const string ShowEventName = @"Local\AmbientLight.ShowWindow";

    private static App? _instance;
    public static App Instance => _instance!;

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private TaskbarIcon? _tray;
    private MenuItem? _trayToggle;
    private PollingService? _poller;
    private MainWindow? _main;
    private bool _coreRunning;

    public PollingService Poller => _poller!;
    public MainWindow MainWin => _main!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- 单实例守卫: 第二次启动 → 通知已运行实例显示窗口后退出
        _mutex = new Mutex(true, @"Local\AmbientLight.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
            {
                ev.Set();
                ev.Dispose();
            }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => ShowMainWindow()), null, -1, false);

        base.OnStartup(e);
        _instance = this;

        ConfigStore.Load();
        InitTray();

        _poller = new PollingService();
        _poller.StateChanged += OnCoreStateChanged;

        _main = new MainWindow();
        if (ConfigStore.App.StartMinimized)
            _main.Hide();
        else
            _main.Show();
        _poller.Start();

        if (ConfigStore.Device.AutoStartCore)
            StartCore();
    }

    // ---- 核心生命周期 -----------------------------------------------
    public bool CoreRunning => _coreRunning;

    public bool StartCore()
    {
        if (_coreRunning) return true;
        var cfg = ConfigStore.Device.ToCoreConfig();
        int rc = AmbientCore.Start(ref cfg);
        if (rc != AmbientCore.ErrOk)
        {
            string reason = rc switch
            {
                AmbientCore.ErrInvalid => "配置参数无效 (modeIndex 等)",
                AmbientCore.ErrAlreadyRunning => "核心已在运行",
                _ => $"错误码 {rc}",
            };
            MessageBox.Show($"启动失败: {reason}\n{AmbientCore.LastDiag()}", "AmbientLight",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _coreRunning = true;
        UpdateTrayToggle();
        return true;
    }

    public void StopCore()
    {
        if (!_coreRunning) return;
        AmbientCore.Stop();
        _coreRunning = false;
        UpdateTrayToggle();
    }

    public void ToggleCore()
    {
        if (_coreRunning) StopCore();
        else StartCore();
    }

    /// <summary>配置变更 (亮度滑条等) → 原子热更新 + 落盘</summary>
    public void ApplyConfig()
    {
        var cfg = ConfigStore.Device.ToCoreConfig();
        if (_coreRunning)
            AmbientCore.SetConfig(ref cfg);
        ConfigStore.SaveDevice();
    }

    private void OnCoreStateChanged(int oldState, int newState)
    {
        // 状态机流转仅刷新托盘菜单文案; 数值细节由主窗口轮询展示
        UpdateTrayToggle();
    }

    private void UpdateTrayToggle()
    {
        if (_trayToggle != null)
            _trayToggle.Header = _coreRunning ? "关闭氛围灯" : "开启氛围灯";
    }

    // ---- 托盘 ---------------------------------------------------------
    private void InitTray()
    {
        _tray = new TaskbarIcon
        {
            ToolTipText = "AmbientLight",
            IconSource = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/app.ico")),
        };

        var menu = new ContextMenu();
        _trayToggle = new MenuItem { Header = "开启氛围灯" };
        _trayToggle.Click += (_, _) => ToggleCore();
        var settings = new MenuItem { Header = "设置" };
        settings.Click += (_, _) => ShowMainWindow();
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(_trayToggle);
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        _tray.ContextMenu = menu;
        _tray.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_main == null) return;
        _main.Show();
        _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void ExitApp()
    {
        if (_main != null) _main.AllowClose = true;
        StopCore();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopCore();
        if (ConfigStore.ConfigDir.Length > 0)
        {
            // 第二实例守卫退出时尚未 Load (ConfigDir 为空), 不能用默认值覆盖已存配置
            ConfigStore.SaveApp();
            ConfigStore.SaveDevice();
        }
        _tray?.Dispose();
        _poller?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
