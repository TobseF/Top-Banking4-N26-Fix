# Installiert/entfernt den N26-Fix in Banking4. Als Administrator ausfuehren, Banking4 vorher schliessen.
#   .\install.ps1            -> installieren (Backup: Subsembly.Scraper.dll.orig)
#   .\install.ps1 -Uninstall -> Original wiederherstellen
param([switch]$Uninstall, [string]$AppDir = 'C:\Program Files (x86)\TopBanking4')
$ErrorActionPreference = 'Stop'
if (Get-Process TopBanking -ErrorAction SilentlyContinue | ? { $_.Path -like "$AppDir*" }) { throw 'Bitte Banking4 zuerst schliessen.' }
$dll = "$AppDir\Subsembly.Scraper.dll"
if ($Uninstall) {
    Copy-Item "$dll.orig" $dll -Force
    Remove-Item "$AppDir\Subsembly.Scraper.N26Fix.dll", "$dll.orig" -Force
    'Original wiederhergestellt.'; return
}
# Ist die installierte DLL ungepatcht (Erstinstallation oder nach einem Banking4-Update),
# wird sie zum neuen Backup - so wird nie eine veraltete .orig-Version zurueckgespielt.
$fPatched = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dll)).Contains('Subsembly.Scraper.N26Fix')
if (-not $fPatched) {
    Copy-Item $dll "$dll.orig" -Force
    'Original gesichert: Subsembly.Scraper.dll.orig'
} elseif (-not (Test-Path "$dll.orig")) {
    throw 'Installierte DLL ist bereits gepatcht, aber es gibt kein Backup (.orig). Banking4 bitte reparieren/neu installieren.'
}
& "$PSScriptRoot\build.ps1" -AppDir $AppDir
Copy-Item "$PSScriptRoot\build\Subsembly.Scraper.dll", "$PSScriptRoot\build\Subsembly.Scraper.N26Fix.dll" $AppDir -Force
'N26-Fix installiert. Nach einem Banking4-Update erneut ausfuehren.'
