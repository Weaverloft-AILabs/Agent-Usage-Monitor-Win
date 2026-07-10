using System.ComponentModel;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeUsageMonitor.App.Interop;
using ClaudeUsageMonitor.App.Theming;
using ClaudeUsageMonitor.App.ViewModels;
using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.Settings;
using DColor = System.Drawing.Color;

namespace ClaudeUsageMonitor.App.Widget.Native;

/// <summary>
/// 임베드 위젯의 WPF 측 브리지.
/// - WidgetViewModel의 표시 상태를 스냅샷으로 변환해 네이티브 창에 게시 (테마 팔레트 포함)
/// - 부모 클라이언트 좌표계에서 도킹 위치/드래그 구간 계산 (오버레이 Dock()과 동일 기하,
///   비율 저장 방식도 동일 — 두 방식 간 위치가 호환됨)
/// - 우클릭 → 숨겨진 WPF 위젯 창의 공유 컨텍스트 메뉴(트레이와 동일)를 커서 위치에 표시,
///   더블클릭 → 대시보드
/// - 헬스 판정(부모 생존 + 부모-자식 관계 유지)은 노출만 — 재시도/폴백 정책은 WidgetController가 소유
/// </summary>
public sealed class NativeWidgetHost : IDisposable
{
    private const double DockMarginRight = 12;
    private const double DockGap = 6;

    private readonly WidgetWindow _widget;
    private readonly WidgetViewModel _viewModel;
    private readonly MonitorSettings _settings;
    private readonly SettingsStore _settingsStore;

    private NativeWidgetWindow? _native;
    private string _parentDevice = "";
    private bool _parentIsPrimary;

    private readonly object _sizeLock = new();
    private int _width;
    private int _height;
    private int _pushQueued;
    private bool _suppressed;

    /// <summary>더블클릭 — 대시보드 열기 (WPF 디스패처에서 발생).</summary>
    public event Action? DashboardRequested;

    public NativeWidgetHost(
        WidgetWindow widget, WidgetViewModel viewModel,
        MonitorSettings settings, SettingsStore settingsStore)
    {
        _widget = widget;
        _viewModel = viewModel;
        _settings = settings;
        _settingsStore = settingsStore;

        _viewModel.PropertyChanged += OnViewModelChanged;
        ThemeManager.EffectiveThemeChanged += OnThemeChanged;
    }

    public bool IsActive => _native is { IsAlive: true };

    /// <summary>부모 작업표시줄이 살아 있고 부모-자식 관계가 유지 중인가.</summary>
    public bool IsHealthy =>
        _native is { } native && native.IsAlive &&
        NativeMethods.IsWindow(native.ParentTaskbar) &&
        NativeMethods.GetParent(native.Hwnd) == native.ParentTaskbar;

    /// <summary>
    /// 임베드 활성화 (이미 건강하면 재도킹만). 실패 시 false — 호출자는 오버레이로 폴백.
    /// 세로 작업표시줄은 임베드 레이아웃 미지원이라 오버레이에 위임.
    /// </summary>
    public bool TryActivate()
    {
        _suppressed = false; // 활성화 요청 = 표시 의도 (전체화면 억제 해제 후 재도킹 시 Show 재개)
        var taskbars = TaskbarLocator.GetAllTaskbars();
        if (taskbars.Count == 0)
        {
            NativeWidgetLog.Write("TryActivate: no taskbars found");
            return false;
        }
        var target = SelectTargetTaskbar(taskbars);
        if (target.Edge is TaskbarEdge.Left or TaskbarEdge.Right)
        {
            NativeWidgetLog.Write($"TryActivate: vertical taskbar ({target.Edge}) — overlay fallback");
            return false;
        }

        if (_native is { IsAlive: true } current && current.ParentTaskbar == target.Hwnd && IsHealthy)
        {
            PushSnapshot();
            Dock();
            return true;
        }

        Deactivate();

        // 메뉴 포그라운드 트릭(트레이 패턴)에 필요 — 숨김 상태라도 HWND는 존재해야 함
        new WindowInteropHelper(_widget).EnsureHandle();

        var native = new NativeWidgetWindow(target.Hwnd);
        native.SizeChanged += OnNativeSizeChanged;
        native.RightClicked += OnRightClicked;
        native.DoubleClicked += OnDoubleClicked;
        native.DragCompleted += OnDragCompleted;

        _native = native;
        _parentDevice = target.MonitorDevice;
        _parentIsPrimary = target.IsPrimary;
        PushSnapshot(); // Start 전에 게시 — 창 생성 직후 첫 렌더에 사용됨

        if (!native.Start(TimeSpan.FromSeconds(3)))
        {
            NativeWidgetLog.Write("TryActivate: native window start/embed failed");
            Deactivate();
            return false;
        }

        Dock();
        return true;
    }

    public void Deactivate()
    {
        if (_native is { } native)
        {
            _native = null;
            // 파괴 전에 핸들러 해제 — 종료 중 도착하는 잔여 SizeChanged가 _width/_height를 되살리거나
            // 폐기된 인스턴스 기준으로 Dock을 유발하지 않도록
            native.SizeChanged -= OnNativeSizeChanged;
            native.RightClicked -= OnRightClicked;
            native.DoubleClicked -= OnDoubleClicked;
            native.DragCompleted -= OnDragCompleted;
            native.Dispose();
        }
        lock (_sizeLock)
        {
            _width = 0;
            _height = 0;
        }
    }

    /// <summary>전체화면 양보 등 임시 표시 제어 (임베드 상태는 유지).</summary>
    public void SetVisible(bool visible)
    {
        // 억제 상태를 래치 — 이걸 두지 않으면 스냅샷 리렌더(SizeChanged→Dock)나 셸 브로드캐스트가
        // 억제 중에 위젯을 다시 Show()해 전체화면 위로 튀어나온다 (전체화면 양보 회귀).
        _suppressed = !visible;
        if (_native is { IsAlive: true } native)
        {
            if (visible)
            {
                native.Show();
            }
            else
            {
                native.Hide();
            }
        }
    }

    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        ThemeManager.EffectiveThemeChanged -= OnThemeChanged;
        Deactivate();
    }

    // ---- 스냅샷 ----

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => QueuePush();

    private void OnThemeChanged() => QueuePush();

    /// <summary>
    /// 1초 틱마다 3~4개 프로퍼티가 바뀌므로 디스패처 단위로 코얼레싱.
    /// PropertyChanged가 폴러 스레드풀 스레드에서도 올 수 있어(RateLimitUpdated) 플래그는 Interlocked —
    /// plain bool의 check-then-act는 업데이트를 잃을 수 있다.
    /// </summary>
    private void QueuePush()
    {
        if (!IsActive)
        {
            return;
        }
        if (Interlocked.Exchange(ref _pushQueued, 1) == 1)
        {
            return; // 이미 예약됨
        }
        _widget.Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _pushQueued, 0);
            PushSnapshot();
        }, DispatcherPriority.Background);
    }

    private void PushSnapshot()
    {
        if (_native is not { } native)
        {
            return;
        }
        var palette = ThemeManager.IsDarkEffective ? NativeWidgetPalette.Dark : NativeWidgetPalette.Light;
        native.UpdateSnapshot(new NativeWidgetSnapshot(
            CliMissing: _viewModel.CliMissing,
            FiveHourPct: _viewModel.FiveHourPct,
            FiveHourResetText: _viewModel.FiveHourResetText,
            FiveHourBar: ToDrawingColor(_viewModel.FiveHourBrush),
            SevenDayPct: _viewModel.SevenDayPct,
            SevenDayResetText: _viewModel.SevenDayResetText,
            SevenDayBar: ToDrawingColor(_viewModel.SevenDayBrush),
            ExhaustionText: _viewModel.ExhaustionText,
            UpdateAvailable: _viewModel.UpdateAvailable,
            Palette: palette));
    }

    private static DColor ToDrawingColor(Brush brush) =>
        brush is SolidColorBrush solid
            ? DColor.FromArgb(solid.Color.A, solid.Color.R, solid.Color.G, solid.Color.B)
            : DColor.FromArgb(0xFF, 0x8C, 0x8C, 0x8C);

    // ---- 도킹 (부모 클라이언트 좌표) ----

    /// <summary>현재 렌더 크기 기준으로 트레이 왼쪽 빈 공간에 배치 + 드래그 구간 갱신 + 표시.</summary>
    public void Dock()
    {
        if (_native is not { IsAlive: true } native || !NativeMethods.IsWindow(native.ParentTaskbar))
        {
            return;
        }

        int width, height;
        lock (_sizeLock)
        {
            width = _width;
            height = _height;
        }
        if (width <= 0 || height <= 0)
        {
            return; // 첫 렌더 전 — SizeChanged에서 다시 도킹됨
        }

        var (spanStart, maxX, clientHeight) = ComputeSpan(native, width);
        var x = _settings.TaskbarOffsetRatio is { } ratio
            ? spanStart + (int)Math.Round(Math.Clamp(ratio, 0, 1) * (maxX - spanStart))
            : maxX; // 기본: 트레이 왼쪽 (우측 정렬)
        var y = Math.Max(0, (clientHeight - height) / 2);

        native.SetDragBounds(spanStart, maxX);
        native.SetPosition(x, y, width, height);
        if (!_suppressed)
        {
            native.Show(); // 전체화면 억제 중에는 재표시 금지 (SizeChanged/셸 브로드캐스트발 Dock 대비)
        }
    }

    /// <summary>위젯 왼쪽 X가 놓일 수 있는 구간 (부모 클라이언트 px) — 오버레이 HorizontalSpan과 동일 기하.</summary>
    private (int SpanStart, int MaxX, int ClientHeight) ComputeSpan(NativeWidgetWindow native, int width)
    {
        NativeMethods.GetClientRect(native.ParentTaskbar, out var client);
        var dpi = NativeMethods.GetDpiForWindow(native.ParentTaskbar);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var gap = (int)Math.Round(DockGap * scale);
        var margin = (int)Math.Round(DockMarginRight * scale);

        var spanStart = gap;
        var spanEnd = client.Right - margin;
        if (_parentIsPrimary && TaskbarLocator.GetTrayNotifyRect() is { } tray)
        {
            var trayLeft = new NativeMethods.POINT { X = tray.Left, Y = tray.Top };
            NativeMethods.MapWindowPoints(IntPtr.Zero, native.ParentTaskbar, ref trayLeft, 1);
            if (trayLeft.X > 0 && trayLeft.X < client.Right)
            {
                spanEnd = trayLeft.X - gap;
            }
        }

        var maxX = Math.Max(spanStart, spanEnd - width);
        return (spanStart, maxX, client.Bottom);
    }

    // ---- 네이티브 이벤트 (네이티브 스레드 → 디스패처 마샬) ----

    private void OnNativeSizeChanged(int width, int height)
    {
        lock (_sizeLock)
        {
            _width = width;
            _height = height;
        }
        _widget.Dispatcher.BeginInvoke(Dock, DispatcherPriority.Background);
    }

    private void OnRightClicked() => _widget.Dispatcher.BeginInvoke(() =>
    {
        if (_widget.ContextMenu is { } menu)
        {
            // NOACTIVATE/숨김 창의 메뉴 light-dismiss를 위한 표준 트레이 패턴 —
            // 이 스레드를 포그라운드로 만들어야 바깥 클릭 시 메뉴가 닫힌다
            NativeMethods.SetForegroundWindow(new WindowInteropHelper(_widget).EnsureHandle());
            menu.IsOpen = true; // ContextMenu 기본 Placement = MousePoint
        }
    });

    private void OnDoubleClicked() =>
        _widget.Dispatcher.BeginInvoke(() => DashboardRequested?.Invoke());

    private void OnDragCompleted(int clientX) => _widget.Dispatcher.BeginInvoke(() =>
    {
        if (_native is not { IsAlive: true } native)
        {
            return;
        }
        int width;
        lock (_sizeLock)
        {
            width = _width;
        }
        var (spanStart, maxX, _) = ComputeSpan(native, width);
        var ratio = maxX == spanStart
            ? 0
            : Math.Clamp((double)(clientX - spanStart) / (maxX - spanStart), 0, 1);

        _settings.TaskbarMonitorDevice = _parentDevice;
        _settings.TaskbarOffsetRatio = ratio;
        _settingsStore.Save(_settings);
        Dock();
    });

    /// <summary>저장된 모니터의 작업표시줄 우선, 없으면 주 작업표시줄 (오버레이와 동일 정책).</summary>
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
}
