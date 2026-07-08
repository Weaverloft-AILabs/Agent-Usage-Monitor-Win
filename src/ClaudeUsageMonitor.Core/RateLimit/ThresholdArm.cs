namespace ClaudeUsageMonitor.Core.RateLimit;

/// <summary>
/// 임계값 경고 1회 발사 로직: 같은 리셋 윈도우(resets_at 동일) 내에서는 재발사하지 않고,
/// 윈도우가 바뀌면(리셋 경과) 재무장한다.
/// </summary>
public sealed class ThresholdArm
{
    private DateTimeOffset? _firedForReset;
    private bool _firedForNullReset;

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
