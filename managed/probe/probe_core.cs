// probe_core.cs — 核心生命周期验证: Start→流式→SetConfig 热更→测试图→重复Start拒绝→Stop
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;

var lib = @"D:\Codes\ambient-light\managed\AmbientLight\bin\Debug\net10.0-windows\win-x64\AmbientCore.dll";
NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, asm, path) =>
    NativeLibrary.Load(lib));

unsafe
{
    var cfg = new ACConfig();
    cfg.structSize = (uint)sizeof(ACConfig);
    cfg.baudRate = 2000000;
    cfg.modeIndex = 0;
    cfg.ledCount = 60;

    int rc = Native.AC_Start(ref cfg);
    Console.WriteLine($"AC_Start -> {rc}");
    var st = Poll(2000);
    Console.WriteLine($"after 2s: state={st.state} frames={st.frameCounter} fps={st.fps:F1} port={S(st.comPort)} proc={st.procMs:F2}ms");

    Console.WriteLine($"AC_Start again -> {Native.AC_Start(ref cfg)} (expect 5)");

    cfg.brightness = 0.5;
    cfg.ledCount = 90;
    rc = Native.AC_SetConfig(ref cfg);
    Console.WriteLine($"AC_SetConfig (brightness 0.5, leds 90) -> {rc}");
    uint before = st.frameCounter;
    st = Poll(1500);
    Console.WriteLine($"after SetConfig: state={st.state} frames={st.frameCounter} (advance={st.frameCounter > before})");

    Native.AC_SetTestPattern(1);
    st = Poll(1500);
    Console.WriteLine($"pattern 1: state={st.state} frames={st.frameCounter}");
    Native.AC_SetTestPattern(0);

    Native.AC_Stop();
    Native.AC_GetStatus(ref st);
    Console.WriteLine($"after Stop: state={st.state} (expect 0)");

    rc = Native.AC_Start(ref cfg);
    var st2 = Poll(1200);
    Console.WriteLine($"restart: rc={rc} state={st2.state} frames={st2.frameCounter}");
    Native.AC_Stop();
    Native.AC_GetStatus(ref st);
    Console.WriteLine($"final: state={st.state}");
}

static ACStatus Poll(int ms)
{
    var st = new ACStatus();
    st.structSize = (uint)Marshal.SizeOf<ACStatus>();
    var sw = Stopwatch.StartNew();
    Native.AC_GetStatus(ref st);
    while (sw.ElapsedMilliseconds < ms)
    {
        Thread.Sleep(100);
        Native.AC_GetStatus(ref st);
    }
    return st;
}

static unsafe string S(byte* p) { int n = 0; while (n < 16 && p[n] != 0) n++; return Encoding.UTF8.GetString(p, n); }

internal static unsafe partial class Native
{
    [DllImport("AmbientCore.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int AC_Start(ref ACConfig cfg);

    [DllImport("AmbientCore.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void AC_Stop();

    [DllImport("AmbientCore.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int AC_SetConfig(ref ACConfig cfg);

    [DllImport("AmbientCore.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void AC_GetStatus(ref ACStatus st);

    [DllImport("AmbientCore.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int AC_SetTestPattern(int mode);
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct ACConfig
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
    public fixed uint reserved[8];
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct ACStatus
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
}
