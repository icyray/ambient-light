// AmbientCore.h — 公共 C ABI (v1.6, x64 cdecl, 未修饰导出)
// 合同来源: docs/AmbientCore_API.md (规范定义以**本头文件**为准)。规则:
//   - 结构体以 structSize 开头, 新增字段只能追加在末尾
//     (v1.1 增补: ACConfig.colorfix/sfloor; v1.2 增补: ACStatus.ledRgb;
//      v1.3 增补: ACConfig.audio* 7 字段, ACStatus.audioLevel/audioOk/audioDev;
//      v1.4 增补: ACConfig.ledRotate;
//      v1.6 增补: ACConfig.chaseSmoothMode (追光柔化平滑档位: 0=关/1=抑制闪光/2=+时域柔和)
//      v1.5 加宽: ACMonitorInfo.adapter 32→64 字节 —— 32 字节会把
//      "NVIDIA GeForce RTX 4060 Laptop GPU" 截成 "…Laptop", 界面上看着像显卡名少了半截)
//   - 字符串一律 UTF-8, 缓冲由调用方分配, DLL 不跨边界分配/释放
//   - v1 明确不做: 回调、跨 ABI 分配、宽字符接口、x86
#pragma once

#ifdef AMBIENTCORE_BUILD
  #define AC_API extern "C" __declspec(dllexport)
#else
  #define AC_API extern "C" __declspec(dllimport)
#endif

#include <stdint.h>

#pragma pack(push, 8)

// ---- 常量 -----------------------------------------------------------
#define AC_MAX_MONITORS 8
#define AC_MAX_DEVICES  8
#define AC_PORT_LEN     16      // "COM5"
#define AC_NAME_LEN     32
#define AC_ADAPTER_LEN  64      // v1.5: 显卡型号全名较长 (含 "GPU" 后缀), 32 会被截断
#define AC_AUDIO_ID_LEN 64      // MMDevice 端点 ID "{0.0.x.00000000}.{GUID}" ≈ 54 字符
#define AC_MAX_AUDIO_DEVS 16
#define AC_MAX_LEDS     180     // 固件定长帧上限 (3+180*3=543B)

// 设备类型 = 握手应答字节解码 (0x80 | type<<2 | devId)
typedef enum {
    AC_DEV_SCREEN = 0,   // 屏幕灯带 (应答 0x80-0x83)
    AC_DEV_FAN    = 1,   // ARGB 风扇 (0x84-0x87)
    AC_DEV_DESK   = 2,   // 桌面灯条 (0x88-0x8B)
    AC_DEV_SOUND  = 3,   // 立柱氛围灯 (0x8C-0x8F)
} ACDevType;

typedef enum {
    AC_STATE_STOPPED    = 0,
    AC_STATE_STARTING   = 1,   // 正在探测/握手
    AC_STATE_RUNNING    = 2,   // 流式运行中
    AC_STATE_WAIT_DISP  = 3,   // 画面暂不可用(锁屏/拓扑切换/独占全屏/熄屏): 串口保持连接,
                               //   每 500ms 重试采集, 恢复后回 RUNNING, 灯带按 waitDispPolicy
    AC_STATE_DEV_LOST   = 4,   // 灯带拔出/端口消失/端口占用, 内部每 2s 自动重试
    AC_STATE_ERROR      = 5,   // 不可恢复错误 (见 AC_GetLastDiag), 每 5s 慢速重试
} ACState;

// ---- 配置 (POD, 原子快照) -------------------------------------------
typedef struct ACConfig {
    uint32_t structSize;        // = sizeof(ACConfig), 版本/前向兼容校验

    // 设备
    char     comPort[AC_PORT_LEN];  // "COM5"; 空串 = 自动探测 VID_2E3C&PID_5740
    uint32_t baudRate;              // 默认 2000000 (设备为原生 CDC, 数值不敏感)

    // 模式: 0=屏幕追光 1=拾音(M3 实现) 2..10,12,13=MCU特效(M2 实现) 11=纯色
    int32_t  modeIndex;

    // 采集
    int32_t  monitorIndex;      // 显示器序号 (AC_GetMonitors 顺序)
    int32_t  ledCount;          // 1..512, 默认 60 (固件上限 180, 超出部分忽略)
    int32_t  sides;             // 2=左右 3=上+左右 4=四边, 默认 4
    int32_t  direction;         // 0=逆时针右下起 1=顺时针左下起, 默认 0
    int32_t  captureRange;      // 取色带深度系数, 深度=H/100*R/2, 默认 15
    int32_t  depthOverride;     // 直接指定深度 px, 0=不用
    int32_t  taps;              // 采样网格 (taps x taps), 默认 8; smoothing>0 时被覆盖
    int32_t  smoothing;         // 厂商平滑级别: >0 时每 S px 一采样点, 0=不启用
    int32_t  fpsCap;            // 处理帧率上限, 0=事件驱动不限
    int32_t  blackBars;         // 黑边自适应开关, 默认 1
    int32_t  blackBarLevel;     // 黑色判定阈值, 默认 3

    // 颜色链 (与厂商 AdjustLedColors 一致)
    double   brightness;        // 线性调光乘数 (HSL 链之后线性缩放 RGB), 默认 1.0; 拾音模式下乘在音量包络上
    double   vibrance;          // HSL S 乘数, 默认 9.0  (厂商 LedVibrance=90)
    double   hueMul;            // HSL H 乘数, 默认 1.0
    double   gamma;             // 默认 0.65
    double   rGain, gGain, bGain;   // 默认 1.0
    int32_t  colorCut;          // 默认 1
    int32_t  minBrightness;     // 默认 1
    int32_t  outputOrder;       // 0=RGB 1=GBR 2=BRG 3=RBG 4=GRB 5=BGR

    // 纯色模式 (modeIndex=11)
    uint8_t  solidRgb[3];       // 默认 {255,225,214}

    int32_t  waitDispPolicy;    // WAIT_DISP/熄屏时灯带策略: 0=保持最后帧 1=最暗(minB 灰帧)
                                //   2=熄灭(全零帧), 默认 2; 状态进入时发一帧, 每 2s 重发防掉电丢帧
    // v1.1 增补 (低灰保真, 默认关闭 = 厂商原链)
    int32_t  colorfix;          // 0=厂商原链 (默认)  1=绝对色度噪声门限修正
    double   sfloor;            // colorfix 色度门限 (8bit max-min), 默认 24

    // v1.3 增补 (M3 拾音)
    int32_t  audioSource;       // 主机拾音时: 0=系统声音回环(默认) 2=麦克风;
                                // 1=MCU 自拾音("Adb", MCU 特效语义)
    double   audioGain;         // 灵敏度乘数, 默认 1.0 (0.1..5.0)
    double   audioRelease;      // 包络回落时间常数(秒), 默认 0.30 (0.05..1.0)
    double   audioGate;         // 底噪门限(原始全频 RMS), 默认 0.003 (0..0.1)
    double   audioBass;         // 低频加权 0..1 (0=全频 1=纯低频), 默认 0.35
    int32_t  audioAgc;          // 慢速峰值自动增益(安静音源抬升), 默认 1
    int32_t  audioFollowVolume; // 音量字节跟随系统主音量标量, 默认 0
    double   audioSoft;         // 律动柔和度 0..1, 默认 0.5: 起音平滑(10-100ms)+
                                //   中段抬升(gamma 1→0.5)+呼吸下限(0-15%), 0=硬包络(频闪感)
    char     audioDevId[AC_AUDIO_ID_LEN];   // 麦克风源: 目标输入端点 ID (AC_GetAudioEndPoints),
                                           // 空 = 跟随系统默认输入; 设备拔出自动回退默认, 重插切回
    char     audioOutDevId[AC_AUDIO_ID_LEN]; // 回环源: 目标输出端点 ID, 空 = 跟随系统默认输出;
                                           // 可选非默认输出 (听 B 设备时灯跟 B); 回退语义同上

    // v1.4 增补 (M4 校准)
    int32_t  ledRotate;         // 灯序偏移: LED i 显示段 (i+rotate) mod N 的取色区
                                //   (校正灯带物理起点与协议起点不一致的少量错位);
                                //   钳到 ±N*12/100 (N<10 → 0, 同厂商 LedRotate), 默认 0

    // v1.6 增补 (追光柔化平滑 — 每设备, 默认关; 都是"输出层的柔化", 不动颜色链)
    //   档位刻意做成"包含关系"的三档, 而不是两个正交开关 —— 用户提出: 用户不该在两个难以分辨的
    //   组合里做选择; 而 2 在行为上严格包含 1 (均值被同一套更严的限幅接管, 再加逐灯柔化) ✓
    //   **符号 = 开关, 绝对值 = 档位** (用户 2026-10-09 提议: 让"关了再开"能记住上次选的档):
    //     +1 / +2 = 开启, 档位 1 / 2
    //     -1 / -2 = 关闭, 但记住档位是 1 / 2 (下次打开还是它)
    //      0      = 从未启用过 (默认; UI 打开时默认落到档位 1)
    //   档位: 1 = 抑制闪光 (整帧平均亮度的**突变限幅**, 每帧最多走 kChaseFlashGuard) → 治闪白/黑场;
    //         2 = 1 **再加**时域柔和 (逐灯逐通道指数平均, τ = kChaseSmoothTauMs) → 治快速摇镜的
    //             逐灯跳变, 代价是轻微拖影 (游戏建议停在 1)
    int32_t  chaseSmoothMode;

    uint32_t reserved[8];       // 必须置 0
} ACConfig;

// ---- 状态 (轮询) -----------------------------------------------------
typedef struct ACStatus {
    uint32_t structSize;        // = sizeof(ACStatus)
    int32_t  state;             // ACState
    int32_t  devType;           // ACDevType, -1=未知
    int32_t  devId;             // 握手应答低 2 位
    char     comPort[AC_PORT_LEN];  // 实际使用的端口 (自动切换后可能与配置不同)
    uint32_t ledStatusByte;     // 周期状态字节 (bit0 = 外接供电)
    uint32_t deviceChanged;     // 热插拔事件计数器 (UI 对比数值变化刷新设备列表)
    int32_t  monitorFallback;   // 1 = 配置显示器不可用, 已回退主屏
    uint32_t allBlackFrames;    // 连续全黑帧计数 (独占全屏/MPO 提示用)
    float    fps;               // 实际处理帧率
    float    procMs;            // 单帧处理平均耗时
    float    wireMs;            // 单帧串口写耗时
    float    cpuPercent;        // 核心线程 CPU% (预留, M2)
    int32_t  margins[4];        // 黑边 margin {底,右,顶,左}
    uint32_t frameCounter;      // 已发送帧数 (UI 判断是否在动)
    // v1.2 增补 (M2): 最近一帧实际下发的 LED 颜色, 前 ledCount*3 字节有效 (RGB 序,
    // 已过颜色链与输出色序, 即串口帧 payload); 追光/纯色/测试图模式随帧刷新,
    // MCU 特效模式 (2..10,12,13) 由 MCU 自渲染, 恒为 0
    uint8_t  ledRgb[AC_MAX_LEDS * 3];
    // v1.3 增补 (M3 拾音): 最近一帧 "Adz" 音量字节 (拾音模式随帧刷新, 其他模式 0);
    // 音源会话健康 0=停止 1=正常 2=重建中; 当前捕获端点友好名 (UTF-8)
    uint8_t  audioLevel;
    int32_t  audioOk;
    char     audioDev[AC_NAME_LEN];
} ACStatus;

// ---- 显示器 / 设备枚举 ------------------------------------------------
typedef struct ACMonitorInfo {
    char     name[AC_NAME_LEN];        // "\\.\DISPLAY5"
    char     adapter[AC_ADAPTER_LEN];  // "NVIDIA GeForce RTX 4060 Laptop GPU" (v1.5 起 64B, 不再截断)
    int32_t  width, height;            // 当前模式 (物理像素)
    int32_t  primary;                  // 1 = 主屏
} ACMonitorInfo;

typedef struct ACDeviceInfo {
    char     comPort[AC_PORT_LEN];  // "COM5"
    int32_t  devType;               // ACDevType
    int32_t  devId;
    int32_t  inUse;                 // 1 = 已被本核心或其他进程占用
} ACDeviceInfo;

// 音频端点 (v1.3): 拾音源选择用; capture=1 枚举输入(麦克风/线路输入), 0 枚举输出
typedef struct ACAudioDevInfo {
    char     id[AC_AUDIO_ID_LEN];   // MMDevice 端点 ID (配置到 ACConfig.audioDevId)
    char     name[AC_NAME_LEN];     // 友好名 (UTF-8)
    int32_t  isDefault;             // 1 = 当前系统默认 (仅 capture 集合内)
} ACAudioDevInfo;

// ---- 错误码 ----------------------------------------------------------
// 0=OK  1=参数无效  2=设备未找到  3=端口打开失败(被占用)
// 4=握手失败  5=已有实例运行  6=D3D/内部错误

#pragma pack(pop)

#ifdef __cplusplus
extern "C" {
#endif

// 版本: (major<<16)|(minor<<8)|patch
AC_API uint32_t   AC_Version(void);

// 生命周期
AC_API int32_t    AC_Start(const ACConfig* cfg);        // 异步: 立即返回, 状态经 AC_GetStatus 推进
AC_API void       AC_Stop(void);                        // 阻塞至线程退出, 发送关灯帧
AC_API int32_t    AC_SetConfig(const ACConfig* cfg);    // 原子热更新, 全字段可热改
AC_API void       AC_GetStatus(ACStatus* out);          // 非阻塞, 随时可调
AC_API int32_t    AC_GetLastDiag(char* buf, uint32_t cap);  // 最近错误文本 (UTF-8); 返回 0=无诊断, 1=有

// 多会话 (v1.3, M3): 每槽位独立会话线程; 上面的单设备接口 = slot 0 的别名。
// slot ∈ [0, AC_MAX_DEVICES); 重复 Start 同一 slot 返回错误码 5;
// 未启动过的 slot 的 GetStatusEx 返回 STOPPED + devType=-1。
AC_API int32_t    AC_StartEx(uint32_t slot, const ACConfig* cfg);
AC_API void       AC_StopEx(uint32_t slot);             // 阻塞至该会话退出, 发送关灯帧
AC_API int32_t    AC_SetConfigEx(uint32_t slot, const ACConfig* cfg);
AC_API void       AC_GetStatusEx(uint32_t slot, ACStatus* out);
AC_API int32_t    AC_GetLastDiagEx(uint32_t slot, char* buf, uint32_t cap);

// 枚举 (可在 Start 前调用; 设备枚举会短暂打开串口发握手, ~200ms/台)
AC_API int32_t    AC_GetMonitors(ACMonitorInfo* out, uint32_t max, int32_t* count);
AC_API int32_t    AC_GetDevices(ACDeviceInfo* out, uint32_t max, int32_t* count);

// 音频端点枚举 (v1.3): capture=1 输入设备(麦克风/线路输入) 0 输出设备; 只列 ACTIVE 端点
AC_API int32_t    AC_GetAudioEndPoints(int32_t capture, ACAudioDevInfo* out, uint32_t max, int32_t* count);

// 测试钩子 (校准/排障)
AC_API int32_t    AC_SetTestPattern(int32_t mode);      // 0=关 1=四象限色块 2=洋红常亮

#ifdef __cplusplus
}
#endif
