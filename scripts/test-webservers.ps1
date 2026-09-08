[CmdletBinding()]
param(
    [string] $ComponentRoot = "",
    [string] $NginxArchive = ""
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$ComponentRoot) { $ComponentRoot = Join-Path $projectRoot 'work/drageon-smoke-root' }
if (!$NginxArchive) { $NginxArchive = Join-Path $projectRoot 'work/nginx-1.31.5.zip' }
$ComponentRoot = [IO.Path]::GetFullPath($ComponentRoot)
$NginxArchive = [IO.Path]::GetFullPath($NginxArchive)
foreach ($binary in @('bin/php/8.5.10/php-cgi.exe', 'bin/apache/2.4.68/bin/httpd.exe')) {
    if (!(Test-Path (Join-Path $ComponentRoot $binary))) { throw "Missing test prerequisite: $ComponentRoot/$binary" }
}
if (!(Test-Path $NginxArchive)) { throw 'Download the Nginx ZIP referenced by portable-template/app/packages/nginx.json first, and pass -NginxArchive.' }
$testRoot = Join-Path $projectRoot ('work/webserver-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $testRoot | Out-Null
Copy-Item (Join-Path $projectRoot 'portable-template/*') $testRoot -Recurse
New-Item -ItemType File (Join-Path $testRoot '.webserver-test-root') | Out-Null
New-Item -ItemType Directory (Join-Path $testRoot 'bin/php'),(Join-Path $testRoot 'bin/apache') -Force | Out-Null
Copy-Item (Join-Path $ComponentRoot 'bin/php/8.5.10') (Join-Path $testRoot 'bin/php') -Recurse
Copy-Item (Join-Path $ComponentRoot 'bin/apache/2.4.68') (Join-Path $testRoot 'bin/apache') -Recurse
$env:DOTNET_CLI_HOME = Join-Path $projectRoot 'work/.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot 'work/.nuget/packages'
$dotnet = Join-Path $projectRoot 'work/.dotnet/dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet = (Get-Command dotnet).Source }
& $dotnet run --project (Join-Path $projectRoot 'tests/Drageon.WebServerTests/Drageon.WebServerTests.csproj') -- $testRoot $NginxArchive
$testExitCode = $LASTEXITCODE
Write-Host "Test artifacts: $testRoot"
exit $testExitCode
