// App.xaml.cs — 单实例守卫 (named mutex) + 托盘 + 核心生命周期
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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

        // ---- M4 崩溃兜底: UI 线程 / 全局 (含 terminating) / 未观察任务异常 → crash.log
        DispatcherUnhandledException += (_, e) =>
        {
            WriteCrashLog("Dispatcher (UI 线程)", e.Exception, terminating: false);
            MessageBox.Show($"界面线程异常: {e.Exception.Message}\n{e.Exception.StackTrace}",
                "AmbientLight", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog(e.IsTerminating ? "AppDomain (进程终止)" : "AppDomain",
                e.ExceptionObject, terminating: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("UnobservedTask (GC 回收未观察任务)", e.Exception, terminating: false);
            e.SetObserved();   // 已记日志, 不让进程因此崩溃
        };

        ConfigStore.Load();
        // 开机自启自愈: app.json 的 AutoStart 是事实源, 快捷方式丢失/残留都对齐
        if (ConfigStore.App.AutoStart && !AutoStart.IsActive())
            AutoStart.Enable();
        else if (!ConfigStore.App.AutoStart && AutoStart.IsActive())
            AutoStart.Disable();
        InitTray();

        _poller = new PollingService();
        _poller.StateChanged += OnCoreStateChanged;

        _main = new MainWindow();
        if (ConfigStore.App.StartMinimized)
            _main.Hide();
        else
            _main.Show();
        _poller.Start();

        // 定时开灯开启且未到时刻 → 本次启动不自动亮灯 (到点由调度器拉起);
        // 已过时刻则照常 AutoStartCore (开箱即亮语义不变)
        bool autoOnWait = ConfigStore.App.AutoOnEnabled && !NowBeyondAutoOnTime();
        if (!autoOnWait && ConfigStore.Devices.Values.Any(d => d.AutoStartCore))
            _ = StartAllAsync();
        InitAutoSchedule();
    }

    // ---- 核心生命周期 (M3: 每设备一个 slot 会话) ---------------------
    private readonly Dictionary<int, string> _slots = new();   // slot → 设备 key

    public bool CoreRunning => _slots.Count > 0;

    /// <summary>slot → 设备 key 快照 (UI 设备卡片/轮询用)</summary>
    public IReadOnlyDictionary<int, string> Slots => _slots;

    public int SlotOf(string key)
    {
        foreach (var (slot, k) in _slots) if (k == key) return slot;
        return -1;
    }

    public string? KeyOf(int slot) => _slots.TryGetValue(slot, out var k) ? k : null;

    public bool StartDevice(string key)
    {
        if (SlotOf(key) >= 0) return true;
        int slot = -1;
        for (int i = 0; i < Interop.AmbientCore.MaxDevices; i++)
            if (!_slots.ContainsKey(i)) { slot = i; break; }
        if (slot < 0)
        {
            MessageBox.Show($"设备数超过上限 ({Interop.AmbientCore.MaxDevices})", "AmbientLight",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var cfg = ConfigStore.GetOrCreate(key).ToCoreConfig();
        int rc = Interop.AmbientCore.StartEx(slot, ref cfg);
        if (rc != Interop.AmbientCore.ErrOk)
        {
            string reason = rc switch
            {
                Interop.AmbientCore.ErrInvalid => "配置参数无效",
                Interop.AmbientCore.ErrAlreadyRunning => "该会话已在运行",
                _ => $"错误码 {rc}",
            };
            MessageBox.Show($"启动 {key} 失败: {reason}\n{Interop.AmbientCore.LastDiagEx(slot)}",
                "AmbientLight", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _slots[slot] = key;
        UpdateTrayToggle();
        return true;
    }

    public void StopDevice(string key)
    {
        int slot = SlotOf(key);
        if (slot < 0) return;
        Interop.AmbientCore.StopEx(slot);
        _slots.Remove(slot);
        UpdateTrayToggle();
    }

    public void StopAllDevices()
    {
        foreach (var slot in _slots.Keys.ToList())
        {
            Interop.AmbientCore.StopEx(slot);
            _slots.Remove(slot);
        }
        UpdateTrayToggle();
    }

    /// <summary>总开关: 枚举在位设备, 为 AutoStartCore 的设备各建会话;
    /// 一台都没枚举到时占住选中设备的 slot 等 DEV_LOST 自动重连</summary>
    public async Task StartAllAsync()
    {
        if (CoreRunning) return;
        try
        {
            var devices = await Task.Run(Interop.AmbientCore.GetDevices);
            bool any = false;
            foreach (var d in devices)
            {
                if (d.devType < 0 || d.inUse == 1) continue;   // 未应答/端口被占
                var key = $"{d.devType}_{d.devId}";
                if (ConfigStore.GetOrCreate(key).AutoStartCore && StartDevice(key))
                    any = true;
            }
            if (!any && ConfigStore.Device.AutoStartCore)
                StartDevice(ConfigStore.SelectedKey);
        }
        catch
        {
            // 枚举失败按未在位处理: 占住选中设备 slot, 状态机自动重试
            if (ConfigStore.Device.AutoStartCore) StartDevice(ConfigStore.SelectedKey);
        }
        UpdateTrayToggle();
    }

    public bool StartCore()
    {
        if (CoreRunning) return true;
        _ = StartAllAsync();
        return true;
    }

    public void StopCore() => StopAllDevices();

    public void ToggleCore()
    {
        if (CoreRunning) StopCore();
        else StartCore();
    }

    /// <summary>配置变更 (亮度滑条等) → 选中设备原子热更新 + 落盘;
    /// 校准窗打开期间挂起 (校准临时配置由校准窗自己下发, 关闭时恢复)。</summary>
    public void ApplyConfig()
    {
        if (Calibrating) return;
        var cfg = ConfigStore.Device.ToCoreConfig();
        int slot = SlotOf(ConfigStore.SelectedKey);
        if (slot >= 0)
            Interop.AmbientCore.SetConfigEx(slot, ref cfg);
        ConfigStore.SaveDevice();
    }

    /// <summary>全屏校准窗打开标记 (M4): ApplyConfig/SetPattern 挂起防抖动。</summary>
    public bool Calibrating { get; set; }

    private DispatcherTimer? _applyDebounce;

    /// <summary>设置页滑条防抖应用 (停止变更 200ms 后热更+落盘一次)</summary>
    public void ApplyConfigDebounced(double ms = 200)
    {
        if (_applyDebounce == null)
        {
            _applyDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            _applyDebounce.Tick += (_, _) => { _applyDebounce!.Stop(); ApplyConfig(); };
        }
        _applyDebounce.Interval = TimeSpan.FromMilliseconds(ms);
        _applyDebounce.Stop();
        _applyDebounce.Start();
    }

    // ---- 定时开灯 (M4): 每天 AutoOnTime 到点自动开启 (当天只一次, 用户手动关闭不劫持;
    //      厂商 LedAutoOnOff/LedAutoTime 同款语义, 但不做厂商的"未到点强制关灯") ----
    private DispatcherTimer? _autoOnTimer;
    private DateOnly _autoOnLastDay;

    private void InitAutoSchedule()
    {
        if (!ConfigStore.App.AutoOnEnabled) return;
        if (NowBeyondAutoOnTime())
            _autoOnLastDay = DateOnly.FromDateTime(DateTime.Now);   // 启动已过点: 今天不再重复拉起
        _autoOnTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoOnTimer.Tick += (_, _) =>
        {
            if (!ConfigStore.App.AutoOnEnabled || !NowBeyondAutoOnTime()) return;
            if (_autoOnLastDay == DateOnly.FromDateTime(DateTime.Now)) return;
            _autoOnLastDay = DateOnly.FromDateTime(DateTime.Now);
            if (!CoreRunning) _ = StartAllAsync();
        };
        _autoOnTimer.Start();
    }

    private static bool NowBeyondAutoOnTime()
    {
        // HH:mm 解析失败按"已过点"处理 (不影响亮灯可用性)
        return !TimeSpan.TryParseExact(ConfigStore.App.AutoOnTime, @"hh\:mm", null, out var t)
               || DateTime.Now.TimeOfDay >= t;
    }

    // ---- 测试图 (高级页; 覆盖正常内容, 再点同钮关闭) --------------------
    public int ActivePattern { get; private set; }
    public event Action? PatternChanged;

    public void SetPattern(int mode)
    {
        if (Calibrating) return;                     // 校准窗运行时不响应普通测试图
        int next = ActivePattern == mode ? 0 : mode;
        AmbientCore.SetTestPattern(next);
        ActivePattern = next;
        PatternChanged?.Invoke();
    }

    // ---- 崩溃日志 (crash.log 与配置同目录; 任何失败静默) --------------------
    private static void WriteCrashLog(string source, object? exc, bool terminating)
    {
        try
        {
            var logDir = ConfigStore.ConfigDir.Length > 0 ? ConfigStore.ConfigDir : ".";
            File.AppendAllText(Path.Combine(logDir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]{(terminating ? " [terminating]" : "")} " +
                $"{exc ?? "(null)"}\r\n\r\n");
        }
        catch { /* 日志失败不再抛 */ }
    }

    private void OnCoreStateChanged(int oldState, int newState)
    {
        // 状态机流转仅刷新托盘菜单文案; 数值细节由主窗口轮询展示
        UpdateTrayToggle();
    }

    private void UpdateTrayToggle()
    {
        if (_trayToggle != null)
            _trayToggle.Header = CoreRunning ? "关闭氛围灯" : "开启氛围灯";
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
        var calibrate = new MenuItem { Header = "屏幕校准" };
        calibrate.Click += (_, _) => { ShowMainWindow(); Views.CalibrateWindow.TryOpen(); };
        var settings = new MenuItem { Header = "设置" };
        settings.Click += (_, _) => ShowMainWindow();
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(_trayToggle);
        menu.Items.Add(calibrate);
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
        StopAllDevices();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _autoOnTimer?.Stop();
        StopAllDevices();
        if (ConfigStore.ConfigDir.Length > 0)
        {
            // 第二实例守卫退出时尚未 Load (ConfigDir 为空), 不能用默认值覆盖已存配置
            ConfigStore.SaveAll();
        }
        _tray?.Dispose();
        _poller?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
