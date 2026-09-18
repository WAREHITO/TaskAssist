[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Windows is required for WPF and the DPAPI tests.'
}
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$dotnet = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
$env:DOTNET_ROOT = Split-Path $dotnet -Parent
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
function Invoke-Dotnet([string[]]$CommandArguments) {
    & $dotnet @CommandArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE : $($CommandArguments -join ' ')" }
}
Push-Location $repositoryRoot
try {
    $expectedSdk = (Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json).sdk.version
    $actualSdk = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or "$actualSdk".Trim() -ne $expectedSdk) {
        throw "Install .NET SDK $expectedSdk for Windows, or pass its dotnet.exe using -DotnetPath."
    }
    $desktop = 'src/TaskAssist.Desktop/TaskAssist.Desktop.csproj'
    $tests = 'tests/TaskAssist.Tests/TaskAssist.Tests.csproj'
    $windowsTests = 'tests/TaskAssist.WindowsTests/TaskAssist.WindowsTests.csproj'
    $worker = 'src/TaskAssist.OutlookWorker/TaskAssist.OutlookWorker.csproj'
    foreach ($project in @($desktop, $tests, $windowsTests, $worker)) {
        Invoke-Dotnet @('restore', $project, '--locked-mode')
    }
    foreach ($project in @($desktop, $tests, $windowsTests, $worker)) {
        Invoke-Dotnet @('build', $project, '-c', 'Release', '--no-restore')
    }
    $runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $resultsDir = Join-Path $repositoryRoot ".artifacts/tests/$runId"
    New-Item -ItemType Directory -Path $resultsDir | Out-Null
    $resultsFile = Join-Path $resultsDir 'automated-tests.json'
    # This project is an executable test runner. dotnet test does not run its checks.
    Invoke-Dotnet @('run', '--project', $tests, '-c', 'Release', '--no-build', '--no-restore', '--', $resultsFile)
    $results = Get-Content -LiteralPath $resultsFile -Raw | ConvertFrom-Json
    if ($results.total -le 0 -or $results.failed -ne 0 -or
        @($results.tests).Count -ne $results.total -or
        @($results.tests | Where-Object { $_.status -ne 'passed' }).Count -ne 0) {
        throw 'The test report does not show a complete passing run.'
    }
    if ($Publish) {
        $version = ([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version
        $publishDir = Join-Path $repositoryRoot ".artifacts/publish/TaskAssist-$version-win-x64-$runId"
        Invoke-Dotnet @('publish', $desktop, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '-p:RestoreLockedMode=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $publishDir)
        Invoke-Dotnet @('publish', $worker, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '-p:RestoreLockedMode=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', (Join-Path $publishDir 'outlook-worker'))
        Write-Host "Executable: $publishDir/TaskAssist.exe"
    }
    Write-Host "PASS: $($results.total) automated checks. Report: $resultsFile"
    Write-Host 'The Windows UI test host was built, but interactive UI tests were not run by this script.'
}
finally { Pop-Location }
