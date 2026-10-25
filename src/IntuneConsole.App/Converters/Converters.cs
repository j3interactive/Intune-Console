using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace IntuneConsole.App.Converters;

/// <summary>Maps a status word (Compliant, Failed, Offline…) to a tone: bad, warn, good or neutral.</summary>
internal static class Tone
{
    public static string Of(object? value)
    {
        var s = (value?.ToString() ?? "").ToLowerInvariant();
        if (s.Length == 0) return "neutral";
        if (s.Contains("noncompliant") || s.Contains("fail") || s.Contains("error") || s.Contains("conflict")
            || s.Contains("not encrypted")) return "bad";
        if (s.Contains("grace") || s.Contains("pending") || s.Contains("offline") || s.Contains("not in datto")
            || s.Contains("unknown") || s.Contains("stale") || s.Contains("never")) return "warn";
        if (s.Contains("compliant") || s.Contains("installed") || s.Contains("online") || s.Contains("succeeded")
            || s.Contains("completed") || s.Contains("success") || s.Contains("encrypted")) return "good";
        return "neutral";
    }

    public static readonly Brush BadBg = Freeze(Color.FromRgb(0xFD, 0xEC, 0xEA));
    public static readonly Brush BadFg = Freeze(Color.FromRgb(0xB4, 0x23, 0x18));
    public static readonly Brush WarnBg = Freeze(Color.FromRgb(0xFF, 0xF4, 0xE0));
    public static readonly Brush WarnFg = Freeze(Color.FromRgb(0x8A, 0x4B, 0x00));
    public static readonly Brush GoodBg = Freeze(Color.FromRgb(0xE8, 0xF5, 0xEC));
    public static readonly Brush GoodFg = Freeze(Color.FromRgb(0x1F, 0x6B, 0x3A));
    public static readonly Brush NeutralBg = Freeze(Color.FromRgb(0xF1, 0xF2, 0xF4));
    public static readonly Brush NeutralFg = Freeze(Color.FromRgb(0x4A, 0x52, 0x5C));

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public sealed class PillBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Tone.Of(value) switch
    {
        "bad" => Tone.BadBg,
        "warn" => Tone.WarnBg,
        "good" => Tone.GoodBg,
        _ => Tone.NeutralBg,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class PillForegroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Tone.Of(value) switch
    {
        "bad" => Tone.BadFg,
        "warn" => Tone.WarnFg,
        "good" => Tone.GoodFg,
        _ => Tone.NeutralFg,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
