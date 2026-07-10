# Bump app version, commit, tag and push -> GitHub Actions builds and publishes the release.
# Usage:
#   .\bump-version.ps1                # patch: 1.0.1 -> 1.0.2
#   .\bump-version.ps1 -Part minor    # 1.0.2 -> 1.1.0
#   .\bump-version.ps1 -Part major    # 1.1.0 -> 2.0.0
#   .\bump-version.ps1 -DryRun        # print what would happen
#   .\bump-version.ps1 -NoPush        # bump + commit + tag only (push manually later)
param(
    [ValidateSet('major', 'minor', 'patch')]
    [string]$Part = 'patch',
    [switch]$DryRun,
    [switch]$NoPush
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = Join-Path $root 'src\ClaudeUsageMonitor.App\ClaudeUsageMonitor.App.csproj'

$content = [System.IO.File]::ReadAllText($csproj)
if ($content -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') {
    throw "No <Version>x.y.z</Version> found in $csproj"
}
$major = [int]$Matches[1]; $minor = [int]$Matches[2]; $patch = [int]$Matches[3]
$old = "$major.$minor.$patch"

switch ($Part) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
}
$new = "$major.$minor.$patch"

Write-Host "version: $old -> $new  (tag v$new)"
if ($DryRun) { Write-Host '(dry run - no changes made)'; exit 0 }

[System.IO.File]::WriteAllText(
    $csproj, $content.Replace("<Version>$old</Version>", "<Version>$new</Version>"))

Push-Location $root   # repo root = source 저장소 루트 (2026-07-10 저장소 분리)
# git의 stderr 경고(LF/CRLF 등)가 Stop 정책에서 오류로 승격되지 않도록 — 실패 판정은 $LASTEXITCODE로
$ErrorActionPreference = 'Continue'
try {
    git add $csproj
    git commit -m "chore: bump version to $new"
    if ($LASTEXITCODE -ne 0) { throw 'git commit failed' }
    git tag "v$new"
    if ($LASTEXITCODE -ne 0) { throw 'git tag failed' }
    if (-not $NoPush) {
        git push origin master
        if ($LASTEXITCODE -ne 0) { throw 'git push failed' }
        git push origin "v$new"
        if ($LASTEXITCODE -ne 0) { throw 'git tag push failed' }
        Write-Host "pushed - GitHub Actions will build and publish release v$new"
    }
    else {
        Write-Host "committed and tagged locally - push with: git push origin master; git push origin v$new"
    }
}
finally {
    Pop-Location
}
