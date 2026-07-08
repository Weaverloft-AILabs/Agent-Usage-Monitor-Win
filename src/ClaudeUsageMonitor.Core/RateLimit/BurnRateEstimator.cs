namespace ClaudeUsageMonitor.Core.RateLimit;

/// <summary>
/// 최근 5시간 사용률 스냅샷으로 소진 속도를 추정해 한도(100%) 도달까지 남은 시간을 예측.
/// 폴링 스레드가 Add, UI 스레드가 Estimate를 호출하므로 내부 잠금으로 보호한다.
/// </summary>
public sealed class BurnRateEstimator
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan MinSpan = TimeSpan.FromMinutes(4);

    /// <summary>이보다 느린 속도(%/분)는 "사실상 소진 없음"으로 간주.</summary>
    private const double MinRatePctPerMinute = 0.02;

    /// <summary>이만큼 수치가 떨어지면 윈도우 리셋으로 간주하고 이력을 버린다.</summary>
    private const double ResetDropThresholdPct = 0.5;

    private readonly object _lock = new();
    private readonly List<(DateTimeOffset At, double Pct)> _samples = [];

    public void Add(DateTimeOffset at, double pct)
    {
        lock (_lock)
        {
            if (_samples.Count > 0 && pct < _samples[^1].Pct - ResetDropThresholdPct)
            {
                _samples.Clear(); // 리셋 감지 — 이전 윈도우 이력은 속도 계산을 오염시킴
            }
            if (_samples.Count > 0 && at <= _samples[^1].At)
            {
                return;
            }
            _samples.Add((at, pct));
            _samples.RemoveAll(s => at - s.At > Window);
        }
    }

    /// <summary>
    /// 현재 속도 유지 시 100% 도달까지 남은 시간. 표본 부족, 짧은 관측 구간,
    /// 또는 속도가 미미하면 null (표시하지 않음).
    /// </summary>
    public TimeSpan? EstimateTimeToExhaustion(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_samples.Count < 2)
            {
                return null;
            }

            var first = _samples[0];
            var last = _samples[^1];
            var span = last.At - first.At;
            if (span < MinSpan)
            {
                return null;
            }

            var ratePerMinute = (last.Pct - first.Pct) / span.TotalMinutes;
            if (ratePerMinute < MinRatePctPerMinute)
            {
                return null;
            }

            var minutesLeft = (100 - last.Pct) / ratePerMinute - (now - last.At).TotalMinutes;
            return minutesLeft <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(minutesLeft);
        }
    }
}
