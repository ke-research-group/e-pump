<#
.SYNOPSIS
    Builds the x86-64 Windows executable for the ReaXus CP pump application.

.DESCRIPTION
    Must be run on Windows. WinUI 3 cannot be cross-compiled: the Windows App SDK build
    chain (the XAML compiler, MIDL, the C#/WinRT projection generator and the Windows SDK
    build tools) ships as Windows PE binaries that MSBuild invokes directly, and there is
    no macOS or Linux host for them.

    Requirements:
      - Windows 10 1809 (build 17763) or newer, x64
      - .NET SDK 8.0 (https://dotnet.microsoft.com/download/dotnet/8.0)

    The result is an unpackaged, self-contained folder: EPump.exe next to the .NET runtime
    and the Windows App SDK, so the target machine needs nothing pre-installed except, for a micro-USB
    connection, the Teledyne SSI USB driver.

.PARAMETER Configuration
    Release (default) or Debug.

.PARAMETER OutputPath
    Where to place the published output. Defaults to winui/artifacts/win-x64.

.PARAMETER Aot
    Publish with Native AOT. Removes the ~170 .NET runtime DLLs from the output.

.EXAMPLE
    pwsh winui/build/build-win-x64.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$OutputPath,
    # Native AOT: compiles the app and the .NET runtime into EPump.exe (~70 files instead of
    # ~280). Needs the Visual Studio C++ build tools for the native linker.
    [switch]$Aot
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$appProject = Join-Path $repoRoot 'winui/src/EPump.App/EPump.App.csproj'
$toolsProject = Join-Path $repoRoot 'winui/src/EPump.Tools/EPump.Tools.csproj'

if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot 'winui/artifacts/win-x64'
}

if (-not $IsWindows) {
    throw 'WinUI 3 can only be built on Windows. See the note in winui/README.md.'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found on PATH. Install .NET SDK 8.0 and try again.'
}

# Run from winui/ so winui/global.json selects the .NET 8 SDK. Windows App SDK 1.6 resolves
# its PRI build task against that SDK's layout and fails under a newer one.
Push-Location (Join-Path $repoRoot 'winui')
try {

Write-Host '==> Restoring' -ForegroundColor Cyan
dotnet restore EPump.sln

Write-Host '==> Running the hardware-independent self-test' -ForegroundColor Cyan
dotnet run --project $toolsProject -c $Configuration -- selftest
if ($LASTEXITCODE -ne 0) { throw "Self-test failed (exit code $LASTEXITCODE)." }

Write-Host "==> Publishing $Configuration | x64 to $OutputPath" -ForegroundColor Cyan
$aotArgs = if ($Aot) { @('-p:PublishAot=true') } else { @('--self-contained', 'true') }
dotnet publish $appProject `
    -c $Configuration `
    -r win-x64 `
    -p:Platform=x64 `
    @aotArgs `
    -o $OutputPath
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)." }

}
finally { Pop-Location }

$exe = Join-Path $OutputPath 'EPump.exe'
if (-not (Test-Path $exe)) { throw "Publish finished but $exe is missing." }

Write-Host ''
Write-Host "Built: $exe" -ForegroundColor Green
Write-Host "Ship the whole $OutputPath folder; EPump.exe on its own will not start."
