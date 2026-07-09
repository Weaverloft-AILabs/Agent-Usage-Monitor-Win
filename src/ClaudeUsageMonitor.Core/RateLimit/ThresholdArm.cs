namespace ClaudeUsageMonitor.Core.RateLimit;

/// <summary>
/// 임계값 경고 1회 발사 로직: 같은 리셋 윈도우(resets_at 동일) 내에서는 재발사하지 않고,
/// 윈도우가 바뀌면(리셋 경과) 재무장한다. 인메모리 상태이므로 앱 재시작 시에도 재무장되며,
/// 계정 변경 등 외부 사유는 Rearm()으로 명시 재무장한다.
/// </summary>
public sealed class ThresholdArm
{
    private DateTimeOffset? _firedForReset;
    private bool _firedForNullReset;

    /// <summary>발사 플래그를 초기화해 같은 윈도우에서도 다시 알릴 수 있게 한다 (계정 변경 등).</summary>
    public void Rearm()
    {
        _firedForReset = null;
        _firedForNullReset = false;
    }

    public bool ShouldFire(double currentPct, double thresholdPct, DateTimeOffset? resetsAt)
    {
        if (currentPct < thresholdPct)
        {
            return false;
        }

        if (resetsAt is null)
        {
            if (_firedForNullReset)
            {
                return false;
            }
            _firedForNullReset = true;
            return true;
        }

        if (_firedForReset == resetsAt)
        {
            return false;
        }

        _firedForReset = resetsAt;
        _firedForNullReset = false;
        return true;
    }
}
