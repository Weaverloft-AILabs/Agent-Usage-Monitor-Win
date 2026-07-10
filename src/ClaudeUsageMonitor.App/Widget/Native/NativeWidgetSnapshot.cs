using System.Drawing;

namespace ClaudeUsageMonitor.App.Widget.Native;

/// <summary>
/// 임베드 위젯이 그릴 표시 상태의 불변 스냅샷.
/// WPF 측(WidgetViewModel/ThemeManager)에서 만들어 네이티브 렌더 스레드로 전달된다 —
/// 렌더러가 WPF 객체를 만지지 않도록 색상까지 System.Drawing.Color로 변환해 담는다.
/// </summary>
public sealed record NativeWidgetSnapshot(
    bool CliMissing,
    double FiveHourPct,
    string FiveHourResetText,
    Color FiveHourBar,
    double SevenDayPct,
    string SevenDayResetText,
    Color SevenDayBar,
    string ExhaustionText,
    bool UpdateAvailable,
    NativeWidgetPalette Palette);

/// <summary>테마(다크/라이트)에서 유도된 위젯 팔레트 — Themes/*.xaml의 Widget* 브러시와 동일 값.</summary>
public sealed record NativeWidgetPalette(
    Color Background,
    Color Border,
    Color Text,
    Color TextStrong,
    Color TextDim,
    Color GaugeTrack,
    Color Prediction)
{
    /// <summary>DarkTheme.xaml의 Widget* 브러시 값.</summary>
    public static readonly NativeWidgetPalette Dark = new(
        Background: Color.FromArgb(0xD9, 0x1C, 0x1E, 0x28),
        Border: Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),
        Text: Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF),
        TextStrong: Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF),
        TextDim: Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF),
        GaugeTrack: Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF),
        Prediction: Color.FromArgb(0xFF, 0xE0, 0x8B, 0x6B));

    /// <summary>LightTheme.xaml의 Widget* 브러시 값.</summary>
    public static readonly NativeWidgetPalette Light = new(
        Background: Color.FromArgb(0xE6, 0xF2, 0xF3, 0xF8),
        Border: Color.FromArgb(0x33, 0x00, 0x00, 0x00),
        Text: Color.FromArgb(0xDD, 0x1F, 0x21, 0x28),
        TextStrong: Color.FromArgb(0xEE, 0x14, 0x16, 0x1E),
        TextDim: Color.FromArgb(0x88, 0x1F, 0x21, 0x28),
        GaugeTrack: Color.FromArgb(0x2E, 0x00, 0x00, 0x00),
        Prediction: Color.FromArgb(0xFF, 0xC4, 0x67, 0x4A));
}
