# N26-Fix für Banking4 (Top Banking4 / Banking4 Home)

Inoffizieller Patch, mit dem der **N26-Kontoabruf in Banking4 wieder funktioniert**.
Seit Ende 2025 bricht der Abruf von N26-Buchungen mit `HTTP 429 Too Many Requests` ab
(siehe [Forum: homebanking-hilfe.de, Thema 28311](https://homebanking-hilfe.de/forum/topic.php?t=28311)).
Dieser Fix drosselt die Anfragen an N26, wiederholt abgewiesene Anfragen automatisch und
bricht nicht mehr ab.

> ⚠️ **Inoffiziell.** Dieses Projekt hat nichts mit der Subsembly GmbH oder der N26 Bank zu tun.
> Es verändert eine DLL deiner Banking4-Installation. Nutzung auf eigene Gefahr. Das Original
> wird gesichert und lässt sich jederzeit wiederherstellen (siehe [Deinstallation](#deinstallation)).

Getestet mit **Banking4 Home 9.4.0.9817** (Windows 11).

---

## Inhalt

- [Das Problem](#das-problem)
- [Die Ursache](#die-ursache)
- [Was der Fix tut](#was-der-fix-tut)
- [Wie der Fix das erreicht](#wie-der-fix-das-erreicht)
- [⏳ Wichtig: Der erste Abruf kann lange dauern](#-wichtig-der-erste-abruf-kann-lange-dauern)
- [Quellcode: Wo liegt was?](#quellcode-wo-liegt-was)
- [Voraussetzungen](#voraussetzungen)
- [Kompilieren](#kompilieren)
- [Installieren](#installieren)
- [Deinstallation](#deinstallation)
- [Nach einem Banking4-Update](#nach-einem-banking4-update)
- [Einstellungen](#einstellungen)
- [Live-Log](#live-log)
- [Datenschutz](#datenschutz)
- [Fehlerbehebung](#fehlerbehebung)

---

## Das Problem

Der Abruf der N26-Kontohistorie bricht nach etwa 40 Sekunden ab:

```
#### TRC: Kontohistorie vom 21.11.2025 bis zum 31.12.2025 laden...
HTTP/1.1 429 Too Many Requests
---- ScraperResult Exception ----
Subsembly.Interweb.HttpStatusException: HTTP Status and Reason: 429 Too Many Requests
   bei Subsembly.Interweb.HttpClient.HttpGetJson(...)
   bei Subsembly.Scraper.ScraperSessionN26._ParseSpaceEntry(JsonObject jsTransaction, DateTime nFromDate)
   bei Subsembly.Scraper.ScraperSessionN26._InsertSpaceTransactions(...)
   bei Subsembly.Scraper.ScraperSessionN26.OnDownloadStatement(...)
#### ERR: HTTP Status and Reason: 429 Too Many Requests
#### ERR: Übertragung enthält Fehlermeldungen!
```

Konten herauszufiltern oder den Zeitraum zu verkürzen hilft nicht, wie die Ursache zeigt.

## Die Ursache

Die N26-Anbindung steckt in `Subsembly.Scraper.dll`, Klasse `Subsembly.Scraper.ScraperSessionN26`.
Der Abruf läuft so ab:

1. **Timeline seitenweise laden:** `GET https://api.tech26.de/api/feed/accounts/{id}` und danach
   `…/timeline?paginationKey=…`. Jede Seite enthält etwa 25 Einträge. Die Timeline liefert die
   **neuesten Buchungen zuerst**.
2. **Pro Buchung eine Detail-Anfrage:** Für *jeden einzelnen* Eintrag ruft `_ParseSpaceEntry`
   zusätzlich `GET …/api/feed/accounts/{id}/transactions?externalId=…` auf. Daher kommen
   Verwendungszweck, IBAN/BIC der Gegenseite, Mandatsreferenz, Gläubiger-ID und Händlername.
3. **Kein Enddatum:** Der N26-Code kennt nur ein *Startdatum*. Er blättert also immer **von heute
   rückwärts**, bis er das Startdatum erreicht. Wer Buchungen aus dem November 2025 abruft, fragt
   damit zuerst jede Buchung von heute bis November 2025 einzeln ab.
4. **Keine Pause, kein Retry:** Die Anfragen gehen ohne Pause raus. Beim ersten `429` wird die
   Exception nicht abgefangen, und der komplette Abruf bricht ab.

N26 begrenzt die Anzahl der Anfragen. Gemessen wurde: **nach genau 210 Anfragen kommt `429`**,
danach sperrt N26 für **etwa 2½ Minuten**. Ohne Fix erreicht Banking4 diese Grenze nach
etwa 40 Sekunden und gibt auf.

## Was der Fix tut

| Funktion | Beschreibung |
|---|---|
| **Drosselung** | Zwischen zwei N26-Anfragen liegen mindestens 500 ms (einstellbar). |
| **Retry mit Backoff** | Bei `429`, `502` oder `503` wartet der Fix 5 s, 10 s, 20 s, 40 s, danach jeweils 60 s, und versucht es erneut, bis zu 8-mal. |
| **Adaptive Bremse** | Nach jedem `429` wird der Abstand um 250 ms größer (max. +2 s). Bei Erfolg baut sich die Bremse langsam wieder ab. |
| **Details optional** | Scheitert die Detail-Anfrage einer Buchung trotz aller Wiederholungen, wird die Buchung **ohne Zusatzinfos importiert**, statt den Abruf abzubrechen. Nach 3 solchen Fehlern in Folge pausieren die Detail-Anfragen 2 Minuten. |
| **Cache** | Buchungsdetails werden pro Sitzung nur einmal abgefragt, Timeline-Seiten 30 Minuten wiederverwendet. Weitere Zeitblöcke oder ein zweiter Abruf kosten so fast keine Anfragen. Der Cache liegt nur im Arbeitsspeicher. |
| **Live-Log** | Fortschritt, Datumsstand und `429`-Pausen landen in einer Logdatei, die du *während* des Abrufs mitlesen kannst (Banking4 zeigt sein Protokoll erst danach). |

Login, TAN-Freigabe, Kontoliste, Kontoauszüge (PDF) und alle anderen Banken bleiben unverändert.

## Wie der Fix das erreicht

Der Fix besteht aus zwei kleinen C#-Programmen:

**1. `Subsembly.Scraper.N26Fix.dll`** ([`N26Fix/src/N26Throttle.cs`](N26Fix/src/N26Throttle.cs))

Eine neue DLL mit genau einer öffentlichen Methode:

```csharp
public static JsonValue GetJson(HttpClient aClient, string sUrl, UrlQueryParams aQuery, HttpHeader[] vHeaders)
```

Sie hat dieselbe Signatur wie die Instanzmethode `HttpClient.HttpGetJson(url, query, headers)`.
Der Client wird nur als erster Parameter übergeben. Intern ruft sie das Original
`aClient.HttpGetJson(...)` auf und legt Drosselung, Retry, Cache und Logging darum.

**2. Patcher** ([`N26Fix/src/Patcher.cs`](N26Fix/src/Patcher.cs))

Der Patcher öffnet mit [Mono.Cecil](https://github.com/jbevain/cecil) die **Original-DLL deiner
Installation**. Er sucht in `ScraperSessionN26` jeden IL-Befehl
`callvirt/call Subsembly.Interweb.HttpClient::HttpGetJson(string, UrlQueryParams, HttpHeader[])`
und ersetzt ihn durch `call Subsembly.Scraper.N26Fix.N26Throttle::GetJson(...)`.

Weil die statische Methode den Client als ersten Parameter nimmt, passt der Stack exakt. Es wird
**nur das Ziel von 6 Aufrufen umgebogen**, sonst ändert sich kein Byte an der Logik:

| Methode | Zweck des Aufrufs |
|---|---|
| `OnContinueLogin` (2×) | Kontoliste und Spaces nach dem Login |
| `OnDownloadStatement` | Timeline-Seiten laden |
| `OnDownloadDocumentList` | Liste der Kontoauszüge |
| `_GetAccoutHolder` | Name des Kontoinhabers |
| `_ParseSpaceEntry` | **Details pro Buchung**, die eigentliche Ursache |

Der Patcher prüft vorher, ob die DLL schon gepatcht ist. Findet er keine Aufrufstelle, etwa weil
Subsembly den Code geändert hat, bricht er ab, ohne etwas zu schreiben.

Die gepatchte `Subsembly.Scraper.dll` behält Name, Version und Public Key. Ihre
Strong-Name-Signatur stimmt danach nicht mehr. Das .NET Framework prüft diese Signatur bei voll
vertrauenswürdigen lokalen Anwendungen standardmäßig nicht (*Strong-Name Bypass*), deshalb lädt
Banking4 die DLL ganz normal.

## ⏳ Wichtig: Der erste Abruf kann lange dauern

Bei einem Abruf **weit in die Vergangenheit** muss Banking4 jede Buchung von heute bis zum
Startdatum einzeln abfragen. N26 lässt aber nur rund 210 Anfragen am Stück zu. Der Fix bricht
deshalb nicht mehr ab, **wartet aber immer wieder mehrere Minuten**.

Beispiel aus einem echten Testlauf (Abruf ab 21.11.2025, durchgeführt am 18.11.2026, also knapp ein Jahr):

```
22:45:38  === N26Fix v3 aktiv (Abstand 500 ms, Details an)
22:45:40  Timeline-Seite 1: 23 Eintraege, zurueck bis 08.11.2026
22:47:23  Timeline-Seite 11: 25 Eintraege, zurueck bis 09.09.2026
22:47:26  N26Fix: HTTP 429 nach 214 Anfragen - warte 5s, Versuch 2/9
22:49:41  N26Fix: HTTP 429 nach 214 Anfragen - warte 60s, Versuch 7/9
22:51:00  N26Fix: 225 Anfragen, 0 aus Cache, 6x HTTP 429, Abstand jetzt 1780 ms
22:53:24  N26Fix: HTTP 429 nach 424 Anfragen - warte 5s, Versuch 2/9
22:58:05  N26Fix: HTTP 429 nach 634 Anfragen - warte 5s, Versuch 2/9
23:02:46  N26Fix: HTTP 429 nach 844 Anfragen - warte 5s, Versuch 2/9
23:06:53  Timeline-Seite 48: 29 Eintraege, zurueck bis 19.11.2025
23:07:01  Timeline-Seite 49: 27 Eintraege, zurueck bis 09.11.2025   ← fertig
```

| Kennzahl | Wert |
|---|---|
| Zeitraum (zurück bis) | ~1 Jahr |
| Timeline-Seiten | 49 (je ~25 Buchungen) |
| Anfragen insgesamt | ~1000 |
| `429`-Sperren | 4, alle überbrückt |
| **Dauer** | **~21 Minuten** |
| Buchungen ohne Details | 0 |

**Faustregel:** etwa **20 Minuten pro Jahr** Historie, bei rund 1000 Buchungen im Jahr.
Schneller geht es kaum, denn die Grenze setzt N26, nicht der Fix. Während der Pausen sieht
Banking4 aus, als würde es hängen. Es arbeitet aber, das zeigt das [Live-Log](#live-log).

**Der normale Alltag ist schnell:** Beim täglichen „Konten aktualisieren“ lädt Banking4 nur die
Buchungen seit dem letzten Abruf. Das sind wenige Anfragen, weit unter dem Limit, und der Abruf
dauert Sekunden. Lange dauert es nur beim ersten Abruf oder beim Nachladen alter Historie.

Wenn es schnell gehen muss und dir die Zusatzinfos egal sind, kannst du die Detail-Anfragen
abschalten (`N26FIX_SKIP_DETAILS=1`, siehe [Einstellungen](#einstellungen)). Dann fehlen bei den
importierten Buchungen aber Verwendungszweck, IBAN/BIC, Mandat und Händlername.

## Quellcode: Wo liegt was?

Du musst keinem fertigen Binary vertrauen. **Die Installation baut alles aus dem Quellcode** und
patcht die DLL *deiner eigenen* Installation. Es wird keine fremde `Subsembly.Scraper.dll` kopiert.

```
N26Fix/
├── src/
│   ├── N26Throttle.cs   ← der eigentliche Fix (Drosselung, Retry, Cache, Log)
│   └── Patcher.cs       ← biegt die 6 Aufrufe in Subsembly.Scraper.dll um
├── tools/
│   ├── Mono.Cecil.dll   ← Drittbibliothek (MIT-Lizenz), von NuGet holen
│   └── Patcher.exe      ← wird von build.ps1 aus src/Patcher.cs gebaut
├── build/               ← Build-Ausgabe (wird von build.ps1 erzeugt)
│   ├── Subsembly.Scraper.N26Fix.dll
│   └── Subsembly.Scraper.dll     (gepatchte Kopie deiner Original-DLL)
├── build.ps1            ← kompiliert und patcht nach .\build
└── install.ps1          ← sichert das Original, ruft build.ps1 auf, kopiert nach Banking4
```

Beide `.cs`-Dateien sind kurz und kommentiert. Prüfe vor allem `N26Throttle.cs`: Der Code
schickt nichts an Dritte, er ruft nur die Original-Methode von Banking4 auf. Er schreibt nur die
Logdatei aus dem Abschnitt [Live-Log](#live-log) und liest optional die drei
Umgebungsvariablen aus [Einstellungen](#einstellungen).

Die einzige fremde DLL ist **Mono.Cecil**. Sie wird nur beim Patchen gebraucht und **nicht** in
Banking4 kopiert.

## Voraussetzungen

- Windows mit **.NET Framework 4.8** (bei Windows 10/11 vorinstalliert). Gebraucht wird der
  mitgelieferte C#-Compiler `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`.
  Ein .NET SDK oder Visual Studio ist **nicht** nötig.
- Banking4 installiert, standardmäßig unter `C:\Program Files (x86)\TopBanking4`.
- **Mono.Cecil** als `N26Fix\tools\Mono.Cecil.dll`. Getestet mit Version 0.10.4:
  1. Paket herunterladen: <https://www.nuget.org/packages/Mono.Cecil/0.10.4>
     (rechts „Download package“, liefert eine `.nupkg`-Datei)
  2. `.nupkg` in `.zip` umbenennen und öffnen
  3. `lib\net40\Mono.Cecil.dll` nach `N26Fix\tools\` kopieren

## Kompilieren

PowerShell im Ordner `N26Fix` öffnen und ausführen:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Für eine andere Installation: `-AppDir "D:\Pfad\zu\TopBanking4"`.

Das Skript

1. kompiliert `src\N26Throttle.cs` gegen die `Subsembly.Interweb.dll` und `Subsembly.Json.dll`
   deiner Installation zu `build\Subsembly.Scraper.N26Fix.dll`,
2. kompiliert `src\Patcher.cs` zu `tools\Patcher.exe`,
3. patcht die Original-`Subsembly.Scraper.dll` und schreibt das Ergebnis nach
   `build\Subsembly.Scraper.dll`. Gibt es schon ein Backup (`.orig`), dient dieses als Quelle.

Die Installation selbst wird dabei **nicht** verändert. Die Ausgabe sieht so aus:

```
  gepatcht: OnContinueLogin @ IL_01bc
  gepatcht: OnContinueLogin @ IL_0220
  gepatcht: OnDownloadStatement @ IL_0060
  gepatcht: OnDownloadDocumentList @ IL_0035
  gepatcht: _GetAccoutHolder @ IL_000e
  gepatcht: _ParseSpaceEntry @ IL_00b6
6 Aufrufe umgeleitet -> ...\build\Subsembly.Scraper.dll
```

### Optional: erst gefahrlos testen

Du kannst den Fix ausprobieren, ohne deine Installation anzufassen:

1. Den Ordner `C:\Program Files (x86)\TopBanking4` komplett kopieren, z. B. nach `D:\TopBanking4-Test`.
2. Die beiden DLLs aus `N26Fix\build\` in die Kopie legen und dabei überschreiben.
3. Von deinem Tresor (`.sub`) eine Kopie anlegen und mit `D:\TopBanking4-Test\TopBanking.exe` öffnen.

## Installieren

1. **Banking4 komplett schließen.**
2. **PowerShell als Administrator** öffnen. Das ist nötig, weil `C:\Program Files (x86)` geschützt ist.
3. In den Ordner `N26Fix` wechseln und ausführen:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Das Skript

1. bricht ab, solange Banking4 noch läuft,
2. sichert die Original-DLL als `Subsembly.Scraper.dll.orig`, sofern die installierte DLL noch
   ungepatcht ist,
3. baut alles über `build.ps1` frisch aus dem Quellcode,
4. kopiert `Subsembly.Scraper.dll` (gepatcht) und `Subsembly.Scraper.N26Fix.dll` nach Banking4.

Danach Banking4 starten und N26 wie gewohnt abrufen. Ob der Fix aktiv ist, zeigt die erste Zeile
im [Live-Log](#live-log): `=== N26Fix v3 aktiv`.

## Deinstallation

Banking4 schließen, dann in einer Administrator-PowerShell im Ordner `N26Fix` ausführen:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
```

Das Skript stellt `Subsembly.Scraper.dll` aus dem Backup wieder her und löscht
`Subsembly.Scraper.N26Fix.dll` und das Backup.

## Nach einem Banking4-Update

Ein Update überschreibt die gepatchte DLL. Das merkst du daran, dass wieder `429 Too Many Requests`
im Protokoll steht oder das Live-Log nicht mehr geschrieben wird.

**Lösung:** `install.ps1` einfach erneut ausführen, wie unter [Installieren](#installieren).

- Das Skript erkennt, dass die installierte DLL wieder ungepatcht ist. Es ersetzt das alte
  `.orig`-Backup durch die neue Original-DLL und patcht diese. Eine veraltete Version wird so nie
  zurückgespielt.
- Hat Subsembly den N26-Code grundlegend geändert, meldet der Patcher
  `Keine Aufrufstellen gefunden` und ändert nichts. Dann muss der Fix angepasst werden.
  Vielleicht hat Subsembly das Problem dann aber auch selbst behoben.

## Einstellungen

Optional, über Benutzer-Umgebungsvariablen. Sie werden beim Start von Banking4 gelesen,
Banking4 also danach neu starten.

| Variable | Standard | Bedeutung |
|---|---|---|
| `N26FIX_MIN_INTERVAL_MS` | `500` | Mindestabstand zwischen zwei N26-Anfragen in Millisekunden |
| `N26FIX_MAX_RETRIES` | `8` | Wiederholungen bei `429`/`502`/`503`, bevor aufgegeben wird |
| `N26FIX_SKIP_DETAILS` | `0` | `1` = keine Detail-Anfragen pro Buchung (sehr schnell, aber ohne Verwendungszweck, IBAN usw.) |

Setzen, z. B. für einen schnellen Import ohne Details:

```powershell
setx N26FIX_SKIP_DETAILS 1
```

Wieder entfernen:

```powershell
reg delete HKCU\Environment /v N26FIX_SKIP_DETAILS /f
```

## Live-Log

Banking4 zeigt sein Online-Protokoll erst **nach** Ende der Übertragung. Der Fix schreibt deshalb
zusätzlich nach:

```
%TEMP%\TopBanking4\N26Fix.log
```

Das ist meist `C:\Users\<Name>\AppData\Local\Temp\TopBanking4\N26Fix.log`.

Live mitlesen:

```powershell
Get-Content "$env:TEMP\TopBanking4\N26Fix.log" -Wait -Tail 20
```

Bedeutung der Zeilen:

| Zeile | Bedeutung |
|---|---|
| `=== N26Fix v3 aktiv (...)` | Der Fix ist geladen, mit den aktuellen Einstellungen |
| `Timeline-Seite N: X Eintraege, zurueck bis TT.MM.JJJJ` | Fortschritt. Ist das Datum vor deinem Startdatum, ist der Abruf gleich fertig |
| `N26Fix: N Anfragen, M aus Cache, Kx HTTP 429, Abstand jetzt …` | Zwischenstand, alle 25 Anfragen |
| `N26Fix: HTTP 429 nach N Anfragen - warte Xs, Versuch a/b` | N26 bremst, der Fix wartet. Das ist **normal** |
| `N26Fix: Details fuer Buchung nicht abrufbar …` | Eine Buchung wurde ohne Zusatzinfos importiert |

Das Log wächst mit jedem Abruf und kann jederzeit gelöscht werden.

## Datenschutz

- Das Live-Log enthält **nur** Uhrzeiten, Zähler, HTTP-Statuscodes und Datumsangaben. Es enthält
  **keine** Beträge, Namen, IBANs, Verwendungszwecke oder Zugangsdaten.
- Der Cache liegt ausschließlich im Arbeitsspeicher und ist beim Schließen von Banking4 weg. Auf
  die Festplatte wird nichts zusätzlich geschrieben. Dein Tresor bleibt wie gewohnt verschlüsselt.
- Der Fix baut keine eigenen Verbindungen auf. Er nutzt nur die Anfragen, die Banking4 ohnehin an
  `api.tech26.de` stellt, mit deren Anmeldung.

## Fehlerbehebung

| Symptom | Lösung |
|---|---|
| `tools\Mono.Cecil.dll fehlt` | Siehe [Voraussetzungen](#voraussetzungen) |
| `Bitte Banking4 zuerst schliessen.` | Banking4 beenden. Auch im Infobereich nachsehen, ob es noch läuft |
| `Zugriff verweigert` beim Installieren | PowerShell **als Administrator** starten |
| `Bereits gepatcht.` / `… kein Backup (.orig)` | Die installierte DLL ist schon gepatcht, aber das Original-Backup fehlt. Banking4 reparieren oder neu installieren, dann `install.ps1` erneut ausführen |
| `Keine Aufrufstellen gefunden` | Die Banking4-Version unterscheidet sich zu stark, der Fix muss angepasst werden |
| Abruf dauert sehr lange | Normal bei alter Historie, siehe [Der erste Abruf kann lange dauern](#-wichtig-der-erste-abruf-kann-lange-dauern). Fortschritt im [Live-Log](#live-log) prüfen |
| Es wird kein Live-Log geschrieben | Der Fix ist nicht aktiv, etwa nach einem Update. `install.ps1` erneut ausführen |

---

*Dieses Repository enthält bewusst **keine** gepatchte `Subsembly.Scraper.dll`. Sie ist
urheberrechtlich geschützte Software der Subsembly GmbH und wird auf deinem Rechner aus deiner
eigenen Installation erzeugt.*
