// AmbientCore.cs — AmbientCore.dll 的 P/Invoke 映射 (blittable 结构体 + LibraryImport 源生成)
// 结构体布局必须与 native/AmbientCore/AmbientCore.h 完全一致 (Pack=8, UTF-8 固定缓冲)
using System.Runtime.InteropServices;
using System.Text;

namespace AmbientLight.Interop;

public static unsafe partial class AmbientCore
{
    private const string Lib = "AmbientCore.dll";

    /// <summary>本托管代码编译时对齐的 native ABI 版本 (major, minor)。
    /// **v1.5 起 `ACMonitorInfo.adapter` 从 32 加宽到 64** —— 这是布局变更, 用旧 DLL 会读到
    /// 错位的分辨率/适配器(静默出错, 不抛异常)。探针用 `AC_Version()` 核对, 防止旁边躺着一个旧 DLL。</summary>
    public static readonly (int Major, int Minor) RequiredAbi = (1, 5);

    // ---- 常量 (与 AmbientCore.h 对齐) ----
    public const int MaxMonitors = 8;
    public const int MaxDevices = 8;

    // ACState
    public const int StateStopped = 0;
    public const int StateStarting = 1;
    public const int StateRunning = 2;
    public const int StateWaitDisp = 3;
    public const int StateDevLost = 4;
    public const int StateError = 5;

    // ACDevType
    public const int DevScreen = 0;
    public const int DevFan = 1;
    public const int DevDesk = 2;
    public const int DevSound = 3;

    // 错误码
    public const int ErrOk = 0;
    public const int ErrInvalid = 1;
    public const int ErrNotFound = 2;
    public const int ErrPortBusy = 3;
    public const int ErrHandshake = 4;
    public const int ErrAlreadyRunning = 5;
    public const int ErrInternal = 6;

    public const int MaxLeds = 180;                 // 固件定长帧上限
    public const int MaxAudioDevs = 16;             // 音频端点枚举上限

    // ---- 模式表 (modeIndex → 名称, 与厂商 lang/zh 一致) ----
    public static readonly (int Index, string Name, string Kind)[] Modes =
    {
        (0,  "屏幕追光", "主机渲染"),
        (1,  "拾音律动", "主机拾音"),
        (2,  "彩虹跳跃", "MCU"),
        (3,  "海浪潮汐", "MCU"),
        (4,  "电闪雷鸣", "MCU"),
        (5,  "呼吸心跳", "MCU"),
        (6,  "火焰燃烧", "MCU"),
        (7,  "南极光彩", "MCU"),
        (8,  "粒子碰撞", "MCU"),
        (9,  "圣诞节日", "MCU"),
        (10, "变幻莫测", "MCU"),
        (11, "纯色灯光", "主机渲染"),
        (12, "浓情夏日", "MCU"),
        (13, "北极冰山", "MCU"),
    };

    /// <summary>MCU 特效模式 (3B 命令透传, 特效由固件渲染)</summary>
    public static bool IsMcuMode(int m) => (m >= 2 && m <= 10) || m == 12 || m == 13;

    /// <summary>主机渲染灯帧的模式 (ACStatus.ledRgb 有实时数据)</summary>
    public static bool IsHostFrameMode(int m) => m == 0 || m == 11;

    public static string ModeName(int m)
    {
        foreach (var (i, name, _) in Modes) if (i == m) return name;
        return $"未知 ({m})";
    }

    // ---- 原生导出 ----
    [LibraryImport(Lib)]
    private static partial uint AC_Version();

    [LibraryImport(Lib)]
    private static partial int AC_Start(ref ACConfig cfg);

    [LibraryImport(Lib)]
    private static partial void AC_Stop();

    [LibraryImport(Lib)]
    private static partial int AC_SetConfig(ref ACConfig cfg);

    [LibraryImport(Lib)]
    private static partial void AC_GetStatus(ref ACStatus status);

    [LibraryImport(Lib)]
    private static partial int AC_GetLastDiag(byte[] buf, uint cap);

    [LibraryImport(Lib)]
    private static partial int AC_GetMonitors(ACMonitorInfo* outBuf, uint max, int* count);

    [LibraryImport(Lib)]
    private static partial int AC_GetDevices(ACDeviceInfo* outBuf, uint max, int* count);

    [LibraryImport(Lib)]
    private static partial int AC_GetAudioEndPoints(int capture, ACAudioDevInfo* outBuf, uint max, int* count);

    [LibraryImport(Lib)]
    private static partial int AC_SetTestPattern(int mode);

    // ---- 多会话 (v1.3, M3): slot 0 = 上面单设备接口的别名 ----
    [LibraryImport(Lib)]
    private static partial int AC_StartEx(uint slot, ref ACConfig cfg);

    [LibraryImport(Lib)]
    private static partial void AC_StopEx(uint slot);

    [LibraryImport(Lib)]
    private static partial int AC_SetConfigEx(uint slot, ref ACConfig cfg);

    [LibraryImport(Lib)]
    private static partial void AC_GetStatusEx(uint slot, ref ACStatus status);

    [LibraryImport(Lib)]
    private static partial int AC_GetLastDiagEx(uint slot, byte[] buf, uint cap);

    // ---- 友好封装 ----
    public static uint Version() => AC_Version();

    public static int Start(ref ACConfig cfg) => AC_Start(ref cfg);

    public static void Stop() => AC_Stop();

    public static int SetConfig(ref ACConfig cfg) => AC_SetConfig(ref cfg);

    public static int StartEx(int slot, ref ACConfig cfg) => AC_StartEx((uint)slot, ref cfg);

    public static void StopEx(int slot) => AC_StopEx((uint)slot);

    public static int SetConfigEx(int slot, ref ACConfig cfg) => AC_SetConfigEx((uint)slot, ref cfg);

    public static ACStatus GetStatusEx(int slot)
    {
        var st = default(ACStatus);
        st.structSize = (uint)sizeof(ACStatus);
        AC_GetStatusEx((uint)slot, ref st);
        return st;
    }

    public static string LastDiagEx(int slot)
    {
        var buf = new byte[256];
        int r = AC_GetLastDiagEx((uint)slot, buf, (uint)buf.Length);
        int n = Array.IndexOf(buf, (byte)0);
        if (n < 0) n = buf.Length;
        return r == 1 ? Encoding.UTF8.GetString(buf, 0, n) : "";
    }

    public static int SetTestPattern(int mode) => AC_SetTestPattern(mode);

    public static ACStatus GetStatus()
    {
        var st = default(ACStatus);
        st.structSize = (uint)sizeof(ACStatus);
        AC_GetStatus(ref st);
        return st;
    }

    public static string LastDiag()
    {
        var buf = new byte[256];
        int r = AC_GetLastDiag(buf, (uint)buf.Length);
        int n = Array.IndexOf(buf, (byte)0);
        if (n < 0) n = buf.Length;
        return r == 1 ? Encoding.UTF8.GetString(buf, 0, n) : "";
    }

    public static ACMonitorInfo[] GetMonitors()
    {
        var buf = new ACMonitorInfo[MaxMonitors];
        int count = 0;
        fixed (ACMonitorInfo* p = buf)
        {
            AC_GetMonitors(p, (uint)buf.Length, &count);
        }
        return buf.Take(count).ToArray();
    }

    public static ACDeviceInfo[] GetDevices()
    {
        var buf = new ACDeviceInfo[MaxDevices];
        int count = 0;
        fixed (ACDeviceInfo* p = buf)
        {
            AC_GetDevices(p, (uint)buf.Length, &count);
        }
        return buf.Take(count).ToArray();
    }

    /// <summary>音频端点枚举 (capture=true 输入/麦克风, false 输出); 失败返回空数组</summary>
    public static ACAudioDevInfo[] GetAudioEndPoints(bool capture)
    {
        var buf = new ACAudioDevInfo[MaxAudioDevs];
        int count = 0;
        try
        {
            fixed (ACAudioDevInfo* p = buf)
            {
                AC_GetAudioEndPoints(capture ? 1 : 0, p, (uint)buf.Length, &count);
            }
        }
        catch
        {
            return Array.Empty<ACAudioDevInfo>();
        }
        return buf.Take(Math.Max(count, 0)).ToArray();
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct ACConfig
{
    public uint structSize;
    public fixed byte comPort[16];
    public uint baudRate;
    public int modeIndex;
    public int monitorIndex;
    public int ledCount;
    public int sides;
    public int direction;
    public int captureRange;
    public int depthOverride;
    public int taps;
    public int smoothing;
    public int fpsCap;
    public int blackBars;
    public int blackBarLevel;
    public double brightness;
    public double vibrance;
    public double hueMul;
    public double gamma;
    public double rGain;
    public double gGain;
    public double bGain;
    public int colorCut;
    public int minBrightness;
    public int outputOrder;
    public fixed byte solidRgb[3];
    public int waitDispPolicy;
    public int colorfix;
    public double sfloor;
    public int audioSource;
    public double audioGain;
    public double audioRelease;
    public double audioGate;
    public double audioBass;
    public int audioAgc;
    public int audioFollowVolume;
    public double audioSoft;
    public fixed byte audioDevId[64];           // 麦克风源目标输入端点, 空=跟随默认
    public fixed byte audioOutDevId[64];        // 回环源目标输出端点, 空=跟随默认
    public int ledRotate;                       // v1.4: 灯序偏移 (核心再钳 ±灯数12%), 默认 0
    public fixed uint reserved[8];

    public string ComPort
    {
        get
        {
            fixed (byte* p = comPort)
            {
                int n = 0;
                while (n < 16 && p[n] != 0) n++;
                return Encoding.UTF8.GetString(p, n);
            }
        }
        set
        {
            var b = Encoding.UTF8.GetBytes(value ?? "");
            fixed (byte* p = comPort)
            {
                for (int i = 0; i < 16; i++) p[i] = 0;
                for (int i = 0; i < b.Length && i < 15; i++) p[i] = b[i];
            }
        }
    }

    public string AudioDevId
    {
        get
        {
            fixed (byte* p = audioDevId)
            {
                int n = 0;
                while (n < 64 && p[n] != 0) n++;
                return Encoding.UTF8.GetString(p, n);
            }
        }
        set
        {
            var b = Encoding.UTF8.GetBytes(value ?? "");
            fixed (byte* p = audioDevId)
            {
                for (int i = 0; i < 64; i++) p[i] = 0;
                for (int i = 0; i < b.Length && i < 63; i++) p[i] = b[i];
            }
        }
    }

    public string AudioOutDevId
    {
        get
        {
            fixed (byte* p = audioOutDevId)
            {
                int n = 0;
                while (n < 64 && p[n] != 0) n++;
                return Encoding.UTF8.GetString(p, n);
            }
        }
        set
        {
            var b = Encoding.UTF8.GetBytes(value ?? "");
            fixed (byte* p = audioOutDevId)
            {
                for (int i = 0; i < 64; i++) p[i] = 0;
                for (int i = 0; i < b.Length && i < 63; i++) p[i] = b[i];
            }
        }
    }

    public static ACConfig CreateDefault() => new()
    {
        structSize = (uint)sizeof(ACConfig),
        baudRate = 2000000,
        modeIndex = 0,
        monitorIndex = 0,
        ledCount = 60,
        sides = 4,
        direction = 0,
        captureRange = 15,
        depthOverride = 0,
        taps = 8,
        smoothing = 0,
        fpsCap = 0,
        blackBars = 1,
        blackBarLevel = 3,
        brightness = 1.0,
        vibrance = 9.0,
        hueMul = 1.0,
        gamma = 0.65,
        rGain = 1.0,
        gGain = 1.0,
        bGain = 1.0,
        colorCut = 1,
        minBrightness = 1,
        outputOrder = 0,
        waitDispPolicy = 2,
        colorfix = 0,
        sfloor = 24.0,
        audioSource = 0,
        audioGain = 1.0,
        audioRelease = 0.30,
        audioGate = 0.003,
        audioBass = 0.35,
        audioAgc = 1,
        audioFollowVolume = 0,
        audioSoft = 0.5,
        ledRotate = 0,
    };
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct ACStatus
{
    public uint structSize;
    public int state;
    public int devType;
    public int devId;
    public fixed byte comPort[16];
    public uint ledStatusByte;
    public uint deviceChanged;
    public int monitorFallback;
    public uint allBlackFrames;
    public float fps;
    public float procMs;
    public float wireMs;
    public float cpuPercent;
    public fixed int margins[4];
    public uint frameCounter;
    public fixed byte ledRgb[540];              // MaxLeds*3: 最近下发帧 RGB (主机渲染模式)
    public byte audioLevel;                     // v1.3: 拾音模式最近音量字节 0..255
    public int audioOk;                         // v1.3: 音源会话 0=停 1=正常 2=重建中
    public fixed byte audioDev[32];             // v1.3: 当前捕获端点友好名 (UTF-8)

    public string ComPort
    {
        get
        {
            fixed (byte* p = comPort)
            {
                int n = 0;
                while (n < 16 && p[n] != 0) n++;
                return Encoding.UTF8.GetString(p, n);
            }
        }
    }

    public string AudioDev
    {
        get
        {
            fixed (byte* p = audioDev)
            {
                int n = 0;
                while (n < 32 && p[n] != 0) n++;
                return Encoding.UTF8.GetString(p, n);
            }
        }
    }

    /// <summary>ledRgb 快照拷贝 (540B, 前 ledCount*3 有效; 主机渲染模式)</summary>
    public byte[] CopyLedRgb()
    {
        var buf = new byte[AmbientCore.MaxLeds * 3];
        fixed (byte* p = ledRgb)
        {
            Marshal.Copy((IntPtr)p, buf, 0, buf.Length);
        }
        return buf;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct ACMonitorInfo
{
    public fixed byte name[32];
    public fixed byte adapter[64];      // v1.5: 32 → 64 (显卡型号带 "GPU" 后缀会被截断)
    public int width;
    public int height;
    public int primary;

    public string Name { get { fixed (byte* p = name) return ReadFixed(p, 32); } }
    public string Adapter { get { fixed (byte* p = adapter) return ReadFixed(p, 64); } }

    internal static string ReadFixed(byte* p, int len)
    {
        int n = 0;
        while (n < len && p[n] != 0) n++;
        return Encoding.UTF8.GetString(p, n);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct ACDeviceInfo
{
    public fixed byte comPort[16];
    public int devType;
    public int devId;
    public int inUse;

    public string ComPort { get { fixed (byte* p = comPort) return ACMonitorInfo.ReadFixed(p, 16); } }
}

/// <summary>音频端点 (拾音源选择); Id 配置到 ACConfig.audioDevId</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct ACAudioDevInfo
{
    public fixed byte id[64];
    public fixed byte name[32];
    public int isDefault;

    public string Id { get { fixed (byte* p = id) return ACMonitorInfo.ReadFixed(p, 64); } }
    public string Name { get { fixed (byte* p = name) return ACMonitorInfo.ReadFixed(p, 32); } }
}
