namespace ClaudeUsageMonitor.Installer.Install;

/// <summary>명시적 <c>--setup</c> 인자로 지정된 Setup.exe만 위치확인한다.
/// (구 "인스톨러와 같은 폴더의 AgentUsageMonitor-win-Setup.exe 자동 사용"은 제거 — 사용자 폴더에
/// 남은 <b>스테일 Setup.exe가 버전검증 없이 실행돼 엉뚱한 구버전을 설치</b>하는 히잡이었다.
/// 오프라인 설치는 이제 인스톨러에 임베드된 Setup(<see cref="EmbeddedSetup"/>)이 담당한다.)</summary>
public static class SetupLocator
{
    /// <summary>vpk pack이 생성하는 Setup 자산 이름 (릴리스 자산·임베드 소스와 동일).</summary>
    public const string SetupFileName = "AgentUsageMonitor-win-Setup.exe";

    /// <summary>명시적 <c>--setup</c> 인자가 있고 실제 존재하면 그 경로, 아니면 null
    /// (= 임베드 추출 / GitHub 다운로드 폴백으로 진행).</summary>
    public static string? Locate(string? cliArgPath, Func<string, bool> fileExists)
        => !string.IsNullOrWhiteSpace(cliArgPath) && fileExists(cliArgPath) ? cliArgPath : null;
}
