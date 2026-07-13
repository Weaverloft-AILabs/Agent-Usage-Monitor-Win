using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClaudeUsageMonitor.App.Converters;

/// <summary>
/// 퍼센트(0~100)를 star GridLength로 변환 — 미터 위 임계값 틱을 픽셀 폭 없이 배치한다.
/// ConverterParameter="rest"면 나머지(100-값)를 반환. 두 컬럼(값 / rest)으로 틱 경계를 만든다.
/// </summary>
public sealed class PercentStarConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var pct = value is double d ? d : 0d;
        pct = Math.Clamp(pct, 0d, 100d);
        if (parameter is string s && s.Equals("rest", StringComparison.OrdinalIgnoreCase))
        {
            pct = 100d - pct;
        }
        return new GridLength(Math.Max(0.0001, pct), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
