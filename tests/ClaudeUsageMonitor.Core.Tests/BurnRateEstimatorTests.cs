using ClaudeUsageMonitor.Core.RateLimit;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public class BurnRateEstimatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RisingUsage_ProducesEstimate()
    {
        var estimator = new BurnRateEstimator();
        estimator.Add(T0, 50);
        estimator.Add(T0.AddMinutes(6), 53); // 0.5%/분 → 잔여 47% ≈ 94분

        var eta = estimator.EstimateTimeToExhaustion(T0.AddMinutes(6));

        Assert.NotNull(eta);
        Assert.InRange(eta.Value.TotalMinutes, 93, 95);
    }

    [Fact]
    public void FlatUsage_ReturnsNull()
    {
        var estimator = new BurnRateEstimator();
        estimator.Add(T0, 50);
        estimator.Add(T0.AddMinutes(10), 50);

        Assert.Null(estimator.EstimateTimeToExhaustion(T0.AddMinutes(10)));
    }

    [Fact]
    public void SingleSample_ReturnsNull()
    {
        var estimator = new BurnRateEstimator();
        estimator.Add(T0, 50);

        Assert.Null(estimator.EstimateTimeToExhaustion(T0));
    }

    [Fact]
    public void ShortObservationSpan_ReturnsNull()
    {
        var estimator = new BurnRateEstimator();
        estimator.Add(T0, 50);
        estimator.Add(T0.AddMinutes(2), 51);

        Assert.Null(estimator.EstimateTimeToExhaustion(T0.AddMinutes(2)));
    }

    [Fact]
    public void ResetDrop_DiscardsOldHistory()
    {
        var estimator = new BurnRateEstimator();
        estimator.Add(T0, 90);
        estimator.Add(T0.AddMinutes(6), 95);
        estimator.Add(T0.AddMinutes(9), 2); // 리셋 발생

        // 리셋 직후 표본 1개뿐 → null (이전 이력 미사용)
        Assert.Null(estimator.EstimateTimeToExhaustion(T0.AddMinutes(9)));

        estimator.Add(T0.AddMinutes(15), 5); // 리셋 후 0.5%/분
        var eta = estimator.EstimateTimeToExhaustion(T0.AddMinutes(15));
        Assert.NotNull(eta);
        Assert.InRange(eta.Value.TotalMinutes, 185, 195); // 잔여 95% / 0.5 = 190분
    }

    [Fact]
    public void ElapsedTimeSinceLastSample_IsSubtracted()
    {
        var estimator = new BurnRateEstimator();
        estimator.Add(T0, 50);
        estimator.Add(T0.AddMinutes(6), 53);

        var atLast = estimator.EstimateTimeToExhaustion(T0.AddMinutes(6))!.Value;
        var later = estimator.EstimateTimeToExhaustion(T0.AddMinutes(16))!.Value;

        Assert.InRange(atLast.TotalMinutes - later.TotalMinutes, 9.5, 10.5);
    }

    [Fact]
    public void AlreadyExhausted_ReturnsZero()
    {
        var estimator = new BurnRateEstimator();
        estimator.Add(T0, 95);
        estimator.Add(T0.AddMinutes(6), 100);

        Assert.Equal(TimeSpan.Zero, estimator.EstimateTimeToExhaustion(T0.AddMinutes(6)));
    }

    [Fact]
    public void Clear_DiscardsSamples_ForAccountSwitch()
    {
        var estimator = new BurnRateEstimator();
        estimator.Add(T0, 10);
        estimator.Add(T0.AddMinutes(10), 20);
        Assert.NotNull(estimator.EstimateTimeToExhaustion(T0.AddMinutes(10))); // 예측 산출됨

        estimator.Clear(); // 계정 전환 — 이전 표본 폐기

        // 새 계정 첫 표본 1개뿐 → 표본 부족으로 예측 없음(Clear 안 했다면 이전 표본과 섞여 예측이 나옴)
        estimator.Add(T0.AddMinutes(11), 60);
        Assert.Null(estimator.EstimateTimeToExhaustion(T0.AddMinutes(11)));
    }
}
