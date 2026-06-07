# Regenerates the decompiled reference tree from the installed game assembly.
# Output (tools/decompiled/) is git-ignored. Re-run after a game update.
#
#   pwsh tools/decompile.ps1
#   pwsh tools/decompile.ps1 -Type GreyScriptHelperServer   # dump a single type to stdout

param(
    [string]$Type = "",
    [string]$Game = "C:\Program Files (x86)\Steam\steamapps\common\Grey Hack"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$asm  = Join-Path $Game "Grey Hack_Data\Managed\Assembly-CSharp.dll"
if (-not (Test-Path $asm)) { throw "Assembly-CSharp.dll not found at: $asm" }

# Locate (downloading if needed) the ilspycmd net6.0 build.
$ver  = "8.2.0.7535"
$tool = Join-Path $PSScriptRoot "_dl\ilspycmd_$ver\tools\net6.0\any\ilspycmd.dll"
if (-not (Test-Path $tool)) {
    $dl = Join-Path $PSScriptRoot "_dl"; New-Item -ItemType Directory -Force $dl | Out-Null
    $zip = Join-Path $dl "ilspycmd.$ver.zip"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/ilspycmd/$ver/ilspycmd.$ver.nupkg" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip -DestinationPath (Join-Path $dl "ilspycmd_$ver") -Force
}

if ($Type) {
    & dotnet $tool $asm -t $Type
} else {
    $out = Join-Path $PSScriptRoot "decompiled\Assembly-CSharp"
    New-Item -ItemType Directory -Force $out | Out-Null
    Write-Host "Decompiling -> $out ..."
    & dotnet $tool $asm -p -o $out
    Write-Host "Done: $((Get-ChildItem $out -Recurse -Filter *.cs).Count) .cs files"
}
