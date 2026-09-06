// AmbientCore.h — 公共 C ABI (v1.0, x64 cdecl, 未修饰导出)
// 合同来源: docs/AmbientCore_API.md。规则:
//   - 结构体以 structSize 开头, 新增字段只能追加在末尾 (v1.1 增补: colorfix/sfloor, 见末尾注释)
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
    double   brightness;        // HSL L 乘数, 默认 1.0
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
} ACStatus;

// ---- 显示器 / 设备枚举 ------------------------------------------------
typedef struct ACMonitorInfo {
    char     name[AC_NAME_LEN];     // "\\.\DISPLAY5"
    char     adapter[AC_NAME_LEN];  // "NVIDIA GeForce ..."
    int32_t  width, height;         // 当前模式 (物理像素)
    int32_t  primary;               // 1 = 主屏
} ACMonitorInfo;

typedef struct ACDeviceInfo {
    char     comPort[AC_PORT_LEN];  // "COM5"
    int32_t  devType;               // ACDevType
    int32_t  devId;
    int32_t  inUse;                 // 1 = 已被本核心或其他进程占用
} ACDeviceInfo;

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

// 枚举 (可在 Start 前调用; 设备枚举会短暂打开串口发握手, ~200ms/台)
AC_API int32_t    AC_GetMonitors(ACMonitorInfo* out, uint32_t max, int32_t* count);
AC_API int32_t    AC_GetDevices(ACDeviceInfo* out, uint32_t max, int32_t* count);

// 测试钩子 (校准/排障)
AC_API int32_t    AC_SetTestPattern(int32_t mode);      // 0=关 1=四象限色块 2=洋红常亮

#ifdef __cplusplus
}
#endif
