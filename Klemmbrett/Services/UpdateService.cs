using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace Klemmbrett.Services;

/// <summary>
/// Update-Check und Self-Update gegen die Releases der eigenen Forge (Kroste-Standard, siehe
/// autoupdate-Referenz): proxy-aware, nicht blockierend, max. 1 Check pro
/// App-Start, nie silent installieren. Download + Austausch mit Nutzer-Zustimmung.
/// </summary>
public sealed class UpdateService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    // **Die eigene Forge statt GitHub** (Lars am 28.10.2026: GitHub wird
    // abgeschaltet). Fuer die Dienstrechner zweitrangig -- sie laden nichts
    // aus dem Internet und holen ihre Updates aus dem Samba-Share.
    private const string ReleasesUrl = "https://git.flexfamily.de/api/v1/repos/LHP542/Klemmbrett-Scaffold/releases/latest";

    private readonly HttpClient _http;
    private UpdateCheckResult? _cached;

    public UpdateService()
    {
        // System-Proxy + Negotiate: läuft identisch auf Arbeitslaptop und Bazzite
        var handler = new HttpClientHandler
        {
            Proxy = WebRequest.DefaultWebProxy,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Klemmbrett-UpdateCheck"); // GitHub-API-Pflicht
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version CurrentVersion =>
        ParseVersion(Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new Version(0, 0, 0);

    /// <summary>Tag wie "v1.2.3", "1.2.3+abc123" oder "1.2.3-alpha.1" → Version. Null bei Müll.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['+', '-']);
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var v)) return null;
        // Fehlende Build-Stelle auf 0 normalisieren: sonst gilt "1.2" (Build=-1)
        // fälschlich als älter als "1.2.0" (Build=0) und ein Update wird verpasst.
        return v.Build < 0 ? new Version(v.Major, v.Minor, 0) : v;
    }

    /// <summary>
    /// Fehler werden nur geloggt (Warn) — offline/Proxy darf die App nicht stören.
    /// <paramref name="forceRefresh"/>=true umgeht den Cache (für den manuellen
    /// „Auf Updates prüfen"-Knopf) und fragt GitHub erneut ab; der automatische
    /// Start-Check bleibt bei max. 1 echtem Check pro App-Start.
    /// </summary>
    public async Task<UpdateCheckResult?> CheckForUpdateAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _cached is not null) return _cached; // max. 1 echter Check pro App-Start

        var sw = Stopwatch.StartNew();
        try
        {
            Log.Debug("Update-Check: {Url}", ReleasesUrl);
            using var response = await _http.GetAsync(ReleasesUrl, ct);
            response.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var root = json.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagEl))
            {
                Log.Warn("Update-Check: GitHub-Antwort ohne 'tag_name'");
                return null;
            }
            var tag = tagEl.GetString();
            var htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? "" : "";

            var latest = ParseVersion(tag);
            if (latest is null)
            {
                Log.Warn("Update-Check: Tag '{Tag}' nicht parsebar", tag);
                return null;
            }

            // Passendes Asset für die laufende Plattform heraussuchen
            var (assetName, assetUrl) = SelectAsset(root);

            _cached = new UpdateCheckResult(CurrentVersion, latest, latest > CurrentVersion,
                htmlUrl, assetName, assetUrl);
            Log.Info("Update-Check fertig in {Ms} ms: aktuell {Current}, neueste {Latest}, Update: {Available}, Asset: {Asset}",
                sw.ElapsedMilliseconds, _cached.Current, _cached.Latest, _cached.UpdateAvailable, assetName ?? "—");
            return _cached;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Update-Check fehlgeschlagen nach {Ms} ms (offline/Proxy?)", sw.ElapsedMilliseconds);
            return null;
        }
    }

    /// <summary>Wählt aus den Release-Assets das für die laufende Plattform passende
    /// (win-x64.zip unter Windows, x86_64.AppImage bzw. linux-x64.tar.gz unter Linux).</summary>
    private static (string? name, string? url) SelectAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, null);

        bool Match(string name) => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            : name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)
              || (name.Contains("linux-x64", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase));

        foreach (var a in assets.EnumerateArray())
        {
            if (!a.TryGetProperty("name", out var nameEl)) continue;
            var name = nameEl.GetString();
            if (name is not null && Match(name)
                && a.TryGetProperty("browser_download_url", out var urlEl))
                return (name, urlEl.GetString());
        }
        return (null, null);
    }

    /// <summary>
    /// Lädt das Update-Asset herunter, entpackt es neben die laufende App und startet
    /// einen Austausch-Prozess, der die App beendet, die Dateien ersetzt und neu startet.
    /// Gibt false zurück, wenn kein Self-Update möglich ist (dann Release-Seite öffnen).
    /// </summary>
    public async Task<bool> DownloadAndApplyAsync(UpdateCheckResult update,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (update.AssetUrl is null || update.AssetName is null)
        {
            Log.Warn("Kein passendes Update-Asset für diese Plattform — Self-Update nicht möglich");
            return false;
        }

        try
        {
            var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var work = Path.Combine(Path.GetTempPath(), "Klemmbrett-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var assetPath = Path.Combine(work, update.AssetName);

            Log.Info("Lade Update herunter: {Asset}", update.AssetName);
            await DownloadWithProgressAsync(update.AssetUrl, assetPath, progress, ct);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return ApplyWindows(assetPath, work, appDir);
            return ApplyLinux(assetPath, appDir);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Self-Update fehlgeschlagen");
            return false;
        }
    }

    private async Task DownloadWithProgressAsync(string url, string dest,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1L;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }
        Log.Debug("Download fertig: {Bytes} Bytes", read);
    }

    /// <summary>Windows: ZIP daneben entpacken, .bat schreibt nach App-Ende die Dateien um und startet neu.</summary>
    private bool ApplyWindows(string zipPath, string work, string appDir)
    {
        var extract = Path.Combine(work, "extracted");
        ZipFile.ExtractToDirectory(zipPath, extract);

        var pid = Environment.ProcessId;
        var exe = Path.Combine(appDir, "Klemmbrett.exe");
        var bat = Path.Combine(work, "apply.bat");
        var log = Path.Combine(work, "update.log");

        // WICHTIG: Batch-Zeilen OHNE führende Einrückung schreiben — ein eingerücktes
        // ":wait"-Label ist für cmd.exe kein gültiges Sprungziel, dann läuft xcopy
        // los, während die alte App die Dateien noch sperrt (→ alte Version startet neu).
        // /WAIT auf den PID-Prozess ist zuverlässiger als eine tasklist-Schleife.
        var lines = new[]
        {
            "@echo off",
            $"echo Warte auf Prozess {pid} >\"{log}\"",
            // Auf das Ende des alten Prozesses warten (blockiert, bis PID weg ist):
            $"powershell -NoProfile -Command \"try {{ Wait-Process -Id {pid} -ErrorAction Stop }} catch {{}}\" >>\"{log}\" 2>&1",
            // Kurzer Nachlauf, damit Dateihandles sicher freigegeben sind:
            "ping 127.0.0.1 -n 2 >NUL",
            $"echo Kopiere Dateien >>\"{log}\"",
            $"xcopy /E /Y /I /Q \"{extract}\\*\" \"{appDir}\\\" >>\"{log}\" 2>&1",
            $"echo Starte neu >>\"{log}\"",
            $"start \"\" \"{exe}\"",
            // work-Ordner NICHT löschen: enthält das Log für die Fehlersuche.
        };
        File.WriteAllLines(bat, lines);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{bat}\"\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = work
        });
        Log.Info("Windows-Update vorbereitet (Skript {Bat}) — App wird für den Austausch beendet", bat);
        return true;
    }

    /// <summary>Linux: AppImage ersetzt sich selbst (eine Datei), tar.gz wird entpackt; Neustart via sh.</summary>
    private bool ApplyLinux(string assetPath, string appDir)
    {
        var runningAppImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var pid = Environment.ProcessId;
        var sh = Path.Combine(Path.GetTempPath(), $"klemmbrett-update-{Guid.NewGuid():N}.sh");

        string body;
        if (assetPath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase) && runningAppImage is not null)
        {
            // Laufendes AppImage ersetzen. mv/rm kann fehlschlagen, solange die Datei
            // als Loop-Device gemountet ist — deshalb erst warten, dann per cp den
            // Inhalt überschreiben (Inode bleibt, kein "Text file busy").
            body = string.Join('\n',
                "#!/bin/sh",
                $"while kill -0 {pid} 2>/dev/null; do sleep 1; done",
                "sleep 1",
                $"chmod +x '{assetPath}'",
                $"cp -f '{assetPath}' '{runningAppImage}' || mv -f '{assetPath}' '{runningAppImage}'",
                $"rm -f '{assetPath}'",
                $"setsid '{runningAppImage}' >/dev/null 2>&1 &");
        }
        else if (assetPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            var exe = Path.Combine(appDir, "Klemmbrett");
            body = string.Join('\n',
                "#!/bin/sh",
                $"while kill -0 {pid} 2>/dev/null; do sleep 1; done",
                "sleep 1",
                $"tar -xzf '{assetPath}' -C '{appDir}'",
                $"chmod +x '{exe}'",
                $"setsid '{exe}' >/dev/null 2>&1 &");
        }
        else
        {
            Log.Warn("Linux-Update: unerwartetes Asset ({Asset}) oder kein laufendes AppImage", Path.GetFileName(assetPath));
            return false;
        }

        File.WriteAllText(sh, body);
        Process.Start(new ProcessStartInfo("/bin/sh", $"\"{sh}\"") { UseShellExecute = false });
        Log.Info("Linux-Update vorbereitet — App wird für den Austausch beendet");
        return true;
    }
}

public sealed record UpdateCheckResult(
    Version Current, Version Latest, bool UpdateAvailable, string ReleaseUrl,
    string? AssetName = null, string? AssetUrl = null);
