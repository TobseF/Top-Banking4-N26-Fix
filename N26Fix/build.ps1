# Baut Subsembly.Scraper.N26Fix.dll und eine gepatchte Subsembly.Scraper.dll in .\build
param([string]$AppDir = 'C:\Program Files (x86)\TopBanking4')
$ErrorActionPreference = 'Stop'
$w = $PSScriptRoot
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = "$fw\csc.exe"
New-Item -ItemType Directory -Force "$w\build" | Out-Null

# Original sichern/verwenden: falls AppDir schon gepatcht ist, das Backup als Quelle nehmen
$src = "$AppDir\Subsembly.Scraper.dll"
if (Test-Path "$AppDir\Subsembly.Scraper.dll.orig") { $src = "$AppDir\Subsembly.Scraper.dll.orig" }

& $csc /nologo /target:library /optimize+ /out:"$w\build\Subsembly.Scraper.N26Fix.dll" `
    /r:"$AppDir\Subsembly.Interweb.dll" /r:"$AppDir\Subsembly.Json.dll" /r:"$fw\netstandard.dll" "$w\src\N26Throttle.cs"
if ($LASTEXITCODE) { throw 'csc N26Fix failed' }

# Patcher immer frisch aus dem Quellcode bauen (benoetigt tools\Mono.Cecil.dll, siehe README)
if (-not (Test-Path "$w\tools\Mono.Cecil.dll")) { throw 'tools\Mono.Cecil.dll fehlt - siehe README, Abschnitt "Voraussetzungen".' }
& $csc /nologo /target:exe /out:"$w\tools\Patcher.exe" /r:"$w\tools\Mono.Cecil.dll" /r:System.Core.dll "$w\src\Patcher.cs"
if ($LASTEXITCODE) { throw 'csc Patcher failed' }
Remove-Item "$w\build\Subsembly.Scraper.dll" -ErrorAction SilentlyContinue
& "$w\tools\Patcher.exe" $src "$w\build\Subsembly.Scraper.dll" "$w\build\Subsembly.Scraper.N26Fix.dll" $AppDir
if ($LASTEXITCODE) { throw 'patch failed' }
