using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SFTLauncher.Pages;

/// <summary>
/// bool → Visibility 转换器。
/// 用于绑定 Visibility 属性：直接绑定枚举属性依赖 WPF 默认转换器，
/// 在某些情况下会走 NotSupportedException 分支，因此这里显式转换。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>为 true 时是否折叠（默认 true 时显示）。</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (Invert)
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility visibility && visibility == Visibility.Visible;
}

/// <summary>bool 取反，用于 IsEnabled 等绑定。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}
