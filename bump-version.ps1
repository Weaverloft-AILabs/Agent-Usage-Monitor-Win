# Bump app version, commit, tag and push -> GitHub Actions builds and publishes the release.
#
# 기본은 BETA 릴리스(별도 'beta' 채널 + GitHub prerelease). 안정 설치본(win 채널)에는 뜨지 않는다.
# 정식 안정판(X.Y.Z)은 사용자 확인 후 -Stable 로 승격 발행한다.
#
# Usage:
#   .\bump-version.ps1                # BETA(기본): 안정 2.7.9 -> 2.7.10-beta.1 / 2.7.10-beta.1 -> 2.7.10-beta.2
#   .\bump-version.ps1 -Part minor    # 새 베타 라인을 minor로: 2.7.9 -> 2.8.0-beta.1 (베타 라인 진행 중이면 -Part 무시)
#   .\bump-version.ps1 -Stable        # 승격: 2.7.10-beta.N -> 2.7.10 (안정판) / 안정 상태면 -Part만큼 다음 안정판
#   .\bump-version.ps1 -DryRun        # 무엇을 할지 출력만
#   .\bump-version.ps1 -NoPush        # bump + commit + tag 만 (푸시는 수동)
param(
    [ValidateSet('major', 'minor', 'patch')]
    [string]$Part = 'patch',
    [switch]$Stable,
    [switch]$DryRun,
    [switch]$NoPush
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = Join-Path $root 'src\ClaudeUsageMonitor.App\ClaudeUsageMonitor.App.csproj'
$installerCsproj = Join-Path $root 'src\ClaudeUsageMonitor.Installer\ClaudeUsageMonitor.Installer.csproj'

$content = [System.IO.File]::ReadAllText($csproj)
# 현재 버전 = X.Y.Z 또는 X.Y.Z-beta.N
if ($content -notmatch '<Version>(\d+)\.(\d+)\.(\d+)(?:-beta\.(\d+))?</Version>') {
    throw "No <Version>x.y.z[-beta.n]</Version> found in $csproj"
}
$major = [int]$Matches[1]; $minor = [int]$Matches[2]; $patch = [int]$Matches[3]
$betaSeq = if ($Matches[4]) { [int]$Matches[4] } else { $null }
$old = if ($null -ne $betaSeq) { "$major.$minor.$patch-beta.$betaSeq" } else { "$major.$minor.$patch" }

function Get-BumpedBase([int]$M, [int]$Mi, [int]$P, [string]$part) {
    switch ($part) {
        'major' { return @(($M + 1), 0, 0) }
        'minor' { return @($M, ($Mi + 1), 0) }
        'patch' { return @($M, $Mi, ($P + 1)) }
    }
}

if ($Stable) {
    if ($null -ne $betaSeq) {
        # 승격: 현재 베타 라인을 그대로 안정판으로 (X.Y.Z-beta.N -> X.Y.Z)
        $new = "$major.$minor.$patch"
    }
    else {
        # 이미 안정판 -> -Part 만큼 다음 안정판
        $b = Get-BumpedBase $major $minor $patch $Part
        $new = "$($b[0]).$($b[1]).$($b[2])"
    }
}
else {
    # 기본 = 베타
    if ($null -ne $betaSeq) {
        # 같은 베타 라인 계속 (seq 증가; -Part 무시)
        $new = "$major.$minor.$patch-beta.$($betaSeq + 1)"
    }
    else {
        # 안정판에서 새 베타 라인 시작 (-Part 만큼 올린 뒤 -beta.1)
        $b = Get-BumpedBase $major $minor $patch $Part
        $new = "$($b[0]).$($b[1]).$($b[2])-beta.1"
    }
}

# 최종 안전 가드 — X.Y.Z 또는 X.Y.Z-beta.N 외의 태그는 절대 만들지 않는다
if ($new -notmatch '^\d+\.\d+\.\d+(-beta\.\d+)?$') {
    throw "refusing to tag non-conforming version: $new"
}

$kind = if ($new -like '*-beta*') { 'BETA (channel=beta, prerelease)' } else { 'STABLE (channel=win)' }
Write-Host "version: $old -> $new  (tag v$new)  [$kind]"
if ($DryRun) { Write-Host '(dry run - no changes made)'; exit 0 }

[System.IO.File]::WriteAllText(
    $csproj, $content.Replace("<Version>$old</Version>", "<Version>$new</Version>"))

# 인스톨러 csproj도 같은 버전으로 동기화 (릴리스는 release.yml의 -p:Version이 지배하지만
# 로컬 Debug 빌드 버전이 어긋나면 인스톨러 업데이트 감지가 오판할 수 있음)
$installerContent = [System.IO.File]::ReadAllText($installerCsproj)
$installerContent = [System.Text.RegularExpressions.Regex]::Replace(
    $installerContent, '<Version>\d+\.\d+\.\d+(-beta\.\d+)?</Version>', "<Version>$new</Version>")
[System.IO.File]::WriteAllText($installerCsproj, $installerContent)

Push-Location $root   # repo root = source 저장소 루트 (2026-07-10 저장소 분리)
# git의 stderr 경고(LF/CRLF 등)가 Stop 정책에서 오류로 승격되지 않도록 — 실패 판정은 $LASTEXITCODE로
$ErrorActionPreference = 'Continue'
try {
    git add $csproj $installerCsproj
    git commit -m "chore: bump version to $new"
    if ($LASTEXITCODE -ne 0) { throw 'git commit failed' }
    git tag "v$new"
    if ($LASTEXITCODE -ne 0) { throw 'git tag failed' }
    if (-not $NoPush) {
        git push origin master
        if ($LASTEXITCODE -ne 0) { throw 'git push failed' }
        git push origin "v$new"
        if ($LASTEXITCODE -ne 0) { throw 'git tag push failed' }
        Write-Host "pushed - GitHub Actions will build and publish release v$new [$kind]"
    }
    else {
        Write-Host "committed and tagged locally - push with: git push origin master; git push origin v$new"
    }
}
finally {
    Pop-Location
}
