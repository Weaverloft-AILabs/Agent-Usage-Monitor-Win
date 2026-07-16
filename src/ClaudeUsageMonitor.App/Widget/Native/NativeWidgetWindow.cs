using System.Collections.Concurrent;
using System.Drawing;
using System.Threading;
using ClaudeUsageMonitor.App.Interop;
using static ClaudeUsageMonitor.App.Interop.NativeMethods;

namespace ClaudeUsageMonitor.App.Widget.Native;

/// <summary>
/// 작업표시줄(Shell_TrayWnd)에 SetParent로 임베드되는 순수 Win32 위젯 창.
///
/// - WPF가 아닌 raw 창을 **전용 스레드**에서 돌린다: cross-process SetParent는 입력 큐를
///   Explorer와 결합시키므로, 결합 범위를 이 스레드 하나로 격리해 WPF 디스패처를 보호한다.
/// - 작업표시줄의 자식이므로 셸이 작업표시줄을 시작 메뉴 밴드로 승격해도 함께 올라간다 —
///   topmost 재주장으로는 불가능했던 "시작 메뉴 열림 중 표시"가 구조적으로 성립.
/// - Explorer 재시작 시 부모와 함께 이 창도 파괴된다(WM_DESTROY → 루프 종료). 감시와 재생성은
///   WPF 측 NativeWidgetHost가 담당 — 프로세스 재기동은 필요 없다.
/// - 렌더는 32bpp ARGB GDI+ 비트맵 → UpdateLayeredWindow (자식 layered는 Win8+ 지원).
/// </summary>
public sealed class NativeWidgetWindow : IDisposable
{
    private const string ClassName = "AgentUsageMonitorEmbeddedWidget";
    private const int DragThresholdPx = 4;

    // WndProc 델리게이트는 클래스 수명 동안 GC되면 안 됨 (필수)
    private static readonly WndProc StaticWndProc = DispatchMessageToInstance;
    private static readonly ConcurrentDictionary<IntPtr, NativeWidgetWindow> Instances = new();
    private static int _classRegistered;

    private readonly IntPtr _parentTaskbar;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);

    private const int RenderFailureThreshold = 3;

    private IntPtr _hwnd;
    private uint _nativeThreadId;
    private volatile bool _createFailed;
    private volatile int _consecutiveRenderFailures;

    private readonly object _stateLock = new();
    private NativeWidgetSnapshot? _pendingSnapshot;
    private int _dragMinX;
    private int _dragMaxX = int.MaxValue;

    private Size _lastSize;
    private bool _mouseDown;
    private bool _dragStarted;
    private POINT _dragStartCursor;   // 화면 좌표
    private int _dragStartWindowX;    // 부모 클라이언트 좌표

    /// <summary>렌더 결과 크기가 바뀜(물리 px) — 호스트가 재도킹해야 함. 네이티브 스레드에서 발생.</summary>
    public event Action<int, int>? SizeChanged;

    /// <summary>우클릭 — 호스트가 WPF 컨텍스트 메뉴를 연다. 네이티브 스레드에서 발생.</summary>
    public event Action? RightClicked;

    /// <summary>더블클릭 — 대시보드 열기. 네이티브 스레드에서 발생.</summary>
    public event Action? DoubleClicked;

    /// <summary>드래그 종료 — (부모 클라이언트 X, 놓인 커서 화면 X, 커서 화면 Y). 호스트가 비율 저장·스냅
    /// 또는 다른 모니터 위에서 놓였으면 재임베드 판정. 네이티브 스레드에서 발생.</summary>
    public event Action<int, int, int>? DragCompleted;

    public NativeWidgetWindow(IntPtr parentTaskbar)
    {
        _parentTaskbar = parentTaskbar;
        _thread = new Thread(ThreadMain)
        {
            Name = "NativeWidgetWindow",
            IsBackground = true,
        };
    }

    public IntPtr Hwnd => _hwnd;

    public IntPtr ParentTaskbar => _parentTaskbar;

    public bool IsAlive => _hwnd != IntPtr.Zero && IsWindow(_hwnd);

    /// <summary>렌더(UpdateLayeredWindow)가 연속 실패하지 않았는가 — 자식은 살아있으나 표면 갱신이
    /// 조용히 깨진 상태(셸 구조 변경 등)를 헬스체크가 감지하도록 노출.</summary>
    public bool RenderHealthy => _consecutiveRenderFailures < RenderFailureThreshold;

    /// <summary>창 생성+임베드 완료까지 대기. 실패 시 false (호출자는 오버레이로 폴백).</summary>
    public bool Start(TimeSpan timeout)
    {
        _thread.Start();
        if (!_ready.Wait(timeout))
        {
            NativeWidgetLog.Write("Start timed out waiting for native thread");
            return false;
        }
        return !_createFailed && IsAlive;
    }

    /// <summary>새 표시 스냅샷 게시 — 네이티브 스레드가 재렌더 (어느 스레드에서든 호출 가능).</summary>
    public void UpdateSnapshot(NativeWidgetSnapshot snapshot)
    {
        lock (_stateLock)
        {
            _pendingSnapshot = snapshot;
        }
        if (IsAlive)
        {
            PostMessage(_hwnd, WM_APP_REFRESH, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>부모 클라이언트 좌표로 이동 (크기는 호출자가 보유한 렌더 결과를 전달 — 위치만).</summary>
    public void SetPosition(int x, int y, int width, int height)
    {
        if (IsAlive)
        {
            MoveWindow(_hwnd, x, y, Math.Max(1, width), Math.Max(1, height), true);
        }
    }

    /// <summary>드래그 허용 X 구간 (부모 클라이언트 좌표, 위젯 왼쪽 기준).</summary>
    public void SetDragBounds(int minX, int maxX)
    {
        lock (_stateLock)
        {
            _dragMinX = minX;
            _dragMaxX = Math.Max(minX, maxX);
        }
    }

    public void Show()
    {
        if (IsAlive)
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        }
    }

    public void Hide()
    {
        if (IsAlive)
        {
            ShowWindow(_hwnd, SW_HIDE);
        }
    }

    public void Dispose()
    {
        var joined = true;
        if (_thread.IsAlive)
        {
            if (IsAlive)
            {
                PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            else if (_nativeThreadId != 0)
            {
                // 부모(작업표시줄)와 함께 창이 이미 파괴된 경우 — 루프에 직접 종료 신호
                PostThreadMessage(_nativeThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            joined = _thread.Join(TimeSpan.FromSeconds(2));
            if (!joined && _nativeThreadId != 0)
            {
                // 스레드가 아직 메시지 큐를 못 만든 채(CreateWindowEx 진행 중) 정지했을 수 있음 —
                // 재시도 후에도 안 끝나면 강제하지 않는다 (곧 CreateAndEmbed 완료 후 스스로 종료)
                PostThreadMessage(_nativeThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                joined = _thread.Join(TimeSpan.FromSeconds(1));
            }
        }
        // 스레드가 아직 살아 있으면 ThreadMain의 finally가 _ready.Set()을 호출할 수 있음 —
        // Dispose와 Set이 겹치면 ObjectDisposedException이 백그라운드 스레드에서 터져 프로세스가 죽는다.
        // 조인 확인된 경우에만 해제 (미조인 시 소량 누수 감수).
        if (joined)
        {
            _ready.Dispose();
        }
    }

    // ---- 네이티브 스레드 ----

    private void ThreadMain()
    {
        _nativeThreadId = GetCurrentThreadId();
        var created = false;
        try
        {
            try
            {
                created = CreateAndEmbed();
                if (!created)
                {
                    _createFailed = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                // 백그라운드 스레드의 미처리 예외는 프로세스를 죽인다 — 실패로 기록하고 폴백에 맡김
                NativeWidgetLog.Write($"CreateAndEmbed exception: {ex.GetType().Name} {ex.Message}");
                _createFailed = true;
                return;
            }
            finally
            {
                _ready.Set();
            }

            // 부모가 파괴되면 이 창도 WM_DESTROY를 받아 루프가 스스로 끝난다
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            // 성공/실패 어느 경로든 Instances 등록분을 반드시 제거 (CreateWindowEx 성공 후 등록되므로
            // SetParent 실패로 조기 종료해도 여기서 정리됨 — 정적 딕셔너리 누수 방지)
            if (_hwnd != IntPtr.Zero)
            {
                Instances.TryRemove(_hwnd, out _);
                _hwnd = IntPtr.Zero;
            }
        }
    }

    private bool CreateAndEmbed()
    {
        var hInstance = GetModuleHandle(null);
        if (Interlocked.Exchange(ref _classRegistered, 1) == 0)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_DBLCLKS,
                lpfnWndProc = StaticWndProc,
                hInstance = hInstance,
                hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref wc) == 0)
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                const int ERROR_CLASS_ALREADY_EXISTS = 1410;
                if (err != ERROR_CLASS_ALREADY_EXISTS)
                {
                    // 래치를 해제해 다음 임베드 시도가 재등록할 수 있게 한다 — 그러지 않으면 최초 1회 실패가
                    // 세션 내내 임베드를 영구 차단(등록됐다고 오인해 CreateWindowEx가 계속 실패)한다.
                    Interlocked.Exchange(ref _classRegistered, 0);
                    NativeWidgetLog.Write($"RegisterClassEx failed err={err}");
                    return false;
                }
                // 이미 등록됨(이전 임베드 사이클 잔존) — 정상, 진행
            }
        }

        // layered popup으로 생성 후 자식으로 전환 + 리페어런팅 (CodeZeno 검증 순서)
        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            ClassName, null, WS_POPUP,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            NativeWidgetLog.Write($"CreateWindowEx failed err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return false;
        }
        Instances[_hwnd] = this;

        var style = GetWindowLongPtr(_hwnd, GWL_STYLE).ToInt64();
        style = (style & ~WS_POPUP) | WS_CHILD | WS_CLIPSIBLINGS;
        SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr(style));

        if (SetParent(_hwnd, _parentTaskbar) == IntPtr.Zero)
        {
            NativeWidgetLog.Write($"SetParent failed parent={_parentTaskbar} err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            DestroyWindow(_hwnd);
            return false;
        }

        NativeWidgetLog.Write($"embedded hwnd={_hwnd} parent={_parentTaskbar}");
        // 초기 스냅샷이 이미 게시돼 있으면 즉시 렌더
        RenderPending();
        return true;
    }

    private static IntPtr DispatchMessageToInstance(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        // WndProc 밖으로 관리 예외가 새면 프로세스가 죽는다 — 반드시 여기서 흡수
        try
        {
            if (Instances.TryGetValue(hwnd, out var self))
            {
                return self.HandleMessage(hwnd, msg, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            NativeWidgetLog.Write($"WndProc exception msg=0x{msg:X}: {ex.GetType().Name} {ex.Message}");
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private IntPtr HandleMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_APP_REFRESH:
                RenderPending();
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                _mouseDown = true;
                _dragStarted = false;
                GetCursorPos(out _dragStartCursor);
                _dragStartWindowX = CurrentWindowXInParent();
                SetCapture(hwnd);
                return IntPtr.Zero;

            case WM_MOUSEMOVE when _mouseDown:
                OnDragMove();
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                if (_mouseDown)
                {
                    _mouseDown = false;
                    ReleaseCapture();
                    if (_dragStarted)
                    {
                        _dragStarted = false;
                        // 커서 화면 좌표도 함께 전달 — 임베드 자식은 드래그로 모니터를 못 넘으므로
                        // 호스트가 "놓인 커서 위치"로 다른 모니터 위 드롭을 판정한다
                        GetCursorPos(out var drop);
                        DragCompleted?.Invoke(CurrentWindowXInParent(), drop.X, drop.Y);
                    }
                }
                return IntPtr.Zero;

            case WM_LBUTTONDBLCLK:
                _mouseDown = false;
                _dragStarted = false;
                ReleaseCapture();
                DoubleClicked?.Invoke();
                return IntPtr.Zero;

            case WM_RBUTTONUP:
                RightClicked?.Invoke();
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private int CurrentWindowXInParent()
    {
        GetWindowRect(_hwnd, out var rect);
        var pt = new POINT { X = rect.Left, Y = rect.Top };
        MapWindowPoints(IntPtr.Zero, _parentTaskbar, ref pt, 1);
        return pt.X;
    }

    private void OnDragMove()
    {
        GetCursorPos(out var cursor);
        var dx = cursor.X - _dragStartCursor.X;
        var dy = cursor.Y - _dragStartCursor.Y;
        if (!_dragStarted)
        {
            if (Math.Abs(dx) < DragThresholdPx && Math.Abs(dy) < DragThresholdPx)
            {
                return; // 더블클릭 여지를 위해 임계값 전까지는 드래그로 보지 않음
            }
            _dragStarted = true;
        }

        int minX, maxX;
        lock (_stateLock)
        {
            minX = _dragMinX;
            maxX = _dragMaxX;
        }

        GetWindowRect(_hwnd, out var rect);
        var current = new POINT { X = rect.Left, Y = rect.Top };
        MapWindowPoints(IntPtr.Zero, _parentTaskbar, ref current, 1);
        var newX = Math.Clamp(_dragStartWindowX + dx, minX, maxX);
        MoveWindow(_hwnd, newX, current.Y, rect.Right - rect.Left, rect.Bottom - rect.Top, true);
    }

    private void RenderPending()
    {
        NativeWidgetSnapshot? snapshot;
        lock (_stateLock)
        {
            snapshot = _pendingSnapshot;
        }
        if (snapshot is null || !IsAlive)
        {
            return;
        }

        var dpi = GetDpiForWindow(_hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;

        using var bitmap = NativeWidgetRenderer.Render(snapshot, scale);
        ApplyLayeredBitmap(bitmap);

        var size = new Size(bitmap.Width, bitmap.Height);
        if (size != _lastSize)
        {
            _lastSize = size;
            SizeChanged?.Invoke(size.Width, size.Height);
        }
    }

    /// <summary>per-pixel alpha 비트맵을 UpdateLayeredWindow로 창 표면에 적용 (표준 GetHbitmap 패턴).</summary>
    private void ApplyLayeredBitmap(Bitmap bitmap)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
            var source = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };
            if (UpdateLayeredWindow(_hwnd, screenDc, IntPtr.Zero, ref size, memDc, ref source, 0, ref blend, ULW_ALPHA))
            {
                _consecutiveRenderFailures = 0;
            }
            else
            {
                // 반환값 무시 시 지속 실패해도 '건강'으로 보여 빈/스테일 위젯이 방치됨 —
                // 연속 실패를 세어 RenderHealthy로 노출하면 호스트 헬스체크가 재임베드/폴백을 유도.
                _consecutiveRenderFailures++;
                NativeWidgetLog.Write($"UpdateLayeredWindow failed err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()} streak={_consecutiveRenderFailures}");
            }
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero)
            {
                SelectObject(memDc, oldBitmap);
            }
            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
