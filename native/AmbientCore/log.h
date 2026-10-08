// log.h — 核心轻量文件日志 (exe 旁 log/ambientcore.log), 多线程安全
//
// **级别**: DEBUG < INFO < WARN < ERROR。默认只写 INFO 及以上 ——
//   诊断类 (电源事件 / 相位时间线 / 60s 状态线 / 宽限忽略 / 配置推送 / 采集重建) 全是 DEBUG,
//   平时不落盘; 要抓时序时把环境变量 `AL_LOG_LEVEL=debug` (或 verbose) 再跑一次即可。
//   (用户 2026-10-08: "诊断类日志提到 debug 级, 以后再加过滤/轮转" —— 这里只加了最简的开关,
//    ⚠️ **尚未做轮转/大小上限**。)
// **重复抑制**: 串口重连失败 (open/handshake) 只在**首次**记 WARN, 之后同一次断连里降为 DEBUG ——
//   实测这两行占整份日志的 67% (每 ~2.7s 一条)。
#pragma once

namespace alog {
// 解析日志路径 (GetModuleFileNameW(NULL) 所在目录 \log\), 首次写入时懒创建
void Write(const char* lvl, const char* fmt, ...);
// DEBUG 级别是否落盘 (环境变量 AL_LOG_LEVEL=debug|verbose; 缺省关)
bool DebugEnabled();
} // namespace alog

// DEBUG 宏带条件判断 → 关掉时**连参数都不求值** (没有格式化的开销)
#define ACLOGD(...) do { if (::alog::DebugEnabled()) ::alog::Write("DEBUG", __VA_ARGS__); } while (0)
#define ACLOGI(...) ::alog::Write("INFO ", __VA_ARGS__)
#define ACLOGW(...) ::alog::Write("WARN ", __VA_ARGS__)
#define ACLOGE(...) ::alog::Write("ERROR", __VA_ARGS__)
