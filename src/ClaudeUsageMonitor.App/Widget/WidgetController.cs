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
/// - 도킹: 작업표시줄(주/보조) 내부 오버레이. 드래그로 자유 이동 가능하며 드롭 시 가장 가까운
///   작업표시줄에 스냅하고 위치를 (모니터 장치명, 비율)로 저장 — 해상도/DPI 변화에 자동 적응.
/// - WM_SETTINGCHANGE/WM_DISPLAYCHANGE/WM_DPICHANGED에서 재도킹, 2초 주기 + 포그라운드 변경 시 topmost 재주장.
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
    private int _lastReassertTick;

    // GC로 콜백이 수집되지 않도록 delegate를 필드로 유지 (필수)
    private readonly NativeMethods.WinEventDelegate _foregroundChanged;
    private readonly NativeMethods.WinEventDelegate _zOrderChanged;
    private IntPtr _winEventHook;
    private IntPtr _reorderEventHook;
    private uint _taskbarCreatedMessage;

    /// <summary>Explorer 재시작으로 작업표시줄이 재생성됨 (트레이 아이콘 재설치 필요).</summary>
    public event Action? TaskbarRecreated;

    public WidgetController(WidgetWindow window, MonitorSettings settings, SettingsStore settingsStore)
    {
        _window = window;
        _settings = settings;
        _settingsStore = settingsStore;

        _window.Moved += (left, top) =>
        {
            switch (_settings.Mode)
            {
                case WidgetMode.Floating:
                    _settings.FloatingLeft = left;
                    _settings.FloatingTop = top;
                    _settingsStore.Save(_settings);
                    break;
                case WidgetMode.Taskbar:
                    // 드래그 종료 → 가장 가까운 작업표시줄(멀티모니터 포함)에 스냅 + 위치 비율 저장
                    SnapToNearestTaskbar();
                    break;
            }
        };

        _window.SourceInitialized += (_, _) => HookWindowMessages();

        // 소진 예측 텍스트 등장/소멸로 위젯 폭이 변하면 트레이 침범 방지를 위해 재도킹
        _window.SizeChanged += (_, _) =>
        {
            if (_settings.Mode == WidgetMode.Taskbar && _window.IsVisible)
            {
                _window.Dispatcher.BeginInvoke(Dock, DispatcherPriority.Background);
            }
        };

        // 작업표시줄도 topmost라 사용자 상호작용(오버플로 ^, 시계, 빈 영역 클릭 등) 시 위젯 위로 올라옴 —
        // ① 포그라운드 변경 이벤트, ② 최상위 z-순서 변경(EVENT_OBJECT_REORDER — 포그라운드가 안 바뀌는
        //    작업표시줄 상호작용까지 포착, 100ms 스로틀), ③ 1초 주기 폴백에서 topmost 재주장
        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _topmostTimer.Tick += (_, _) => ReassertIfVisible();
        _topmostTimer.Start();

        _foregroundChanged = (_, _, _, _, _, _, _) => ReassertIfVisible();
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundChanged, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);

        _zOrderChanged = (_, _, _, _, _, _, _) =>
        {
            // z-순서 변경은 폭주할 수 있음(창 드래그 등) — 스로틀로 보호. 재주장 자체는 noop 수준으로 저렴
            var now = Environment.TickCount;
            if (now - _lastReassertTick >= 100)
            {
                ReassertIfVisible();
            }
        };
        _reorderEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_REORDER, NativeMethods.EVENT_OBJECT_REORDER,
            IntPtr.Zero, _zOrderChanged, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);

        WeakReferenceMessenger.Default.Register(this);
    }

    private void ReassertIfVisible()
    {
        // 메뉴가 열린 동안 재주장하면 위젯이 메뉴 위로 올라가 메뉴를 가림 — 반드시 건너뜀
        if (_window.IsVisible && !_window.IsContextMenuOpen)
        {
            _lastReassertTick = Environment.TickCount;
            WindowStyling.ReassertTopmost(_window.Hwnd);
        }
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
                _window.AllowDrag = true; // 작업표시줄 내 자유 이동 (드롭 시 스냅)
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
        _window.UpdateLayout();

        var taskbars = TaskbarLocator.GetAllTaskbars();
        if (taskbars.Count == 0)
        {
            PositionFloating();
            return;
        }

        var target = SelectTargetTaskbar(taskbars);

        // 자동 숨김 주 작업표시줄: rect가 화면 밖으로 밀려 있음 — 화면 하단 위 폴백
        if (target.IsPrimary && TaskbarLocator.GetTaskbar() is { AutoHide: true })
        {
            var workArea = SystemParameters.WorkArea;
            _window.Left = workArea.Right - _window.ActualWidth - DockMarginRight;
            _window.Top = workArea.Bottom - _window.ActualHeight - DockGap;
            return;
        }

        // 물리 픽셀 기준 배치 (모니터별 DPI 차이는 이동 후 WM_DPICHANGED → 재도킹으로 수렴)
        var dpi = VisualTreeHelper.GetDpi(_window);
        var widthPx = (int)Math.Round(_window.ActualWidth * dpi.DpiScaleX);
        var heightPx = (int)Math.Round(_window.ActualHeight * dpi.DpiScaleY);

        int x, y;
        if (target.Edge is TaskbarEdge.Bottom or TaskbarEdge.Top)
        {
            var (spanStart, spanEnd) = HorizontalSpan(target, dpi.DpiScaleX);
            var maxX = Math.Max(spanStart, spanEnd - widthPx);
            x = _settings.TaskbarOffsetRatio is { } ratio
                ? spanStart + (int)Math.Round(Math.Clamp(ratio, 0, 1) * (maxX - spanStart))
                : maxX; // 기본: 트레이 왼쪽 (우측 정렬)
            y = target.Top + Math.Max(0, (target.Height - heightPx) / 2);
        }
        else
        {
            // 좌/우 세로 작업표시줄: 비율은 세로 방향으로 적용, 기본은 하단(시계 위)
            var gapPx = (int)Math.Round(DockGap * dpi.DpiScaleY);
            var spanStart = target.Top + gapPx;
            var spanEnd = target.Bottom - (int)Math.Round(160 * dpi.DpiScaleY);
            var maxY = Math.Max(spanStart, spanEnd - heightPx);
            y = _settings.TaskbarOffsetRatio is { } ratio
                ? spanStart + (int)Math.Round(Math.Clamp(ratio, 0, 1) * (maxY - spanStart))
                : maxY;
            x = target.Left + Math.Max(0, (target.Width - widthPx) / 2);
        }

        NativeMethods.SetWindowPos(_window.Hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>저장된 모니터의 작업표시줄 우선, 없으면(모니터 분리 등) 주 작업표시줄.</summary>
    private TaskbarInstance SelectTargetTaskbar(IReadOnlyList<TaskbarInstance> taskbars)
    {
        if (_settings.TaskbarMonitorDevice is { } device)
        {
            foreach (var taskbar in taskbars)
            {
                if (taskbar.MonitorDevice == device)
                {
                    return taskbar;
                }
            }
        }

        foreach (var taskbar in taskbars)
        {
            if (taskbar.IsPrimary)
            {
                return taskbar;
            }
        }
        return taskbars[0];
    }

    /// <summary>가로 작업표시줄 내 위젯이 놓일 수 있는 물리픽셀 X 구간 (주 모니터는 트레이 왼쪽까지).</summary>
    private static (int Start, int End) HorizontalSpan(TaskbarInstance target, double dpiScaleX)
    {
        var gapPx = (int)Math.Round(DockGap * dpiScaleX);
        var marginPx = (int)Math.Round(DockMarginRight * dpiScaleX);
        var start = target.Left + gapPx;
        var end = target.Right - marginPx;
        if (target.IsPrimary &&
            TaskbarLocator.GetTrayNotifyRect() is { } tray &&
            tray.Left > target.Left && tray.Left < target.Right)
        {
            end = tray.Left - gapPx;
        }
        return (start, Math.Max(start, end));
    }

    /// <summary>
    /// 드래그 종료 위치에서 가장 가까운 작업표시줄을 찾아 위치 비율을 저장하고 스냅.
    /// 다른 모니터의 작업표시줄로도 이동 가능하며, 비율 저장이라 해상도/DPI가 달라도 복원된다.
    /// </summary>
    private void SnapToNearestTaskbar()
    {
        if (!NativeMethods.GetWindowRect(_window.Hwnd, out var rect))
        {
            return;
        }

        var taskbars = TaskbarLocator.GetAllTaskbars();
        if (taskbars.Count == 0)
        {
            Dock();
            return;
        }

        var centerX = (rect.Left + rect.Right) / 2;
        var centerY = (rect.Top + rect.Bottom) / 2;
        var target = taskbars[0];
        var best = long.MaxValue;
        foreach (var taskbar in taskbars)
        {
            var dx = (long)Math.Max(0, Math.Max(taskbar.Left - centerX, centerX - taskbar.Right));
            var dy = (long)Math.Max(0, Math.Max(taskbar.Top - centerY, centerY - taskbar.Bottom));
            var distance = dx * dx + dy * dy;
            if (distance < best)
            {
                best = distance;
                target = taskbar;
            }
        }

        var dpi = VisualTreeHelper.GetDpi(_window);
        double ratio;
        if (target.Edge is TaskbarEdge.Bottom or TaskbarEdge.Top)
        {
            var (spanStart, spanEnd) = HorizontalSpan(target, dpi.DpiScaleX);
            var maxX = Math.Max(spanStart, spanEnd - (rect.Right - rect.Left));
            ratio = maxX == spanStart
                ? 0
                : Math.Clamp((double)(rect.Left - spanStart) / (maxX - spanStart), 0, 1);
        }
        else
        {
            var gapPx = (int)Math.Round(DockGap * dpi.DpiScaleY);
            var spanStart = target.Top + gapPx;
            var spanEnd = target.Bottom - (int)Math.Round(160 * dpi.DpiScaleY);
            var maxY = Math.Max(spanStart, spanEnd - (rect.Bottom - rect.Top));
            ratio = maxY == spanStart
                ? 0
                : Math.Clamp((double)(rect.Top - spanStart) / (maxY - spanStart), 0, 1);
        }

        _settings.TaskbarMonitorDevice = target.MonitorDevice;
        _settings.TaskbarOffsetRatio = ratio;
        _settingsStore.Save(_settings);

        Dock();
    }

    private void HookWindowMessages()
    {
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        var source = HwndSource.FromHwnd(_window.Hwnd);
        source?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool _) =>
        {
            if (msg is NativeMethods.WM_SETTINGCHANGE or NativeMethods.WM_DISPLAYCHANGE or NativeMethods.WM_DPICHANGED &&
                _settings.Mode == WidgetMode.Taskbar && _window.IsVisible)
            {
                _window.Dispatcher.BeginInvoke(Dock, DispatcherPriority.Background);
            }
            if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
            {
                // Explorer 재시작 — 트레이 아이콘 재설치 + 재도킹
                _window.Dispatcher.BeginInvoke(() =>
                {
                    TaskbarRecreated?.Invoke();
                    if (_settings.Mode != WidgetMode.Hidden)
                    {
                        ApplyMode(_settings.Mode);
                    }
                }, DispatcherPriority.Background);
            }
            return IntPtr.Zero;
        });
    }

    public void Dispose()
    {
        _topmostTimer.Stop();
        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        if (_reorderEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_reorderEventHook);
            _reorderEventHook = IntPtr.Zero;
        }
        WeakReferenceMessenger.Default.Unregister<WidgetModeChangedMessage>(this);
    }
}
