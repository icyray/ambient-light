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

        if (ConfigStore.Devices.Values.Any(d => d.AutoStartCore))
            _ = StartAllAsync();
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

    /// <summary>最近一次"总开启"没建起任何会话时的原因: (胶囊短标签, InfoBar 详情)。
    /// null = 未尝试, 或已正常建起会话。</summary>
    public (string Label, string Detail)? LastStartProblem { get; private set; }

    /// <summary>总开关: 枚举在位设备, 为 AutoStartCore 的设备各建会话; 选中设备可用则直接开。
    /// 并发调用**复用同一次启动任务** —— 轮 16 用户报"快速连点开启/关闭会弹报错":
    /// 连点会让 Stop 尚未释放端口时又发起 StartEx, 失败分支弹出模态 MessageBox (App.xaml.cs 启动失败提示)。
    /// 去重后同一时刻只有一次枚举+建会话。
    /// **轮 18: 不再对"设备被占用/未应答/不在位"占坑建兜底会话** —— 那会让 UI 出现一个假的开启态
    /// (banner 亮起 + 胶囊"未连接" + 按钮却写着"开启", 再点反而变关闭)。现在改为把原因写进
    /// `LastStartProblem` 由 UI 呈现 (胶囊警示色 + InfoBar), 按钮保持"开启", 再点即重试。</summary>
    public Task StartAllAsync()
    {
        if (CoreRunning) return Task.CompletedTask;
        if (_startTask is { IsCompleted: false }) return _startTask;
        return _startTask = StartAllCoreAsync();
    }

    private Task? _startTask;   // 进行中的启动任务 (完成即失效)

    private async Task StartAllCoreAsync()
    {
        LastStartProblem = null;
        try
        {
            var devices = await Task.Run(Interop.AmbientCore.GetDevices);
            var usable = devices.Where(d => d.devType >= 0 && d.inUse == 0).ToArray();

            bool any = false;
            foreach (var d in usable)
            {
                var key = $"{d.devType}_{d.devId}";
                if (ConfigStore.GetOrCreate(key).AutoStartCore && StartDevice(key))
                    any = true;
            }
            // 用户明确点了"开启": 只要选中设备这次枚举到且可用就直接开 —— AutoStartCore 管的是
            // "应用启动时自动连", 不该拦住手动开启 (否则会出现"点了没反应、也不说为什么")
            if (!any && usable.Any(d => $"{d.devType}_{d.devId}" == ConfigStore.SelectedKey))
                any = StartDevice(ConfigStore.SelectedKey);

            if (!any) LastStartProblem = DescribeNoStart(devices);
        }
        catch (Exception ex)
        {
            LastStartProblem = ("枚举失败", $"设备枚举失败: {ex.Message}");
        }
        UpdateTrayToggle();
    }

    /// <summary>一台会话都没建起来时, 把原因整理成 (胶囊短标签, InfoBar 详情)</summary>
    private static (string Label, string Detail) DescribeNoStart(Interop.ACDeviceInfo[] devices)
    {
        var busy = devices.Where(d => d.inUse == 1).Select(d => d.ComPort).ToArray();
        if (busy.Length > 0)
            return ("端口被占用",
                    $"端口 {string.Join("、", busy)} 已被占用, 无法开启。常见原因是 Ansnake 等工具正连着灯带 —— 关闭占用程序后再点「开启」。");

        var silent = devices.Where(d => d.devType < 0).Select(d => d.ComPort).ToArray();
        if (silent.Length > 0)
            return ("设备未应答",
                    $"发现端口 {string.Join("、", silent)}, 但握手无应答。检查 USB 连接与供电后再点「开启」重试。");

        if (devices.Length == 0)
            return ("未发现灯带",
                    "未发现灯带 (VID_2E3C & PID_5740)。检查 USB 连接、确认 Ansnake 未占用端口, 然后点「开启」重试。");

        return ("设备未启用",
                "已发现可用灯带, 但该设备未勾选「启动时自动开启灯带」。可到高级设置勾选, 或在下方设备列表里单独点「开启」。");
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
        else _ = StartFromTrayAsync();
    }

    /// <summary>托盘"开启氛围灯": 没开起来时把主窗带到前台 —— 那是唯一能看到失败原因的地方
    /// (banner 胶囊 + InfoBar 会说明是端口被占用 / 未应答 / 未发现灯带)。
    /// 轮 18 之前这里会占坑建会话, 于是"点了没反应"和"灯自己亮了"都很突然; 现在改为如实报告。</summary>
    private async Task StartFromTrayAsync()
    {
        await StartAllAsync();
        if (LastStartProblem != null) ShowMainWindow();
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

    /// <summary>全屏校准窗打开标记 (M4): ApplyConfig 挂起防抖动。</summary>
    public bool Calibrating { get; set; }

    // ---- 设置页防抖应用 ----------------------------------------------------

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

    // ---- 崩溃日志 (exe 旁 log/crash.log; 任何失败静默) ----------------------
    // 目录与核心统一 (轮 22): 核心写的是 exe 旁 log/ambientcore.log (native/log.cpp:
    // GetModuleFileNameW + \log\), 托管侧原先却写进 config/ —— 配置目录只该放配置。
    // 程序目录不可写时 (例如装在 Program Files) 回退 %APPDATA%\AmbientLight\log。
    // 注: AC_SetTestPattern 原生接口保留 (probe 回归用), UI 入口已由全屏校准向导取代

    private static string? _logDir;

    /// <summary>日志目录 (懒解析 + 缓存): exe 旁 log/, 不可写则 %APPDATA%\AmbientLight\log</summary>
    private static string LogDir => _logDir ??= ResolveLogDir();

    private static string ResolveLogDir()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "log");
        try
        {
            Directory.CreateDirectory(local);
            var probe = Path.Combine(local, ".write_probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return local;
        }
        catch
        {
            var roaming = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AmbientLight", "log");
            try { Directory.CreateDirectory(roaming); } catch { /* 由 append 的 try 兜底 */ }
            return roaming;
        }
    }

    private static void WriteCrashLog(string source, object? exc, bool terminating)
    {
        try
        {
            File.AppendAllText(Path.Combine(LogDir, "crash.log"),
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
