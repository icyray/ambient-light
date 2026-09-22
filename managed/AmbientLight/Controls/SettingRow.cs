// Controls/SettingRow.cs — Win11 设置风格行卡片 (M5.1): 图标 + 左侧标题/说明 + 右侧交互控件。
// 复用 WPF-UI CardControl 模板的三列 Grid (Icon | Header | Content), Header 由
// Title/Description 自动生成为两行文本, 免去每行 ~20 行样板 (全项目 ~30 行设置项)。
// 说明文字挂 AppTextHintBrush (应用自己的"次级文字"刷子, M5.2): 浅色主题下 WPF-UI 的
// TextFillColorTertiaryBrush (#72000000) 落在卡片底上对比度只有 ~3.3, 说明文字发虚;
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

    public SettingRow()
    {
        SetResourceReference(StyleProperty, typeof(Wpf.Ui.Controls.CardControl));
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
        => ((SettingRow)d).RebuildHeader();

    private void RebuildHeader()
    {
        var grid = new Grid();

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBlock
        {
            Text = Title,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(title, 0);
        grid.Children.Add(title);

        if (!string.IsNullOrEmpty(Description))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var desc = new TextBlock
            {
                Text = Description,
                FontSize = 12,
                Margin = new Thickness(0, 2, 8, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, AmbientLight.Services.ThemeService.AppTextHintKey);
            Grid.SetRow(desc, 1);
            grid.Children.Add(desc);
        }

        Header = grid;
    }
}
