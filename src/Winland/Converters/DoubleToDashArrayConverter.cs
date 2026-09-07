using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Winland.Converters;

/// <summary>
/// Turns a single circumference value (in stroke-thickness units — see
/// NotchViewModel's OuterCircumference/InnerCircumference) into the
/// [dash, gap] pair Shape.StrokeDashArray needs to draw "one dash exactly
/// as long as the whole circle", which combined with StrokeDashOffset is
/// what produces the percentage-arc ring gauge.
/// </summary>
public sealed class DoubleToDashArrayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var length = value is double d ? d : 0;
        return new DoubleCollection { length, length };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
