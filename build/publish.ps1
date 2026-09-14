<#
.SYNOPSIS
    Publishes CaptionBridge as a single self-contained exe.

.DESCRIPTION
    Produces publish/CaptionBridge.exe, which runs on any 64-bit Windows 10/11 machine
    without the .NET runtime installed.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\publish')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot '..\src\CaptionBridge\CaptionBridge.csproj'

dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $OutputPath `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path ([System.IO.Path]::GetFullPath($OutputPath)) 'CaptionBridge.exe'
Write-Host "Published $exe ($([Math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"
