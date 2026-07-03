#!/usr/bin/env pwsh
<#
.SYNOPSIS
  CI-friendly build for Lumen: restore, build (warnings as errors), test.
.EXAMPLE
  ./build.ps1                    # Release build + tests (perf tests excluded)
  ./build.ps1 -IncludePerf       # also run perf-gated tests
  ./build.ps1 -Coverage          # collect XPlat code coverage
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$IncludePerf,
    [switch]$Coverage,
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot 'Lumen.sln'

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $Name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Invoke-Step 'restore' { dotnet restore $solution }
Invoke-Step 'build'   { dotnet build $solution -c $Configuration --no-restore }

if (-not $SkipTests) {
    $testArgs = @($solution, '-c', $Configuration, '--no-build')
    if (-not $IncludePerf) { $testArgs += @('--filter', 'Category!=Perf') }
    if ($Coverage) { $testArgs += @('--collect', 'XPlat Code Coverage') }
    Invoke-Step 'test' { dotnet test @testArgs }
}

if ($Package) {
    $app = Join-Path $PSScriptRoot 'src\Lumen.App\Lumen.App.csproj'
    Invoke-Step 'publish' { dotnet publish $app -c Release -p:PublishProfile=win-x64 }

    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iscc) {
        Invoke-Step 'installer' { & $iscc (Join-Path $PSScriptRoot 'build\Lumen.iss') }
    }
    else {
        Write-Host 'Inno Setup not found; skipping installer (payload is in artifacts/publish).' -ForegroundColor Yellow
    }
}

Write-Host 'Build succeeded.' -ForegroundColor Green
