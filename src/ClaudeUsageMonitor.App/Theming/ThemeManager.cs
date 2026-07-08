using System.Windows;
using ClaudeUsageMonitor.Core.Models;
using Microsoft.Win32;

namespace ClaudeUsageMonitor.App.Theming;

/// <summary>
/// 다크/라이트/시스템 테마 적용. 시스템 모드는 HKCU AppsUseLightTheme를 따르고,
/// OS 테마 변경(UserPreferenceChanged)을 감지해 실시간 반영한다.
/// </summary>
public static class ThemeManager
{
    private static ThemePreference _preference = ThemePreference.Dark;

    /// <summary>현재 유효 테마가 다크인지 (System 설정 해석 포함).</summary>
    public static bool IsDarkEffective { get; private set; } = true;

    /// <summary>유효 테마(다크 여부)가 실제로 바뀌었을 때 발생.</summary>
    public static event Action? EffectiveThemeChanged;

    public static void Initialize(ThemePreference preference)
    {
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (_preference == ThemePreference.System && args.Category == UserPreferenceCategory.General)
            {
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(_preference));
            }
        };
        Apply(preference);
    }

    public static void Apply(ThemePreference preference)
    {
        _preference = preference;
        var dark = preference switch
        {
            ThemePreference.Light => false,
            ThemePreference.System => SystemPrefersDark(),
            _ => true,
        };

        var app = Application.Current;
        if (app is not null)
        {
            var replacement = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}Theme.xaml"),
            };
            var dictionaries = app.Resources.MergedDictionaries;
            if (dictionaries.Count > 0)
            {
                dictionaries[0] = replacement;
            }
            else
            {
                dictionaries.Add(replacement);
            }
        }

        if (IsDarkEffective != dark)
        {
            IsDarkEffective = dark;
            EffectiveThemeChanged?.Invoke();
        }
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException)
        {
            return true; // 조회 실패 시 다크 기본
        }
    }
}
