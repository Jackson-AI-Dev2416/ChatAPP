param(
    [switch]$Release
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\Ts3Messenger\Ts3Messenger.csproj"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error ".NET 8 SDK가 필요합니다: https://aka.ms/dotnet/download"
}

$config = if ($Release) { "Release" } else { "Debug" }
$sln = Join-Path $root "Ts3Messenger.sln"
$iconScript = Join-Path $PSScriptRoot "generate-app-icon.ps1"
if (Test-Path $iconScript) {
    & $iconScript
}
dotnet restore $sln
dotnet build $sln -c $config
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$testProj = Join-Path $root "tests\Ts3Messenger.Tests\Ts3Messenger.Tests.csproj"
dotnet test $testProj -c $config --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Release) {
    if (Test-Path $iconScript) {
        & $iconScript
        dotnet build $sln -c Release
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    dotnet publish $proj -c Release -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o (Join-Path $root "dist\TS3 Messenger")
    Write-Host "Published: $(Join-Path $root 'dist\TS3 Messenger\Ts3Messenger.exe')"
}
