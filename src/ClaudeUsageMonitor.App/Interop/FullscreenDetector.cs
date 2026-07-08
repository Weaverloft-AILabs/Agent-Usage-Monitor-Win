using System.Windows.Threading;
using static ClaudeUsageMonitor.App.Interop.NativeMethods;

namespace ClaudeUsageMonitor.App.Interop;

/// <summary>
/// 전체화면 앱(게임/프레젠테이션) 감지 — 위젯 양보용.
/// SHQueryUserNotificationState 5초 폴링.
/// (설계상 ABN_FULLSCREENAPP appbar 콜백 병행이 이상적이나, 크래시 시 appbar 등록 누수가
///  셸을 교란하는 리스크가 있어 폴링 단독으로 구현 — 감지 지연 최대 5초 허용.)
/// </summary>
public sealed class FullscreenDetector : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _lastFullscreen;

    public event Action<bool>? FullscreenChanged;

    public FullscreenDetector()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    private void Poll()
    {
        if (SHQueryUserNotificationState(out var state) != 0)
        {
            return; // 실패 시 상태 유지
        }

        var fullscreen = state is
            QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN or
            QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY or
            QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;

        if (fullscreen != _lastFullscreen)
        {
            _lastFullscreen = fullscreen;
            FullscreenChanged?.Invoke(fullscreen);
        }
    }

    public void Dispose() => _timer.Stop();
}
