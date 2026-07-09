# Claude Usage Monitor — 배포 스크립트
# 무설치 self-contained 단일 exe 생성 (WPF는 트리밍/NativeAOT 미지원)
param(
    [string]$Runtime = "win-x64",
    [string]$Output = "artifacts\publish"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $userLocal = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
    if (Test-Path $userLocal) { $dotnet = $userLocal } else { throw ".NET SDK를 찾을 수 없습니다" }
} else {
    $dotnet = $dotnet.Source
}

& $dotnet publish "src\ClaudeUsageMonitor.App" `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $Output

Write-Host ""
Write-Host "완료: $root\$Output\Agent Usage Monitor.exe"
Get-Item "$Output\Agent Usage Monitor.exe" | Select-Object Name, @{N = "SizeMB"; E = { [Math]::Round($_.Length / 1MB, 1) } }
