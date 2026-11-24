<#
.SYNOPSIS
    Starts the WebAirIG host (CIGI/DIS + control UI), the consumer (decode/YOLO/metrics UI) and the Godot IG.
.EXAMPLE
    ./scripts/run-all.ps1 -Build
    ./scripts/run-all.ps1 -NoIg                # web apps only
    ./scripts/run-all.ps1 -IgArgs "--no-stream"
#>
param(
    [switch]$Build,
    [switch]$NoHost,
    [switch]$NoConsumer,
    [switch]$NoIg,
    [string]$Godot = "",
    [string]$IgArgs = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $Godot) { $Godot = Join-Path $root "Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe" }

if ($Build) {
    dotnet build (Join-Path $root "WebAirIG.slnx")
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}

function Start-WebApp([string]$name) {
    $dir = Join-Path $root "src\$name"
    $exe = Join-Path $dir "bin\Debug\net10.0\$name.exe"
    if (-not (Test-Path $exe)) { throw "$exe not found - run with -Build first" }
    Start-Process -FilePath $exe -WorkingDirectory $dir
    Write-Host "Started $name"
}

if (-not $NoHost) { Start-WebApp "WebAirIG.Host"; Start-Sleep -Seconds 2 }
if (-not $NoConsumer) { Start-WebApp "WebAirIG.Consumer" }
if (-not $NoIg) {
    if (-not (Test-Path $Godot)) { throw "Godot not found at $Godot (use -Godot <path>)" }
    $igArgList = @("--path", (Join-Path $root "ig"))
    if ($IgArgs) { $igArgList += "--"; $igArgList += $IgArgs.Split(" ", [StringSplitOptions]::RemoveEmptyEntries) }
    Start-Process -FilePath $Godot -ArgumentList $igArgList
    Write-Host "Started IG"
}

Write-Host ""
Write-Host "Host UI:     http://localhost:5080   (sensors, coverage map, scenario)"
Write-Host "Consumer UI: http://localhost:5090   (live video, YOLO metrics, dataset recorder)"
