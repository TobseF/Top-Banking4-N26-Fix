// Subsembly.Scraper.N26Fix - Drosselung, Retry und Cache fuer den N26-Abruf in Banking4.
// Ersetzt im gepatchten Subsembly.Scraper.dll alle Aufrufe von
//   HttpClient.HttpGetJson(url, query, headers)
// innerhalb von ScraperSessionN26 durch N26Throttle.GetJson(client, url, query, headers).
//
// Warum: Banking4 laedt die N26-Timeline (neueste zuerst) und fragt fuer JEDE Buchung
// einzeln Details ab. Fuer einen alten Zeitraum werden so alle Buchungen bis heute
// angefragt - und das fuer jeden Zeitblock erneut. Ohne Bremse -> HTTP 429.
//
// Einstellbar per Umgebungsvariablen (optional):
//   N26FIX_MIN_INTERVAL_MS  Mindestabstand zwischen zwei N26-Anfragen (Default 500)
//   N26FIX_MAX_RETRIES      Wiederholungen bei 429 (Default 8)
//   N26FIX_SKIP_DETAILS=1   Detailabfragen pro Buchung ganz weglassen (schnell, weniger Infos)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Subsembly.Interweb;
using Subsembly.Json;

namespace Subsembly.Scraper.N26Fix
{
    public static class N26Throttle
    {
        const string DETAIL_MARK = "/transactions?externalId=";
        const string TIMELINE_MARK = "/timeline";

        static readonly object g_aLock = new object();
        static DateTime g_tLastRequest = DateTime.MinValue;
        static int g_nPenaltyMs;
        static int g_nDetailFailures;
        static DateTime g_tDetailRetryAfter = DateTime.MinValue;

        // Statistik fuer die Fortschrittsanzeige im Online-Protokoll
        static int g_nRequests, g_nCacheHits, g_n429, g_nPages;
        static bool g_fStarted;

        // Cache (nur im Arbeitsspeicher, solange Banking4 laeuft):
        //  - Buchungsdetails aendern sich nicht -> unbegrenzt
        //  - Timeline-Seiten -> 30 Minuten, damit mehrere Zeitbloecke eines Abrufs
        //    nicht jedes Mal erneut von heute zurueckblaettern muessen
        static readonly Dictionary<string, JsonValue> g_aDetailCache = new Dictionary<string, JsonValue>();
        static readonly Dictionary<string, KeyValuePair<DateTime, JsonValue>> g_aPageCache = new Dictionary<string, KeyValuePair<DateTime, JsonValue>>();
        static readonly TimeSpan PAGE_TTL = TimeSpan.FromMinutes(30);

        static int MinIntervalMs { get { return _Env("N26FIX_MIN_INTERVAL_MS", 500); } }
        static int MaxRetries { get { return _Env("N26FIX_MAX_RETRIES", 8); } }
        static bool SkipDetails { get { return _Env("N26FIX_SKIP_DETAILS", 0) != 0; } }

        public static JsonValue GetJson(HttpClient aClient, string sUrl, UrlQueryParams aQuery, HttpHeader[] vHeaders)
        {
            string sKey = sUrl ?? "";
            bool fDetail = sKey.IndexOf(DETAIL_MARK, StringComparison.OrdinalIgnoreCase) >= 0;
            bool fTimeline = !fDetail && aQuery == null && sKey.IndexOf("/api/feed/accounts/", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!g_fStarted)
            {
                g_fStarted = true;
                _Log("=== N26Fix v3 aktiv (Abstand " + MinIntervalMs + " ms, Details " + (SkipDetails ? "AUS" : "an") + ")");
            }

            if (fDetail && SkipDetails)
            {
                return null;
            }

            // Cache
            lock (g_aLock)
            {
                JsonValue jsCached;
                if (fDetail && g_aDetailCache.TryGetValue(sKey, out jsCached))
                {
                    ++g_nCacheHits;
                    return jsCached;
                }
                KeyValuePair<DateTime, JsonValue> aPage;
                if (fTimeline && g_aPageCache.TryGetValue(sKey, out aPage) && DateTime.UtcNow - aPage.Key < PAGE_TTL)
                {
                    ++g_nCacheHits;
                    return aPage.Value;
                }
            }

            // Wenn Detailabfragen gerade mehrfach endgueltig gescheitert sind: kurz aussetzen.
            if (fDetail && g_nDetailFailures >= 3)
            {
                if (DateTime.UtcNow < g_tDetailRetryAfter)
                {
                    return null;
                }
                g_nDetailFailures = 2; // nach der Pause genau einen neuen Versuch erlauben
            }

            int nMaxRetries = MaxRetries;
            for (int nAttempt = 0; ; ++nAttempt)
            {
                _Throttle();
                try
                {
                    JsonValue jsResult = aClient.HttpGetJson(sUrl, aQuery, vHeaders);
                    lock (g_aLock)
                    {
                        ++g_nRequests;
                        // Erfolg: Strafpause langsam wieder abbauen
                        g_nPenaltyMs = Math.Max(0, g_nPenaltyMs - 20);
                        if (fDetail)
                        {
                            g_nDetailFailures = 0;
                            g_aDetailCache[sKey] = jsResult;
                        }
                        else if (fTimeline)
                        {
                            g_aPageCache[sKey] = new KeyValuePair<DateTime, JsonValue>(DateTime.UtcNow, jsResult);
                            ++g_nPages;
                            _Log("Timeline-Seite " + g_nPages + ": " + _OldestDate(jsResult));
                        }
                        if (g_nRequests % 25 == 0)
                        {
                            _Trace(aClient, "N26Fix: " + g_nRequests + " Anfragen, " + g_nCacheHits + " aus Cache, "
                                + g_n429 + "x HTTP 429, Abstand jetzt " + (MinIntervalMs + g_nPenaltyMs) + " ms");
                        }
                    }
                    return jsResult;
                }
                catch (HttpStatusException x)
                {
                    bool fRetryable = x.HttpStatus == 429 || x.HttpStatus == 503 || x.HttpStatus == 502;
                    if (x.HttpStatus == 429)
                    {
                        Interlocked.Increment(ref g_n429);
                    }
                    if (!fRetryable || nAttempt >= nMaxRetries)
                    {
                        if (fDetail)
                        {
                            // Details sind optional: Buchung ohne Zusatzinfos importieren statt abbrechen.
                            ++g_nDetailFailures;
                            g_tDetailRetryAfter = DateTime.UtcNow.AddMinutes(2);
                            _Trace(aClient, "N26Fix: Details fuer Buchung nicht abrufbar (HTTP " + x.HttpStatus + "), importiere ohne Details.");
                            return null;
                        }
                        throw;
                    }

                    // Exponentielles Backoff: 5s, 10s, 20s, 40s, dann max. 60s
                    int nWaitMs = Math.Min(60000, 5000 << Math.Min(nAttempt, 4));
                    _Trace(aClient, "N26Fix: HTTP " + x.HttpStatus + " nach " + g_nRequests + " Anfragen - warte "
                        + (nWaitMs / 1000) + "s, Versuch " + (nAttempt + 2) + "/" + (nMaxRetries + 1));
                    Thread.Sleep(nWaitMs);

                    // Nach einem 429 etwas langsamer werden (baut sich bei Erfolg wieder ab).
                    lock (g_aLock)
                    {
                        g_nPenaltyMs = Math.Min(2000, g_nPenaltyMs + 250);
                    }
                }
            }
        }

        static void _Throttle()
        {
            lock (g_aLock)
            {
                int nInterval = MinIntervalMs + g_nPenaltyMs;
                TimeSpan tElapsed = DateTime.UtcNow - g_tLastRequest;
                int nWait = nInterval - (int)tElapsed.TotalMilliseconds;
                if (nWait > 0)
                {
                    Thread.Sleep(nWait);
                }
                g_tLastRequest = DateTime.UtcNow;
            }
        }

        static void _Trace(HttpClient aClient, string sLine)
        {
            try
            {
                aClient.TraceLine(TraceLevel.Warning, sLine);
            }
            catch
            {
            }
            _Log(sLine);
        }

        // Live-Log (Banking4 zeigt sein Protokoll erst nach Abschluss an).
        // Enthaelt nur Zaehler, Statuscodes und Datumsstand - keine Betraege, Namen oder IBANs.
        public static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TopBanking4", "N26Fix.log");

        static void _Log(string sLine)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath));
                System.IO.File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + sLine + Environment.NewLine);
            }
            catch
            {
            }
        }

        // Aeltestes Buchungsdatum einer Timeline-Seite, um den Fortschritt zu zeigen.
        static string _OldestDate(JsonValue jsPage)
        {
            try
            {
                double nMin = double.MaxValue;
                int nItems = 0;
                List<JsonValue> vLists = new List<JsonValue>();
                JsonArray jsModules = jsPage["modules"] as JsonArray;
                if (jsModules != null)
                {
                    foreach (JsonValue jsMod in jsModules.Value)
                    {
                        if (jsMod != null) vLists.Add(jsMod["items"]);
                    }
                }
                vLists.Add(jsPage["items"]);
                foreach (JsonValue jsList in vLists)
                {
                    JsonArray jsItems = jsList as JsonArray;
                    if (jsItems == null) continue;
                    foreach (JsonValue jsItem in jsItems.Value)
                    {
                        if (jsItem == null) continue;
                        JsonValue jsTs = jsItem["timestamp"];
                        if (jsTs == null) continue;
                        double n = (double)jsTs;
                        ++nItems;
                        if (n > 0 && n < nMin) nMin = n;
                    }
                }
                if (nItems == 0) return "0 Eintraege";
                return nItems + " Eintraege, zurueck bis " + JsonDate.SetTime(nMin).ToString("dd.MM.yyyy");
            }
            catch (Exception x)
            {
                return "(Datum unbekannt: " + x.GetType().Name + ")";
            }
        }

        static int _Env(string sName, int nDefault)
        {
            try
            {
                string s = Environment.GetEnvironmentVariable(sName);
                int n;
                if (!string.IsNullOrEmpty(s) && int.TryParse(s, out n))
                {
                    return n;
                }
            }
            catch
            {
            }
            return nDefault;
        }
    }
}
