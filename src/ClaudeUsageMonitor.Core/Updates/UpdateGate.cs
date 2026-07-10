namespace ClaudeUsageMonitor.Core.Updates;

/// <summary>
/// 메이저 버전 점프(예: 1.x → 2.x) 인앱 자동 업데이트 차단 판정.
/// 2.0.0부터는 설치 방식이 달라 수동 설치가 필요하므로(2026-07-10 정책),
/// 인앱 설치 대신 GitHub 릴리스 페이지 링크로 안내한다.
/// </summary>
public static class UpdateGate
{
    /// <summary>target이 상위 major면 true — 인앱 설치를 차단해야 한다.
    /// 비대칭 기본값(비용 비대칭):
    /// · target 파싱 불가 → false (점프인지 알 수 없음 — 기존 인앱 동작 유지)
    /// · current 파싱 불가 + target 파싱 가능 → true (fail-closed: 오차단 비용은 수동 링크
    ///   클릭 한 번, 오허용 비용은 설치 방식이 다른 메이저 버전의 인앱 설치 파손)</summary>
    public static bool IsMajorJump(string? currentVersion, string? targetVersion)
    {
        if (!TryParseMajor(targetVersion, out var target))
        {
            return false;
        }

        return !TryParseMajor(currentVersion, out var current) || target > current;
    }

    /// <summary>semver 문자열에서 major만 관대하게 추출 ("v" 접두, "-beta.1", "+메타데이터" 허용).</summary>
    private static bool TryParseMajor(string? version, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var s = version.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var digits = 0;
        while (digits < s.Length && char.IsAsciiDigit(s[digits]))
        {
            digits++;
        }

        return digits > 0 && int.TryParse(s[..digits], out major);
    }
}
