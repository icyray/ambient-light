// ConfigStore.cs — config/*.json 读写; 程序目录不可写时回退 %APPDATA%\AmbientLight\config
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmbientLight.Services;

public sealed class AppOptions
{
    public string Lang { get; set; } = "zh";
    public bool StartMinimized { get; set; } = false;
    public bool AutoStart { get; set; } = false;   // shell:startup 快捷方式 (M4)
}

public sealed class DeviceSettings
{
    public string ComPort { get; set; } = "";          // 空 = 自动探测 VID_2E3C&PID_5740
    public int BaudRate { get; set; } = 2000000;
    public bool AutoStartCore { get; set; } = false;   // app 启动即开灯 (M1 默认关)
    public int ModeIndex { get; set; } = 0;
    public int MonitorIndex { get; set; } = 0;
    public int LedCount { get; set; } = 60;
    public int Sides { get; set; } = 4;
    public int Direction { get; set; } = 0;
    public int CaptureRange { get; set; } = 15;
    public int DepthOverride { get; set; } = 0;
    public int Taps { get; set; } = 8;
    public int Smoothing { get; set; } = 0;
    public int FpsCap { get; set; } = 0;
    public bool BlackBars { get; set; } = true;
    public int BlackBarLevel { get; set; } = 3;
    public double Brightness { get; set; } = 1.0;
    public double Vibrance { get; set; } = 9.0;
    public double Hue { get; set; } = 1.0;
    public double Gamma { get; set; } = 0.65;
    public double RGain { get; set; } = 1.0;
    public double GGain { get; set; } = 1.0;
    public double BGain { get; set; } = 1.0;
    public int ColorCut { get; set; } = 1;
    public int MinBrightness { get; set; } = 1;
    public int OutputOrder { get; set; } = 0;
    public string SolidColor { get; set; } = "#FFE1D6";
    public int WaitDispPolicy { get; set; } = 2;
    public bool Colorfix { get; set; } = false;
    public double Sfloor { get; set; } = 24.0;

    public unsafe Interop.ACConfig ToCoreConfig()
    {
        var c = Interop.ACConfig.CreateDefault();
        c.ComPort = ComPort;
        c.baudRate = (uint)BaudRate;
        c.modeIndex = ModeIndex;
        c.monitorIndex = MonitorIndex;
        c.ledCount = LedCount;
        c.sides = Sides;
        c.direction = Direction;
        c.captureRange = CaptureRange;
        c.depthOverride = DepthOverride;
        c.taps = Taps;
        c.smoothing = Smoothing;
        c.fpsCap = FpsCap;
        c.blackBars = BlackBars ? 1 : 0;
        c.blackBarLevel = BlackBarLevel;
        c.brightness = Brightness;
        c.vibrance = Vibrance;
        c.hueMul = Hue;
        c.gamma = Gamma;
        c.rGain = RGain;
        c.gGain = GGain;
        c.bGain = BGain;
        c.colorCut = ColorCut;
        c.minBrightness = MinBrightness;
        c.outputOrder = OutputOrder;
        var rgb = ParseColor(SolidColor, (255, 225, 214));
        c.solidRgb[0] = rgb.r;
        c.solidRgb[1] = rgb.g;
        c.solidRgb[2] = rgb.b;
        c.waitDispPolicy = WaitDispPolicy;
        c.colorfix = Colorfix ? 1 : 0;
        c.sfloor = Sfloor;
        return c;
    }

    public unsafe void FromCoreConfig(Interop.ACConfig c)
    {
        ComPort = c.ComPort;
        BaudRate = (int)c.baudRate;
        ModeIndex = c.modeIndex;
        MonitorIndex = c.monitorIndex;
        LedCount = c.ledCount;
        Sides = c.sides;
        Direction = c.direction;
        CaptureRange = c.captureRange;
        DepthOverride = c.depthOverride;
        Taps = c.taps;
        Smoothing = c.smoothing;
        FpsCap = c.fpsCap;
        BlackBars = c.blackBars != 0;
        BlackBarLevel = c.blackBarLevel;
        Brightness = c.brightness;
        Vibrance = c.vibrance;
        Hue = c.hueMul;
        Gamma = c.gamma;
        RGain = c.rGain;
        GGain = c.gGain;
        BGain = c.bGain;
        ColorCut = c.colorCut;
        MinBrightness = c.minBrightness;
        OutputOrder = c.outputOrder;
        SolidColor = $"#{c.solidRgb[0]:X2}{c.solidRgb[1]:X2}{c.solidRgb[2]:X2}";
        WaitDispPolicy = c.waitDispPolicy;
        Colorfix = c.colorfix != 0;
        Sfloor = c.sfloor;
    }

    private static (byte r, byte g, byte b) ParseColor(string hex, (byte r, byte g, byte b) fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.TrimStart('#');
        if (s.Length == 8) s = s.Substring(2);           // #AARRGGBB → RGB
        if (s.Length != 6) return fallback;
        try
        {
            return ((byte)Convert.ToByte(s[..2], 16),
                    (byte)Convert.ToByte(s.Substring(2, 2), 16),
                    (byte)Convert.ToByte(s.Substring(4, 2), 16));
        }
        catch { return fallback; }
    }
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppOptions App { get; private set; } = new();
    public static DeviceSettings Device { get; private set; } = new();

    public static string ConfigDir { get; private set; } = "";
    public static string ConfigDirSource { get; private set; } = "";   // "app" | "appdata"

    public static void Load()
    {
        ConfigDir = ResolveConfigDir();
        App = LoadJson<AppOptions>(Path.Combine(ConfigDir, "app.json")) ?? new AppOptions();
        Device = LoadJson<DeviceSettings>(Path.Combine(ConfigDir, "device_0_1.json")) ?? new DeviceSettings();
    }

    public static void SaveApp() => WriteJson(Path.Combine(ConfigDir, "app.json"), App);

    public static void SaveDevice() => WriteJson(Path.Combine(ConfigDir, "device_0_1.json"), Device);

    private static string ResolveConfigDir()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "config");
        try
        {
            Directory.CreateDirectory(local);
            var probe = Path.Combine(local, ".write_probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            ConfigDirSource = "app";
            return local;
        }
        catch
        {
            var roaming = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AmbientLight", "config");
            Directory.CreateDirectory(roaming);
            ConfigDirSource = "appdata";
            return roaming;
        }
    }

    private static T? LoadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
            return null;   // 损坏配置视为缺省, 下次保存覆盖
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOpts));
        }
        catch
        {
            // 只读目录下的写失败静默; 实际路径已回退, 不影响运行
        }
    }
}
