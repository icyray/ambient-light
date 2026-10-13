// Localization/Loc.cs — 文案读取入口 (M5.3 i18n 第二步的基础设施)。
//
// 机制 = WPF 原生资源字典: 唯一文案源 = `Strings.zh.xaml` (App.xaml 合并)。
//   · XAML 侧 (8 批) 用 `{DynamicResource 键}` (换字典自动刷新, 与主题同款);
//   · C# 侧走本类的 T/F —— 换语言 = 替换字典 + 广播重取 (已实现, 见 Services/LanguageService)。
//
// 约定:
//   · 键稳定、值可改 —— 顺文案/翻译只动 Strings.*.xaml, 不动代码;
//   · 缺键时返回 key 本身: 显式可见的失败, 探针有完整性断言守着 (别悄悄显示空白);
//   · 值不带首尾空白 (空格在调用处拼); 值里的 {0}/{1} 是格式占位, 字面大括号写 {{ }}。
using System.Windows;

namespace AmbientLight.Localization;

public static class Loc
{
    /// <summary>取文案; 缺键时返回 key 本身。</summary>
    public static string T(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;

    /// <summary>取文案并格式化 (string.Format; 值里用 {0}/{1} 占位)。</summary>
    public static string F(string key, params object?[] args)
        => string.Format(T(key), args);
}
