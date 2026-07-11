using ClaudeUsageMonitor.Core.Updates;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public class UpdatePendingMarkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Write_Read_Delete_Roundtrip()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var marker = new UpdatePendingMarker(dir);
            Assert.Null(marker.TryRead());

            Assert.True(marker.Write("2.2.1", Now));
            var read = marker.TryRead();
            Assert.NotNull(read);
            Assert.Equal("2.2.1", read.TargetVersion);
            Assert.Equal(Now, read.WrittenUtc);

            marker.Delete();
            Assert.Null(marker.TryRead());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Assess_NoMarker_Is_None()
        => Assert.Equal(PendingUpdateAssessment.None, UpdatePendingMarker.Assess(null, "2.2.1", Now));

    [Fact]
    public void Assess_Applied_Is_Completed_Regardless_Of_Age()
    {
        // 재시작 연속성의 정상 경로: 초 단위 나이 + 신버전
        var fresh = new PendingUpdateMarker("2.2.1", Now.AddSeconds(-5));
        Assert.Equal(PendingUpdateAssessment.Completed, UpdatePendingMarker.Assess(fresh, "2.2.1", Now));

        // --update-done이 유실됐고 한참 뒤 수동 실행된 경우도 완료로 판정
        var old = new PendingUpdateMarker("2.2.1", Now.AddMinutes(-10));
        Assert.Equal(PendingUpdateAssessment.Completed, UpdatePendingMarker.Assess(old, "2.2.2", Now));
    }

    [Fact]
    public void Assess_NotApplied_Within_Grace_Is_InProgress()
    {
        // 무창 구간(적용 중) 사용자 수동 실행 — "완료되지 않았습니다" 오경보를 내면 안 됨 (수정 필수 ②)
        var marker = new PendingUpdateMarker("2.2.1", Now.AddMinutes(-1));
        Assert.Equal(PendingUpdateAssessment.InProgress, UpdatePendingMarker.Assess(marker, "2.2.0", Now));
    }

    [Fact]
    public void Assess_NotApplied_After_Grace_Is_Failed()
    {
        var marker = new PendingUpdateMarker("2.2.1", Now.AddMinutes(-5));
        Assert.Equal(PendingUpdateAssessment.Failed, UpdatePendingMarker.Assess(marker, "2.2.0", Now));
    }

    [Fact]
    public void Assess_NotApplied_Beyond_Failure_Window_Is_Stale()
    {
        var marker = new PendingUpdateMarker("2.2.1", Now.AddMinutes(-31));
        Assert.Equal(PendingUpdateAssessment.Stale, UpdatePendingMarker.Assess(marker, "2.2.0", Now));
    }

    [Theory]
    [InlineData("2.2.1", "2.2.1", true)]   // 정확 일치
    [InlineData("2.2.2", "2.2.1", true)]   // 상위 patch
    [InlineData("2.3.0", "2.2.9", true)]   // 상위 minor
    [InlineData("2.2.0", "2.2.1", false)]  // 미달
    [InlineData("v2.2.1", "2.2.1", true)]  // v 접두 허용
    [InlineData("2.2.1+abc123", "2.2.1", true)] // 빌드메타 절단
    [InlineData("2.2.1", "v2.2.1", true)]
    public void IsApplied_Compares_Numeric_Semver(string current, string target, bool expected)
        => Assert.Equal(expected, UpdatePendingMarker.IsApplied(current, target));

    [Fact]
    public void IsApplied_Unparseable_Falls_Back_To_String_Equality()
    {
        Assert.True(UpdatePendingMarker.IsApplied("weird", "WEIRD"));
        Assert.False(UpdatePendingMarker.IsApplied("weird", "other"));
    }
}
