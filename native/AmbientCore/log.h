// log.h — 核心轻量文件日志 (exe 旁 log/ambientcore.log), 多线程安全
#pragma once

namespace alog {
// 解析日志路径 (GetModuleFileNameW(NULL) 所在目录 \log\), 首次写入时懒创建
void Write(const char* lvl, const char* fmt, ...);
} // namespace alog

#define ACLOGI(...) ::alog::Write("INFO ", __VA_ARGS__)
#define ACLOGW(...) ::alog::Write("WARN ", __VA_ARGS__)
#define ACLOGE(...) ::alog::Write("ERROR", __VA_ARGS__)
