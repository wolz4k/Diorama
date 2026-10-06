#!/usr/bin/env pwsh
# Builds a self-contained single-file release Diorama.exe into compiled/
# Needs the .NET 10 SDK, plus BrickVault and Common cloned beside this folder:
#   git clone https://github.com/connorh315/BrickVault ..\BrickVault
#   git clone https://github.com/connorh315/Common ..\Common
# Shaders are read from Rendering\Shaders next to the exe, so that folder ships beside it.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

foreach ($dep in '..\BrickVault\BrickVault.csproj', '..\Common\Common.csproj') {
    if (-not (Test-Path $dep)) {
        Write-Error "Missing $dep. Clone it beside this folder (see the top of build.ps1)."
    }
}

if (Test-Path compiled) { Remove-Item compiled -Recurse -Force }

dotnet publish src/Diorama.csproj -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o compiled
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Loose native DLLs next to the exe mean they weren't embedded, and the exe won't run once copied elsewhere
$loose = Get-ChildItem compiled -Filter *.dll
if ($loose) {
    Write-Error "Native DLLs were not embedded in Diorama.exe: $($loose.Name -join ', ')"
}
if (-not (Test-Path compiled\Rendering\Shaders\*.frag)) {
    Write-Error "Shaders were not copied to compiled\Rendering\Shaders"
}

$exe = Get-Item compiled\Diorama.exe
Write-Host ("Built {0} ({1:N0} MB). Copy the whole compiled folder; Diorama.exe needs Rendering\ beside it." -f $exe.FullName, ($exe.Length / 1MB))
