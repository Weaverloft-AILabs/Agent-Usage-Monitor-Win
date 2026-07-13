using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeUsageMonitor.App.Interop;
using ClaudeUsageMonitor.App.Messaging;
using ClaudeUsageMonitor.App.Widget.Native;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Settings;
using CommunityToolkit.Mvvm.Messaging;

namespace ClaudeUsageMonitor.App.Widget;

/// <summary>
/// 위젯 창의 표시 모드(taskbar 도킹 / floating / 숨김)를 관리.
/// - taskbar 모드 1차 경로: 작업표시줄 자식으로 임베드(NativeWidgetHost) — 시작 메뉴/플라이아웃에도
///   가려지지 않음. 임베드 실패가 반복되면 이 세션 동안 오버레이로 폴백.
/// - 오버레이(폴백/floating): 드래그로 자유 이동, 드롭 시 가장 가까운 작업표시줄에 스냅하고
///   위치를 (모니터 장치명, 비율)로 저장 — 해상도/DPI 변화에 자동 적응.
/// - WM_SETTINGCHANGE/WM_DISPLAYCHANGE/WM_DPICHANGED에서 재도킹.
/// - 오버레이 topmost 재주장 4경로: 포그라운드 변경 훅 / SHOW·HIDE·REORDER 훅(100ms 스로틀) /
///   셸 이벤트 후 트레일링 버스트(150ms×8) / 1초 폴백 타이머 — 어느 것도 제거 금지.
/// </summary>
public sealed class WidgetController : IDisposable, IRecipient<WidgetModeChangedMessage>
{
    private const double DockMarginRight = 12;
    private const double DockGap = 6;
    private const int EmbedFailureLimit = 3;

    private readonly WidgetWindow _window;
    private readonly MonitorSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly NativeWidgetHost _nativeHost;
    private readonly DispatcherTimer _topmostTimer;
    private readonly DispatcherTimer _burstTimer;
    private bool _hiddenByFullscreen;
    private int _lastReassertTick;
    private int _burstTicksLeft;

    /// <summary>연속 임베드 실패 횟수 — 한도 도달 시 이 세션에서는 오버레이로 고정.</summary>
    private int _embedFailStreak;
    private bool _embedBroken;
    private int _lastEmbedAttemptTick;

    // GC로 콜백이 수집되지 않도록 delegate를 필드로 유지 (필수)
    private readonly NativeMethods.WinEventDelegate _foregroundChanged;
    private readonly NativeMethods.WinEventDelegate _zOrderChanged;
    private IntPtr _winEventHook;
    private IntPtr _reorderEventHook;
    private uint _taskbarCreatedMessage;

    /// <summary>Explorer 재시작으로 작업표시줄이 재생성됨 (트레이 아이콘 재설치 필요).</summary>
    public event Action? TaskbarRecreated;

    public WidgetController(
        WidgetWindow window, MonitorSettings settings, SettingsStore settingsStore,
        NativeWidgetHost nativeHost)
    {
        _window = window;
        _settings = settings;
        _settingsStore = settingsStore;
        _nativeHost = nativeHost;

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

        // 임베드 위젯을 드래그로 다른 모니터 작업표시줄 위에 놓으면 그 모니터로 재임베드 (세로면 오버레이 폴백).
        // 설정(TaskbarMonitorDevice)은 호스트가 이미 저장했으므로 여기선 taskbar 모드 재적용만.
        _nativeHost.MonitorChangeRequested += () =>
            _window.Dispatcher.BeginInvoke(() => ApplyMode(WidgetMode.Taskbar), DispatcherPriority.Background);

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
        _topmostTimer.Tick += (_, _) =>
        {
            CheckEmbedHealth();
            ReassertIfVisible();
        };
        _topmostTimer.Start();

        _foregroundChanged = (_, _, _, _, _, _, _) => ReassertIfVisible();
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundChanged, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);

        // 셸 이벤트 버스트(플라이아웃/시작 메뉴 닫힘 등)의 마지막 이벤트가 스로틀에 버려지면
        // 폴백 타이머(1초)까지 가려짐 — 트레일링 버스트로 마지막 이벤트 후 ~1.2초간 150ms 간격 재주장.
        // (시작 메뉴 닫힘처럼 셸이 작업표시줄 밴드를 늦게 되돌리는 경우까지 커버)
        _burstTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _burstTimer.Tick += (_, _) =>
        {
            ReassertIfVisible();
            if (--_burstTicksLeft <= 0)
            {
                _burstTimer.Stop();
            }
        };

        _zOrderChanged = (_, _, _, _, _, _, _) =>
        {
            // SHOW/HIDE/REORDER는 폭주할 수 있음(창 드래그 등) — 즉시 재주장은 100ms 스로틀,
            // 트레일링 버스트는 항상 재장전 (재주장 자체는 noop 수준으로 저렴)
            var now = Environment.TickCount;
            if (now - _lastReassertTick >= 100)
            {
                ReassertIfVisible();
            }
            _burstTicksLeft = 8;
            if (!_burstTimer.IsEnabled)
            {
                _burstTimer.Start();
            }
        };
        _reorderEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_REORDER,
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
        _window.Dispatcher.Invoke(() =>
        {
            // 사용자 주도 재적용(설정 저장/메뉴) — 임베드에 새 기회 (자동 헬스체크는 리셋하지 않음)
            _embedFailStreak = 0;
            _embedBroken = false;
            ApplyMode(message.Mode);
        });

    public void ApplyMode(WidgetMode mode)
    {
        switch (mode)
        {
            case WidgetMode.Hidden:
                _nativeHost.Deactivate();
                _window.Hide();
                break;

            case WidgetMode.Floating:
                _nativeHost.Deactivate();
                _window.AllowDrag = true;
                _window.Show();
                PositionFloating();
                WindowStyling.ReassertTopmost(_window.Hwnd);
                break;

            case WidgetMode.Taskbar:
            default:
                if (TryEmbed())
                {
                    _window.Hide(); // 임베드 성공 — 오버레이 창은 숨김 (메뉴 소유자로만 사용)
                    break;
                }
                _nativeHost.Deactivate();
                _window.AllowDrag = true; // 작업표시줄 내 자유 이동 (드롭 시 스냅)
                _window.Show();
                Dock();
                WindowStyling.ReassertTopmost(_window.Hwnd);
                break;
        }
    }

    /// <summary>임베드 시도 + 연속 실패 카운트. 한도 도달 시 이 세션에서는 오버레이 고정.</summary>
    private bool TryEmbed()
    {
        if (!_settings.TaskbarEmbedEnabled || _embedBroken)
        {
            return false;
        }
        _lastEmbedAttemptTick = Environment.TickCount;
        if (_nativeHost.TryActivate())
        {
            _embedFailStreak = 0;
            return true;
        }
        if (++_embedFailStreak >= EmbedFailureLimit)
        {
            _embedBroken = true; // Windows 업데이트로 임베드가 깨진 경우 등 — 오버레이가 안전망
            Native.NativeWidgetLog.Write($"embed broken after {EmbedFailureLimit} consecutive failures — overlay for this session");
        }
        return false;
    }

    /// <summary>
    /// 1초 폴백 타이머에서 임베드 상태 감시 — Explorer 재시작으로 부모가 죽으면 자식 창도
    /// 함께 파괴되므로(메시지 두절) 여기서 감지해 재임베드한다. 프로세스 재기동은 불필요.
    /// 시작 시점의 일시 실패(셸 바쁨 등)도 5초 간격으로 재시도해 복구한다.
    /// </summary>
    private void CheckEmbedHealth()
    {
        if (_settings.Mode != WidgetMode.Taskbar || _hiddenByFullscreen ||
            !_settings.TaskbarEmbedEnabled || _embedBroken)
        {
            return;
        }
        if (_nativeHost.IsActive && !_nativeHost.IsHealthy)
        {
            Native.NativeWidgetLog.Write("embed unhealthy (parent gone?) — re-embedding");
            _nativeHost.Deactivate();
            ApplyMode(WidgetMode.Taskbar);
        }
        else if (!_nativeHost.IsActive && Environment.TickCount - _lastEmbedAttemptTick >= 5000)
        {
            ApplyMode(WidgetMode.Taskbar); // 재시도 한도는 TryEmbed의 연속 실패 카운트가 관리
        }
    }

    /// <summary>전체화면 앱 감지 시 임시 숨김/복원 (Task 14에서 호출).</summary>
    public void SetFullscreenSuppressed(bool suppressed)
    {
        _window.Dispatcher.Invoke(() =>
        {
            if (suppressed && (_window.IsVisible || _nativeHost.IsActive))
            {
                _hiddenByFullscreen = true;
                _window.Hide();
                _nativeHost.SetVisible(false);
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
                _settings.Mode == WidgetMode.Taskbar && !_hiddenByFullscreen)
            {
                if (_nativeHost.IsActive)
                {
                    // 해상도/DPI/작업표시줄 배치 변경 — 재임베드 경로로 재도킹 (필요 시 재생성)
                    _window.Dispatcher.BeginInvoke(() => ApplyMode(WidgetMode.Taskbar), DispatcherPriority.Background);
                }
                else if (_window.IsVisible)
                {
                    _window.Dispatcher.BeginInvoke(Dock, DispatcherPriority.Background);
                }
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
        _burstTimer.Stop();
        _nativeHost.Dispose();
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
