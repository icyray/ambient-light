// Controls/SettingRow.cs — Win11 设置风格行卡片 (M5.1): 图标 + 左侧标题/说明 + 右侧交互控件。
// 复用 WPF-UI CardControl 模板的三列 Grid (Icon | Header | Content); Header 用 App 资源里的
// `SettingRowHeaderTemplate` **装一次** (标题 + 说明两行), 之后 Title/Description 变化只改两行文本
// —— 结构在 XAML, 代码不重复生成 (全项目 ~30 处)。
// 说明文字挂 TextFillColorSecondaryBrush (WPF-UI 的"次要文字"档, 随主题自动跟随)。
// 为什么不用 Tertiary: 浅色主题下它是 #72000000 (45% 黑), 落在卡片底上对比度只有 ~3.3 → 发虚;
// 也别用 Opacity 压暗文字 (同样会掉到 3:1 以下)。
//
// 样式注意: WPF 隐式样式按"精确类型"匹配, SettingRow 作为 CardControl 子类拿不到
// ControlsDictionary 里 TargetType=CardControl 的隐式样式 (无模板 → 只渲染 Content)。
// 构造时用 SetResourceReference 把 Style 指到 typeof(CardControl) 键上, 走资源树
// 动态查找 (App 级 ControlsDictionary 命中, 主题热切换也跟随)。
using System.Windows;
using System.Windows.Controls;

namespace AmbientLight.Controls;

public class SettingRow : Wpf.Ui.Controls.CardControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(null, OnTextChanged));

    // Header 里两行文本的引用 (装一次模板后缓存; 之后只改 .Text / Visibility)
    private TextBlock? _titleText;
    private TextBlock? _descText;

    public SettingRow()
    {
        SetResourceReference(StyleProperty, typeof(Wpf.Ui.Controls.CardControl));
        LoadHeaderTemplate();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SettingRow)d).UpdateHeaderTexts();

    /// <summary>把 Header 结构从 App 资源装一次。模板里固定: Children[0] = 标题 / [1] = 说明。</summary>
    private void LoadHeaderTemplate()
    {
        if (TryFindResource("SettingRowHeaderTemplate") is not DataTemplate tpl
            || tpl.LoadContent() is not Grid grid
            || grid.Children.Count < 2
            || grid.Children[0] is not TextBlock title
            || grid.Children[1] is not TextBlock desc)
            return;                              // 资源缺失时保持空 Header (探针有接线断言守着)
        _titleText = title;
        _descText = desc;
        Header = grid;
        UpdateHeaderTexts();
    }

    /// <summary>只有文本在动 (构造期也可能先于 XAML 属性应用被调 → 判空)。</summary>
    private void UpdateHeaderTexts()
    {
        if (_titleText is null || _descText is null) return;
        _titleText.Text = Title ?? "";
        bool hasDesc = !string.IsNullOrEmpty(Description);
        _descText.Text = Description ?? "";
        _descText.Visibility = hasDesc ? Visibility.Visible : Visibility.Collapsed;
    }
}
