using ClaudeUsageMonitor.Core.Storage;

namespace ClaudeUsageMonitor.Core.Updates;

/// <summary>적용 직전 기록되는 마커 내용.</summary>
public sealed record PendingUpdateMarker(string TargetVersion, DateTimeOffset WrittenUtc);

/// <summary>재시작 연속성 판정 결과.</summary>
public enum PendingUpdateAssessment
{
    /// <summary>마커 없음 — 할 일 없음.</summary>
    None,

    /// <summary>적용이 아직 진행 중일 수 있음(유예 창) — 침묵. 무창 구간에 사용자가 앱을
    /// 수동 실행했을 때 "완료되지 않았습니다" 오경보를 내지 않기 위한 창.</summary>
    InProgress,

    /// <summary>대상 버전 이상으로 실행 중 — 완료 카드 1회 표시 후 마커 삭제.</summary>
    Completed,

    /// <summary>유예 창이 지났는데 구버전 그대로 — 적용 실패 안내 후 마커 삭제.</summary>
    Failed,

    /// <summary>오래된 잔존 마커(강제 종료 등) — 조용히 삭제.</summary>
    Stale,
}

/// <summary>
/// 인앱 업데이트 적용 직전 상태를 파일로 외부화하는 마커 — 적용~재시작 사이 무창 구간의 결과를
/// 재시작(--update-done) 또는 다음 실행이 이어받아 완료/실패를 가시화한다(옵션 A 재시작 연속성).
/// 마커는 보조 신호다: 기록 실패는 업데이트를 막지 않고, 연속성 표시만 저하된다.
/// </summary>
public sealed class UpdatePendingMarker
{
    /// <summary>적용 시작 후 이 시간 안에는 "진행 중"으로 간주 — 오경보 차단 유예 창.</summary>
    public static readonly TimeSpan InProgressGrace = TimeSpan.FromMinutes(3);

    /// <summary>유예 창 이후 이 시간까지는 실패로 안내, 넘으면 스테일 폐기.</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(30);

    private readonly string _path;

    public UpdatePendingMarker(string dataDirectory)
        => _path = Path.Combine(dataDirectory, "update-pending.json");

    /// <summary>적용 직전 기록 (원자적). 실패해도 업데이트는 계속 — 연속성 표시만 저하.</summary>
    public bool Write(string targetVersion, DateTimeOffset? nowUtc = null)
    {
        try
        {
            AtomicJsonFile.Save(_path, new PendingUpdateMarker(targetVersion, nowUtc ?? DateTimeOffset.UtcNow));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public PendingUpdateMarker? TryRead()
    {
        try
        {
            return AtomicJsonFile.Load<PendingUpdateMarker>(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Delete()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 삭제 실패 — 다음 실행의 스테일 판정이 정리
        }
    }

    /// <summary>순수 판정: 현재 버전·마커·현재 시각으로 재시작 연속성 결과를 결정.</summary>
    public static PendingUpdateAssessment Assess(
        PendingUpdateMarker? marker, string currentVersion, DateTimeOffset nowUtc)
    {
        if (marker is null)
        {
            return PendingUpdateAssessment.None;
        }

        if (IsApplied(currentVersion, marker.TargetVersion))
        {
            return PendingUpdateAssessment.Completed;
        }

        var age = nowUtc - marker.WrittenUtc;
        if (age < InProgressGrace)
        {
            return PendingUpdateAssessment.InProgress;
        }

        return age <= FailureWindow ? PendingUpdateAssessment.Failed : PendingUpdateAssessment.Stale;
    }

    /// <summary>현재 버전이 대상 버전 이상인지 (major.minor.patch 숫자 비교 — v 접두/빌드메타 허용).
    /// 어느 한쪽이라도 파싱 불가면 문자열 동등으로 폴백(방어적).</summary>
    public static bool IsApplied(string currentVersion, string targetVersion)
    {
        if (!TryParse(currentVersion, out var current) || !TryParse(targetVersion, out var target))
        {
            return string.Equals(currentVersion.Trim(), targetVersion.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (current.Major != target.Major)
        {
            return current.Major > target.Major;
        }

        if (current.Minor != target.Minor)
        {
            return current.Minor > target.Minor;
        }

        if (current.Patch != target.Patch)
        {
            return current.Patch > target.Patch;
        }

        // 코어(M.m.p)가 같으면 프리릴리스 순서로 비교 — 정식(Pre=MaxValue) > beta.N > 낮은 beta.
        // 이로써 beta.3→beta.4 실패(여전히 beta.3)를 '완료'로 오판하지 않는다.
        return current.Pre >= target.Pre;
    }

    private static bool TryParse(string? version, out (int Major, int Minor, int Patch, int Pre) result)
    {
        result = (0, 0, 0, int.MaxValue);
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var s = version.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
        {
            s = s[1..];
        }

        // 빌드메타(+) 절단
        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            s = s[..plus];
        }

        // 프리릴리스(-) 분리: 없으면 정식(Pre=MaxValue, 가장 높음), '-beta.N'이면 N, 그 외 프리릴리스는 0.
        var pre = int.MaxValue;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            var preTag = s[(dash + 1)..];
            s = s[..dash];
            var dot = preTag.IndexOf('.');
            if (dot >= 0
                && preTag[..dot].Equals("beta", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(preTag[(dot + 1)..], out var n))
            {
                pre = n;
            }
            else
            {
                // beta.N 이외 프리릴리스(rc/alpha 등)는 파싱 실패로 두어 IsApplied가 문자열 정확 동등으로 폴백 —
                // 전부 pre=0으로 뭉개면 서로 다른 rc가 >= 로 오판돼 실패한 업데이트를 'Completed'로 보고했다.
                return false;
            }
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

        result = (major, minor, patch, pre);
        return true;
    }
}
