[CmdletBinding()]
param(
    [string] $Output
)

$ErrorActionPreference = "Stop"
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $projectRoot "publish\win-x64"
}
$project = Join-Path $projectRoot "src\Drageon.App\Drageon.App.csproj"
$localDotnet = Join-Path $projectRoot "work\.dotnet\dotnet.exe"
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $localDotnet -PathType Leaf) {
    $dotnet = $localDotnet
}
elseif ($dotnetCommand) {
    $dotnet = $dotnetCommand.Source
}
else {
    throw ".NET 10 SDK wurde nicht gefunden. Installiere das SDK von https://dotnet.microsoft.com/download/dotnet/10.0 und starte das Terminal neu."
}

$dotnetHome = Join-Path $projectRoot "work\.dotnet-home"
[System.IO.Directory]::CreateDirectory($dotnetHome) | Out-Null
$env:DOTNET_CLI_HOME = $dotnetHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = "0"
$env:NUGET_PACKAGES = Join-Path $projectRoot "work\.nuget\packages"
[System.IO.Directory]::CreateDirectory($env:NUGET_PACKAGES) | Out-Null

$outputPath = [System.IO.Path]::GetFullPath($Output)
& $dotnet restore $project `
    --runtime win-x64 `
    --configfile (Join-Path $projectRoot "NuGet.Config") `
    --ignore-failed-sources `
    -p:TreatWarningsAsErrors=false `
    -p:NuGetAudit=false

if ($LASTEXITCODE -ne 0) {
    throw "Die Abhängigkeiten für den Release-Build konnten nicht wiederhergestellt werden."
}

& $dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $outputPath `
    --no-restore `
    -p:NuGetAudit=false

if ($LASTEXITCODE -ne 0) {
    throw "Der Release-Build ist fehlgeschlagen."
}

Write-Host "Portable Ausgabe: $outputPath"
