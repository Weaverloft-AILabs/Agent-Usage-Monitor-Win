namespace ClaudeUsageMonitor.Core.RateLimit;

/// <summary>
/// 임계값 경고 1회 발사 로직: 같은 리셋 윈도우(resets_at 동일) 내에서는 재발사하지 않고,
/// 윈도우가 바뀌면(리셋 경과) 재무장한다. 인메모리 상태이므로 앱 재시작 시에도 재무장되며,
/// 계정 변경 등 외부 사유는 Rearm()으로 명시 재무장한다.
/// </summary>
public sealed class ThresholdArm
{
    private readonly object _lock = new();   // 폴링 스레드(ShouldFire)와 계정감시 스레드(Rearm)가 공유
    private DateTimeOffset? _firedForReset;
    private bool _firedForNullReset;

    /// <summary>발사 플래그를 초기화해 같은 윈도우에서도 다시 알릴 수 있게 한다 (계정 변경 등).</summary>
    public void Rearm()
    {
        lock (_lock)
        {
            _firedForReset = null;
            _firedForNullReset = false;
        }
    }

    public bool ShouldFire(double currentPct, double thresholdPct, DateTimeOffset? resetsAt)
    {
        lock (_lock)
        {
        if (currentPct < thresholdPct)
        {
            return false;
        }

        // usage API는 매 응답마다 resets_at의 초 미만 성분을 다르게 반환한다(실측 2026-07-13:
        // 10:59:59.542213 → .918093). 원값으로 비교하면 폴링마다 윈도우가 "바뀐 것"으로 오인돼
        // 임계값 초과 상태에서 알림이 폴링 주기마다 반복 발사된다. 윈도우 경계는 시간 단위라
        // 분으로 정규화하면 같은 윈도우는 동일 키, 다른 윈도우(5시간 간격)는 여전히 구분된다.
        // resets_at가 잠깐 누락(null)되면 직전에 발사한 실제 윈도우로 대체 — 같은 초과 에피소드가
        // 실제윈도우→null 전환에서 중복 발사되던 것을 억제(_firedForReset이 있으면 그 윈도우로 간주).
        var window = NormalizeWindow(resetsAt) ?? _firedForReset;

        if (window is null)
        {
            if (_firedForNullReset)
            {
                return false;
            }
            _firedForNullReset = true;
            return true;
        }

        if (_firedForReset == window)
        {
            return false;
        }

        _firedForReset = window;
        _firedForNullReset = false;
        return true;
        }
    }

    /// <summary>resets_at를 가장 가까운 분으로 정규화(서버가 흔드는 초 미만 지터 제거). null은 그대로.</summary>
    private static DateTimeOffset? NormalizeWindow(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not { } v)
        {
            return null;
        }

        const long minute = TimeSpan.TicksPerMinute;
        long rem = v.UtcTicks % minute;
        long rounded = v.UtcTicks - rem + (rem >= minute / 2 ? minute : 0);
        return new DateTimeOffset(rounded, TimeSpan.Zero);
    }
}
