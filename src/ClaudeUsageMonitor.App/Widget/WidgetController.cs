using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeUsageMonitor.App.Interop;
using ClaudeUsageMonitor.App.Messaging;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Settings;
using CommunityToolkit.Mvvm.Messaging;

namespace ClaudeUsageMonitor.App.Widget;

/// <summary>
/// 위젯 창의 표시 모드(taskbar 도킹 / floating / 숨김)를 관리.
/// - 도킹: ABM_GETTASKBARPOS 물리픽셀 → DIP 변환 후 작업표시줄 위 우측 정렬(공간 예약 없음).
/// - WM_SETTINGCHANGE/WM_DISPLAYCHANGE/WM_DPICHANGED에서 재도킹, 30초마다 topmost 재주장.
/// </summary>
public sealed class WidgetController : IDisposable, IRecipient<WidgetModeChangedMessage>
{
    private const double DockMarginRight = 12;
    private const double DockGap = 6;

    private readonly WidgetWindow _window;
    private readonly MonitorSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly DispatcherTimer _topmostTimer;
    private bool _hiddenByFullscreen;

    public WidgetController(WidgetWindow window, MonitorSettings settings, SettingsStore settingsStore)
    {
        _window = window;
        _settings = settings;
        _settingsStore = settingsStore;

        _window.Moved += (left, top) =>
        {
            if (_settings.Mode == WidgetMode.Floating)
            {
                _settings.FloatingLeft = left;
                _settings.FloatingTop = top;
                _settingsStore.Save(_settings);
            }
        };

        _window.SourceInitialized += (_, _) => HookWindowMessages();

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _topmostTimer.Tick += (_, _) =>
        {
            if (_window.IsVisible)
            {
                WindowStyling.ReassertTopmost(_window.Hwnd);
            }
        };
        _topmostTimer.Start();

        WeakReferenceMessenger.Default.Register(this);
    }

    public void Receive(WidgetModeChangedMessage message) =>
        _window.Dispatcher.Invoke(() => ApplyMode(message.Mode));

    public void ApplyMode(WidgetMode mode)
    {
        switch (mode)
        {
            case WidgetMode.Hidden:
                _window.Hide();
                break;

            case WidgetMode.Floating:
                _window.AllowDrag = true;
                _window.Show();
                PositionFloating();
                WindowStyling.ReassertTopmost(_window.Hwnd);
                break;

            case WidgetMode.Taskbar:
            default:
                _window.AllowDrag = false;
                _window.Show();
                Dock();
                WindowStyling.ReassertTopmost(_window.Hwnd);
                break;
        }
    }

    /// <summary>전체화면 앱 감지 시 임시 숨김/복원 (Task 14에서 호출).</summary>
    public void SetFullscreenSuppressed(bool suppressed)
    {
        _window.Dispatcher.Invoke(() =>
        {
            if (suppressed && _window.IsVisible)
            {
                _hiddenByFullscreen = true;
                _window.Hide();
            }
            else if (!suppressed && _hiddenByFullscreen)
            {
                _hiddenByFullscreen = false;
                if (_settings.Mode != WidgetMode.Hidden)
                {
                    ApplyMode(_settings.Mode);
                }
            }
        });
    }

    private void PositionFloating()
    {
        if (_settings is { FloatingLeft: { } left, FloatingTop: { } top })
        {
            _window.Left = left;
            _window.Top = top;
            return;
        }

        var workArea = SystemParameters.WorkArea;
        _window.UpdateLayout();
        _window.Left = workArea.Right - _window.ActualWidth - 24;
        _window.Top = workArea.Bottom - _window.ActualHeight - 24;
    }

    private void Dock()
    {
        var taskbar = TaskbarLocator.GetTaskbar();
        _window.UpdateLayout();

        if (taskbar is not { } info)
        {
            PositionFloating();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(_window);
        double ToDipX(int px) => px / dpi.DpiScaleX;
        double ToDipY(int px) => px / dpi.DpiScaleY;

        var width = _window.ActualWidth;
        var height = _window.ActualHeight;

        if (info.AutoHide)
        {
            // 자동 숨김 작업표시줄은 rect가 화면 밖으로 밀려 있음 — 화면 하단 위에 표시
            var workArea = SystemParameters.WorkArea;
            _window.Left = workArea.Right - width - DockMarginRight;
            _window.Top = workArea.Bottom - height - DockGap;
            return;
        }

        if (info.Edge is TaskbarEdge.Bottom or TaskbarEdge.Top)
        {
            // 작업표시줄 "내부" 배치: 트레이 알림영역(시계) 왼쪽 빈 공간, 수직 중앙 정렬
            var rightLimit = ToDipX(info.Right) - DockMarginRight;
            var tray = TaskbarLocator.GetTrayNotifyRect();
            if (tray is { } t && t.Left > info.Left && t.Left < info.Right)
            {
                rightLimit = ToDipX(t.Left) - DockGap;
            }

            _window.Left = rightLimit - width;
            var taskbarTop = ToDipY(info.Top);
            var taskbarHeight = ToDipY(info.Bottom) - taskbarTop;
            _window.Top = taskbarTop + Math.Max(0, (taskbarHeight - height) / 2);
        }
        else
        {
            // 좌/우 세로 작업표시줄: 내부 하단(시계 위) 중앙 정렬
            var taskbarLeft = ToDipX(info.Left);
            var taskbarWidth = ToDipX(info.Right) - taskbarLeft;
            _window.Left = taskbarLeft + Math.Max(0, (taskbarWidth - width) / 2);
            _window.Top = ToDipY(info.Bottom) - height - 160;
        }
    }

    private void HookWindowMessages()
    {
        var source = HwndSource.FromHwnd(_window.Hwnd);
        source?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool _) =>
        {
            if (msg is NativeMethods.WM_SETTINGCHANGE or NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_DPICHANGED &&
                _settings.Mode == WidgetMode.Taskbar && _window.IsVisible)
            {
                _window.Dispatcher.BeginInvoke(Dock, DispatcherPriority.Background);
            }
            return IntPtr.Zero;
        });
    }

    public void Dispose()
    {
        _topmostTimer.Stop();
        WeakReferenceMessenger.Default.Unregister<WidgetModeChangedMessage>(this);
    }
}
