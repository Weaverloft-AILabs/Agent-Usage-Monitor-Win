using System.Diagnostics;
using System.IO;

namespace ClaudeUsageMonitor.Installer.Install;

/// <summary>설치 창이 취할 동작.</summary>
public enum InstallMode
{
    /// <summary>미설치 → 신규 설치.</summary>
    NotInstalled,

    /// <summary>구버전 설치됨 → 업데이트 (구버전 설치 시 자동 전환되는 경로).</summary>
    Update,

    /// <summary>동일 버전 설치됨 → 실행/닫기.</summary>
    UpToDate,

    /// <summary>더 최신 버전 설치됨 → 다운그레이드 방지(실행만 안내).</summary>
    Downgrade,
}

/// <summary>설치 여부·버전 비교 결과.</summary>
public sealed record InstallPlan(InstallMode Mode, string? InstalledVersion, string TargetVersion);

/// <summary>
/// 설치 전 현재 설치 상태를 감지하고(설치본 exe의 ProductVersion) 인스톨러가 담은 대상 버전과 비교해
/// 신규 설치 / 업데이트 / 최신 / 다운그레이드 중 무엇인지 결정한다. Decide는 순수 함수(테스트 대상).
/// </summary>
public static class InstallProbe
{
    /// <summary>설치본 exe의 ProductVersion 원문(빌드 메타 포함). 미설치/읽기 실패면 null.</summary>
    public static string? ReadInstalledVersion(string installedExePath)
    {
        try
        {
            if (!File.Exists(installedExePath))
            {
                return null;
            }

            var v = FileVersionInfo.GetVersionInfo(installedExePath).ProductVersion;
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>설치 상태 결정. installedVersion=null이면 미설치.
    /// 설치는 됐는데 버전 파싱 불가 → 안전하게 Update(재설치 성격). target 파싱 불가 → 방어적으로 NotInstalled.</summary>
    public static InstallPlan Decide(string? installedVersion, string targetVersion)
    {
        if (installedVersion is null)
        {
            return new InstallPlan(InstallMode.NotInstalled, null, targetVersion);
        }

        if (!TryParse(installedVersion, out var installed))
        {
            return new InstallPlan(InstallMode.Update, installedVersion, targetVersion);
        }

        if (!TryParse(targetVersion, out var target))
        {
            return new InstallPlan(InstallMode.NotInstalled, installedVersion, targetVersion);
        }

        var cmp = Compare(installed, target);
        var mode = cmp < 0 ? InstallMode.Update
            : cmp == 0 ? InstallMode.UpToDate
            : InstallMode.Downgrade;
        return new InstallPlan(mode, installedVersion, targetVersion);
    }

    /// <summary>semver major.minor.patch 숫자 비교 (-1/0/1). 프리릴리스·빌드메타는 비교에서 무시.
    /// (인스톨러는 정식 릴리스만 다루므로 프리릴리스 순서 규칙까지는 불필요.)</summary>
    public static int CompareVersions(string a, string b)
    {
        if (!TryParse(a, out var va) || !TryParse(b, out var vb))
        {
            return string.CompareOrdinal(a, b);
        }

        return Compare(va, vb);
    }

    private static int Compare((int Major, int Minor, int Patch) a, (int Major, int Minor, int Patch) b)
    {
        if (a.Major != b.Major)
        {
            return a.Major < b.Major ? -1 : 1;
        }

        if (a.Minor != b.Minor)
        {
            return a.Minor < b.Minor ? -1 : 1;
        }

        if (a.Patch != b.Patch)
        {
            return a.Patch < b.Patch ? -1 : 1;
        }

        return 0;
    }

    /// <summary>"v2.1.0", "2.1.0-beta.1", "2.0.2+hash" 등에서 major.minor.patch 추출.</summary>
    private static bool TryParse(string? version, out (int Major, int Minor, int Patch) result)
    {
        result = (0, 0, 0);
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var s = version.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
        {
            s = s[1..];
        }

        // 프리릴리스(-)·빌드메타(+) 절단
        var cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            s = s[..cut];
        }

        var parts = s.Split('.');
        if (parts.Length == 0 || !int.TryParse(parts[0], out var major))
        {
            return false;
        }

        var minor = 0;
        var patch = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], out minor))
        {
            return false;
        }

        if (parts.Length > 2 && !int.TryParse(parts[2], out patch))
        {
            return false;
        }

        result = (major, minor, patch);
        return true;
    }
}
