using System.IO;
using System.Reflection;

namespace ClaudeUsageMonitor.Installer.Install;

/// <summary>
/// 오프라인 설치 지원(Design C) — 릴리스 빌드는 vpk pack이 만든 Velopack Setup.exe를
/// <c>EmbeddedResource</c>(LogicalName=<see cref="ResourceName"/>)로 인스톨러에 담고,
/// 이 클래스가 런타임에 임시 exe로 추출한다. 임베드가 실제 설치 페이로드이므로 인스톨러는
/// 네트워크 없이도(오프라인) 자기 버전을 설치할 수 있다.
/// 로컬 dev 빌드에는 리소스가 없어 <see cref="TryExtract"/>가 null → 다운로드 폴백으로 진행한다.
/// (임베드 배선은 인스톨러 csproj의 조건부 EmbeddedResource + release.yml의 EmbeddedSetupPath 참고.)
/// </summary>
public static class EmbeddedSetup
{
    /// <summary>인스톨러 csproj가 Setup.exe를 담을 때 부여하는 논리 리소스명.</summary>
    public const string ResourceName = "EmbeddedSetup.exe";

    /// <summary>임베드된 Setup가 있으면 임시 exe로 추출해 경로 반환, 없으면(dev 빌드) null.</summary>
    public static string? TryExtract() =>
        Extract(() => typeof(EmbeddedSetup).Assembly.GetManifestResourceStream(ResourceName), Path.GetTempPath());

    /// <summary>테스트 가능한 코어 — <paramref name="open"/>가 null이면 null(리소스 없음),
    /// 아니면 <paramref name="tempDir"/>에 고유 이름 exe로 복사하고 그 경로를 반환한다.</summary>
    public static string? Extract(Func<Stream?> open, string tempDir)
    {
        using var source = open();
        if (source is null)
        {
            return null;
        }

        // 고유 임시 이름 — 동시 실행/이전 잔존 파일과의 충돌 방지 (사용 후 호출부 finally에서 삭제)
        var destination = Path.Combine(tempDir, $"AgentUsageMonitor-Setup-embedded-{Guid.NewGuid():N}.exe");
        using (var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(file);
        }

        return destination;
    }
}
