<#
.SYNOPSIS
    Pack Blazecoin.ServiceDefaults and push it to NuGet.org. The API key is read from a file you keep outside the
    repo (default C:\Users\Andrew\nuget-apikey.txt, one line) and is never printed or stored anywhere else.

.PARAMETER Version
    Override the package version (default: the <Version> in the csproj).
.PARAMETER NoPush
    Pack only; the .nupkg lands in out\.
.PARAMETER KeyFile
    Path to the file holding the NuGet.org API key (scope: push new packages and versions, glob *).

.EXAMPLE
    .\Publish-ServiceDefaults.ps1 -NoPush
    .\Publish-ServiceDefaults.ps1                 # pack + push the csproj version
    .\Publish-ServiceDefaults.ps1 -Version 1.0.1
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$NoPush,
    [string]$KeyFile = 'C:\Users\Andrew\nuget-apikey.txt',
    [string]$Source = 'https://api.nuget.org/v3/index.json'
)
$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'src\Blazecoin.ServiceDefaults\Blazecoin.ServiceDefaults.csproj'
$out = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force $out | Out-Null

if (-not $Version) {
    $x = [IO.File]::ReadAllText($proj)
    if ($x -notmatch '<Version>\s*([0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?)\s*</Version>') { throw 'Version not found in the csproj' }
    $Version = $Matches[1]
}

Write-Host "Testing..."
dotnet test (Join-Path $PSScriptRoot 'tests\Blazecoin.ServiceDefaults.Tests\Blazecoin.ServiceDefaults.Tests.csproj') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'tests failed - not packing' }

Write-Host "Packing Blazecoin.ServiceDefaults $Version -> $out"
dotnet pack $proj -c Release -o $out -p:Version=$Version -p:ContinuousIntegrationBuild=true --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed' }
$pkg = Get-Item (Join-Path $out "Blazecoin.ServiceDefaults.$Version.nupkg")
Write-Host ("  {0}  {1:N0} bytes" -f $pkg.Name, $pkg.Length)

if ($NoPush) { Write-Host 'NoPush: done.'; return }

if (-not (Test-Path $KeyFile)) { throw "API key file not found: $KeyFile (one line, the NuGet.org API key)" }
$key = (Get-Content $KeyFile -First 1).Trim()
if (-not $key) { throw "API key file is empty: $KeyFile" }

Write-Host "Pushing $($pkg.Name) -> $Source"
dotnet nuget push $pkg.FullName --source $Source --api-key $key --skip-duplicate
$rc = $LASTEXITCODE
Remove-Variable key
if ($rc -ne 0) { throw 'push failed' }
Write-Host "Published Blazecoin.ServiceDefaults $Version. NuGet.org indexes a new package in a few minutes; restores may 404 until then."
