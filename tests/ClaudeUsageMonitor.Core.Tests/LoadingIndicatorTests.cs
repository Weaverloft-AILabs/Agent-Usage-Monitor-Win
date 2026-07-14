using ClaudeUsageMonitor.Core.Models;
using ClaudeUsageMonitor.Core.RateLimit;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public class LoadingIndicatorTests
{
    [Fact]
    public void Loading_WhenNoDataYetAndSnapshotPending()
    {
        // 시작/업데이트 직후 — 아직 스냅샷 없음, 전송 오류도 아님 → 로딩
        Assert.True(LoadingIndicator.IsLoading(hadDataBefore: false, snapshotPresent: false, RateLimitStatus.Stale));
    }

    [Fact]
    public void NotLoading_WhenSnapshotArrives()
    {
        Assert.False(LoadingIndicator.IsLoading(hadDataBefore: false, snapshotPresent: true, RateLimitStatus.Ok));
    }

    [Fact]
    public void NotLoading_AfterFirstDataEvenIfLaterNullTransient()
    {
        // 한 번 데이터를 받은 뒤엔 다음 폴링이 잠깐 비어도 로딩 아님(마지막 값 유지)
        Assert.False(LoadingIndicator.IsLoading(hadDataBefore: true, snapshotPresent: false, RateLimitStatus.Stale));
    }

    [Fact]
    public void NotLoading_WhenNoCredentials()
    {
        // CLI 미감지 = 확정 상태(별도 경고 UI) — 로딩 아님
        Assert.False(LoadingIndicator.IsLoading(hadDataBefore: false, snapshotPresent: false, RateLimitStatus.NoCredentials));
    }

    [Fact]
    public void NotLoading_WhenAuthRequired()
    {
        // 재로그인 필요 = 확정 상태 — 무한 로딩 방지
        Assert.False(LoadingIndicator.IsLoading(hadDataBefore: false, snapshotPresent: false, RateLimitStatus.AuthRequired));
    }
}
