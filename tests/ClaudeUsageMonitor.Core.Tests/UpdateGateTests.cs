using ClaudeUsageMonitor.Core.Updates;
using Xunit;

namespace ClaudeUsageMonitor.Core.Tests;

public class UpdateGateTests
{
    [Theory]
    [InlineData("1.1.2", "2.0.0")]
    [InlineData("1.1.2", "2.0.0-beta.1")]   // 프리릴리스도 메이저 점프
    [InlineData("1.1.2", "2.0.0+build.5")]  // target 쪽 빌드 메타데이터
    [InlineData("1.9.9", "3.0.0")]          // 두 단계 점프
    [InlineData("2.0.0", "3.1.4")]          // 2.x → 3.x도 동일 정책
    [InlineData("1.1.2+464da3c", "2.0.0")]  // current 쪽 빌드 메타데이터
    [InlineData("v1.1.2", "v2.0.0")]        // "v" 접두 허용
    [InlineData("V1.1.2", "V2.0.0")]        // 대문자 "V"도 허용
    [InlineData(" 1.1.2 ", " 2.0.0 ")]      // 앞뒤 공백은 Trim 후 판정
    [InlineData("9.0.0", "10.0.0")]         // 다자리 major (숫자 비교)
    [InlineData("1x", "2.0.0")]             // 관대한 접두 숫자 파싱: current=1 → 점프
    public void Major_Jump_Blocks(string current, string target)
        => Assert.True(UpdateGate.IsMajorJump(current, target));

    [Theory]
    // current 파싱 불가 + target 파싱 가능 → fail-closed (오허용이 오차단보다 훨씬 비쌈)
    [InlineData(null, "2.0.0")]
    [InlineData("", "2.0.0")]
    [InlineData("   ", "2.0.0")]
    [InlineData("abc", "2.0.0")]
    [InlineData("v", "2.0.0")]              // 접두만 있고 숫자 없음
    [InlineData("?", "2.0.0")]              // CurrentVersionText 폴백 "?" 케이스
    [InlineData("99999999999999999999.0.0", "2.0.0")] // current int 오버플로 → 파싱 불가
    public void Unknown_Current_With_Parseable_Target_Blocks(string? current, string target)
        => Assert.True(UpdateGate.IsMajorJump(current, target));

    [Theory]
    [InlineData("1.1.2", "1.2.0")]          // minor 업 — 인앱 허용
    [InlineData("1.9.9", "1.10.0")]         // 숫자 비교 (문자열 비교 아님)
    [InlineData("1.1.2", "1.1.3")]
    [InlineData("2.0.0-beta.1", "2.0.0")]   // 같은 major
    [InlineData("2.1.0", "1.9.9")]          // 다운그레이드는 점프 아님
    [InlineData("1.1.2", "1.1.2")]
    public void Same_Or_Lower_Major_Allows(string current, string target)
        => Assert.False(UpdateGate.IsMajorJump(current, target));

    [Theory]
    // target 파싱 불가 → 점프인지 알 수 없음 — 차단하지 않음 (기존 동작 유지)
    [InlineData("1.1.2", null)]
    [InlineData("1.1.2", "")]
    [InlineData("1.1.2", "next")]
    [InlineData("1.1.2", "v")]
    [InlineData("2147483647.0.0", "2147483648.0.0")] // target int 오버플로 → 파싱 불가 (문서화된 fail-open 에지)
    public void Unparseable_Target_Allows(string current, string? target)
        => Assert.False(UpdateGate.IsMajorJump(current, target));
}
