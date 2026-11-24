<#
.SYNOPSIS
    Converts every .blend under models/aircraft to models/aircraft/glb/<name>.glb using Blender in background mode.
.DESCRIPTION
    The IG loads .glb/.gltf/.fbx/.obj at runtime; .blend needs Blender. Install it with:
        winget install --id BlenderFoundation.Blender -e --source winget
.EXAMPLE
    ./tools/models/convert_blend.ps1
    ./tools/models/convert_blend.ps1 -Blender "D:\Blender\blender.exe"
#>
param([string]$Blender = "")

$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$script = Join-Path $PSScriptRoot "blend_to_glb.py"

if (-not $Blender) {
    $candidates = @()
    $candidates += Get-ChildItem "C:\Program Files\Blender Foundation" -Recurse -Filter blender.exe -ErrorAction SilentlyContinue
    $candidates += Get-ChildItem (Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages") -Recurse -Filter blender.exe -ErrorAction SilentlyContinue
    $Blender = ($candidates | Sort-Object FullName -Descending | Select-Object -First 1).FullName
}
if (-not $Blender -or -not (Test-Path $Blender)) { throw "Blender not found. Install it (winget install --id BlenderFoundation.Blender -e --source winget) or pass -Blender <path>." }
Write-Host "Blender: $Blender"

$aircraft = Join-Path $root "models\aircraft"
$outDir = Join-Path $aircraft "glb"
Get-ChildItem $aircraft -Recurse -Filter *.blend | Where-Object { $_.FullName -notlike "*\glb\*" } | ForEach-Object {
    $name = ($_.BaseName -replace '[^A-Za-z0-9_-]+', '_').Trim('_')
    $out = Join-Path $outDir "$name.glb"
    Write-Host "Converting $($_.FullName) -> $out"
    & $Blender -b $_.FullName --python $script -- $out
    if ($LASTEXITCODE -ne 0) { Write-Warning "Blender failed for $($_.Name)" }
}
