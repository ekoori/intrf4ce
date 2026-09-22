<#
.SYNOPSIS
    Checks that the Linux build actually runs on Linux.

.DESCRIPTION
    Publishes linux-x64 and runs it inside a container that has only the .NET runtime
    dependencies, so a missing native library shows up here rather than on a user's machine.
    It checks the command line, not the window: the stage needs a display, and the hidden
    renderer needs WebKitGTK, neither of which a bare container has.

    Needs Docker or Podman running.

.EXAMPLE
    ./scripts/smoke-linux.ps1
#>
[CmdletBinding()]
param(
    [string]$Image = "mcr.microsoft.com/dotnet/runtime-deps:10.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist/linux-x64"

$runtime = @("docker", "podman") | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $runtime) { throw "neither docker nor podman is installed" }

& $PSScriptRoot/package.ps1 -Runtime linux-x64

$script = @(
    "cp /app/intrf4ce /tmp/intrf4ce",
    "chmod +x /tmp/intrf4ce",
    "/tmp/intrf4ce --version",
    "echo SMOKE-OK"
) -join " && "

Write-Host "running the Linux build in $Image with $runtime" -ForegroundColor Cyan
$output = & $runtime run --rm -v "${dist}:/app:ro" $Image /bin/sh -c $script 2>&1
$output | Write-Host

if ($output -notcontains "SMOKE-OK") { throw "the Linux build did not run" }
Write-Host "ok" -ForegroundColor Green
