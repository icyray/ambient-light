// Views/DiagText.cs — 核心诊断文本 (native 英文原文 → 字典中文) 的**唯一映射表**。
//
// 背景: DiagBar / 启动失败对话框显示的 LastDiag 来自 native (AmbientCore.cpp 的 SetDiag),
// native 不读字典 (i18n 只管托管层) → 已知原文在**展示层**做一次映射; 文案键在
// Strings.zh.xaml 的 Diag.* 段。
// 约定:
//   · 只收录 native 确定发过的原文 (按稳定前缀匹配; 参数从原文里截出来传进 Loc.F);
//   · **未收录的原文原样透传** —— 宁可显示英文, 不吞信息 (native 直接写中文的条目, 如
//     "指定端口 ... 未应答", 也走这条透传路径);
//   · 加新文案 = native SetDiag 补一条 + 这里补一条 + 字典补键 (探针打真值表, 改别名就现形)。
using System;
using AmbientLight.Localization;

namespace AmbientLight.Views;

public static class DiagText
{
    /// <summary>native 诊断原文 → 本地化文案; 未收录的原样返回。</summary>
    public static string Translate(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        // WireFail: "serial write failed on COM5, reconnecting"
        // (格式是 "serial %s on %s, reconnecting", why 目前恒为 "write failed")
        const string writeFail = "serial write failed on ";
        const string reconnecting = ", reconnecting";
        if (raw.StartsWith(writeFail, StringComparison.Ordinal)
            && raw.EndsWith(reconnecting, StringComparison.Ordinal))
            return Loc.F("Diag.SerialWriteFailed", raw[writeFail.Length..^reconnecting.Length]);

        if (raw.StartsWith("device lost: no usable port", StringComparison.Ordinal))
            return Loc.T("Diag.NoUsablePort");
        if (raw.StartsWith("capture build failed", StringComparison.Ordinal))
            return Loc.T("Diag.CaptureFailed");
        if (raw.StartsWith("core thread crashed", StringComparison.Ordinal))
            return Loc.T("Diag.CoreCrashed");

        return raw;
    }
}
