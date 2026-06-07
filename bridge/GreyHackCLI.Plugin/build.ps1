# Builds the BepInEx plugin with the .NET Framework csc (no SDK required).
# Compiles to a local bin/ (never locked), then deploys to the game's plugins folder.
# NOTE: while Grey Hack is running it holds the deployed DLL memory-mapped, so deploy only
# succeeds when the game is closed. The compile always succeeds regardless. The plugin only
# (re)loads at game startup, so the loop is: build -> close game -> deploy -> relaunch.
#
#   pwsh bridge/GreyHackCLI.Plugin/build.ps1            # compile + try to deploy
#   pwsh bridge/GreyHackCLI.Plugin/build.ps1 -NoDeploy  # compile only

param(
    [string]$Game = "C:\Program Files (x86)\Steam\steamapps\common\Grey Hack",
    [switch]$NoDeploy
)

$ErrorActionPreference = "Stop"
$managed   = Join-Path $Game "Grey Hack_Data\Managed"
$bepcore   = Join-Path $Game "BepInEx\core"
$binDir    = Join-Path $PSScriptRoot "bin"
$binDll    = Join-Path $binDir "GreyHackCLI.dll"
$deployDir = Join-Path $Game "BepInEx\plugins\GreyHackCLI"
$deployDll = Join-Path $deployDir "GreyHackCLI.dll"
$csc       = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

foreach ($p in @($managed, $bepcore, $csc)) {
    if (-not (Test-Path $p)) { throw "Missing required path: $p" }
}
New-Item -ItemType Directory -Force $binDir | Out-Null

# Compile every .cs in this folder.
$sources = Get-ChildItem $PSScriptRoot -Filter *.cs | ForEach-Object { $_.FullName }

# NOTE: do not hand-quote these; the call operator (&) quotes array args automatically.
# (System.dll / System.Core.dll are auto-imported by csc — do not list them or CS1703 results.)
$refs = @(
    "$managed\netstandard.dll",
    "$managed\UnityEngine.dll",
    "$managed\UnityEngine.CoreModule.dll",
    "$managed\Assembly-CSharp.dll",
    "$bepcore\BepInEx.dll",
    "$bepcore\0Harmony.dll"
) | ForEach-Object { "/reference:$_" }

$args = @("/nologo", "/target:library", "/optimize+", "/out:$binDll") + $refs + $sources
Write-Host "Compiling -> $binDll"
& $csc @args
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Write-Host "Compiled OK: $((Get-Item $binDll).Length) bytes"

if ($NoDeploy) { return }

New-Item -ItemType Directory -Force $deployDir | Out-Null
try {
    Copy-Item $binDll $deployDll -Force
    Write-Host "Deployed -> $deployDll"
} catch {
    Write-Warning "Could not deploy (is Grey Hack running? it locks the DLL). Close the game and re-run, or run with -NoDeploy then deploy manually."
    Write-Warning $_.Exception.Message
    exit 2
}
