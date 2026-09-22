<#
.SYNOPSIS
    Builds intrf4ce as one self-contained executable per platform.

.DESCRIPTION
    Publishes win-x64, win-arm64, linux-x64 and osx-arm64 into dist/, each a single file that
    needs no .NET installed. Trimming stays off: the web renderer's interop and Svg.Skia both
    reflect, and a trimmed build fails at runtime rather than at build.

    Only Windows is verified so far. The Linux build needs a machine with WebKitGTK for the
    hidden renderer; macOS is untested.

.PARAMETER Runtime
    One runtime identifier instead of all four, e.g. -Runtime win-x64.

.PARAMETER Version
    Version stamped into the executable. Defaults to 0.1.0.

.EXAMPLE
    ./scripts/package.ps1 -Runtime win-x64
#>
[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "osx-arm64")]
    [string[]]$Runtime = @("win-x64", "win-arm64", "linux-x64", "osx-arm64"),
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src/Intrf4ce.App/Intrf4ce.App.csproj"
$dist = Join-Path $root "dist"

New-Item -ItemType Directory -Force -Path $dist | Out-Null

foreach ($rid in $Runtime) {
    $out = Join-Path $dist $rid
    Write-Host "publishing $rid" -ForegroundColor Cyan

    dotnet publish $project `
        --configuration Release `
        --runtime $rid `
        --output $out `
        -p:PublishSingleFile=true `
        -p:Version=$Version `
        --nologo

    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    $exe = Get-ChildItem -Path $out -Filter "intrf4ce*" -File |
        Where-Object { $_.Extension -in @("", ".exe") } |
        Select-Object -First 1

    if ($exe) {
        "{0,-12} {1,8:N1} MB  {2}" -f $rid, ($exe.Length / 1MB), $exe.FullName | Write-Host
    }
}

Write-Host "`ndone. The executables are in $dist" -ForegroundColor Green
