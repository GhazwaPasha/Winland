using System;
using System.Globalization;
using System.Windows.Data;

namespace Winland.Converters;

/// <summary>
/// Maps a battery percentage to the matching glyph in the "Segoe Fluent
/// Icons" font — the exact same icon font (and codepoints) Windows 11's
/// own system tray uses for its battery flyout, so this renders pixel-for-
/// pixel identical to the real thing instead of a hand-drawn approximation.
/// Battery0..Battery9 = U+E850..U+E859 (0%-90%, rounded down to the nearest
/// 10%), Battery10 (100%) is the one outlier at U+E83F.
/// </summary>
public sealed class BatteryGlyphConverter : IValueConverter
{
    private const int Battery0 = 0xE850;
    private const int Battery10 = 0xE83F;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var percent = value is int i ? i : 0;
        var tier = Math.Clamp(percent / 10, 0, 10);
        var codepoint = tier == 10 ? Battery10 : Battery0 + tier;
        return char.ConvertFromUtf32(codepoint);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
