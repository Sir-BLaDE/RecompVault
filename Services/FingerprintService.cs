using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using RecompVault.Core;
using RecompVault.Models;

namespace RecompVault.Services;

public sealed record FingerprintCandidate(string Provider, string Repository, string? AssetFilter, string? QuiverFolderName, string Origin);

public sealed record FingerprintResult(bool Matched, string Method, FingerprintCandidate? Candidate, string? Version, string? AssetName, string Message);

/// <summary>
/// Verificación "por huella": confirma repositorio y versión comparando lo instalado con los releases.
/// 1) SHA-256 de installed-build.txt (Quiver) frente al digest que publica GitHub para cada paquete.
/// 2) CRC32 + tamaño del ejecutable frente al índice del .zip de cada release, leyendo solo el final
///    del zip con peticiones parciales (Range), sin descargar el paquete.
/// </summary>
public static class FingerprintService
{
    private static readonly HttpClient Http = CreateHttp();
    private const int MaxReleases = 25;
    private const long MaxCentralDirectory = 16L * 1024 * 1024;

    // cachés de sesión (útiles al verificar en bloque)
    private static readonly Dictionary<string, List<UpdateService.ReleaseInfo>> ReleaseCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<ZipEntryInfo>?> ZipCache = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = 6 })
        { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("RecompVault/" + SelfUpdateService.CurrentVersion + " (fingerprint)");
        return h;
    }

    /// <summary>Candidatos: el vínculo actual y, si no hay o se pide, los de Quiver con pruebas fuertes.</summary>
    public static List<FingerprintCandidate> CandidatesFor(GameEntry g, QuiverLibrary? lib, bool includeSuggestions)
    {
        var list = new List<FingerprintCandidate>();
        if (!string.IsNullOrWhiteSpace(g.Repository))
            list.Add(new FingerprintCandidate(g.Provider, g.Repository!, g.AssetFilter, g.QuiverFolderName, "vínculo actual"));
        if (lib != null && (includeSuggestions || list.Count == 0))
        {
            foreach (var a in QuiverMatcher.Candidates(lib, g.FolderPath, g.Name, 3))
            {
                if (list.Any(c => string.Equals(c.Repository, a.Repository, StringComparison.OrdinalIgnoreCase) && c.AssetFilter == a.AssetFilter)) continue;
                list.Add(new FingerprintCandidate(a.Provider, a.Repository, a.AssetFilter.Length > 0 ? a.AssetFilter : null, a.FolderName, "candidato de Quiver: " + a.Name));
            }
        }
        return list;
    }

    public static async Task<FingerprintResult> VerifyAsync(GameEntry g, IReadOnlyList<FingerprintCandidate> candidates, AppConfig cfg,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (candidates.Count == 0)
            return new FingerprintResult(false, "", null, null, null, "No hay ningún repositorio con el que comparar (vincúlalo en ℹ o sincroniza con Quiver).");

        var sha = InstalledSha(g.FolderPath);
        string? exe = !string.IsNullOrWhiteSpace(g.LaunchPath) && File.Exists(g.LaunchPath) && PathUtil.IsSameOrInside(g.LaunchPath, g.FolderPath) ? g.LaunchPath : null;
        uint exeCrc = 0; long exeSize = 0;
        if (exe != null)
        {
            progress?.Report($"Calculando la huella de {Path.GetFileName(exe)}...");
            exeSize = new FileInfo(exe).Length;
            exeCrc = await Task.Run(() => ZipIndex.FileCrc32(exe), ct).ConfigureAwait(false);
        }
        if (sha == null && exe == null)
            return new FingerprintResult(false, "", null, null, null, "No hay installed-build.txt ni ejecutable con el que comparar.");

        var notes = new List<string>();
        foreach (var cand in candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Leyendo releases de {cand.Repository}...");
            var releases = await GetReleasesAsync(cand, cfg, ct).ConfigureAwait(false);
            if (releases == null) { notes.Add($"{cand.Repository}: no se pudieron leer los releases"); continue; }
            if (releases.Count == 0) { notes.Add($"{cand.Repository}: sin releases"); continue; }

            // 1) SHA-256 del paquete instalado (Quiver) frente al digest de GitHub
            if (sha != null)
            {
                foreach (var r in releases)
                {
                    var a = r.Assets.FirstOrDefault(x => x.Digest != null && x.Digest.Equals("sha256:" + sha, StringComparison.OrdinalIgnoreCase));
                    if (a != null)
                        return new FingerprintResult(true, "SHA-256 del paquete", cand, r.Tag, a.Name,
                            $"El paquete instalado ({a.Name}) es idéntico al del release {r.Tag} de {cand.Repository}.");
                }
            }

            // 2) CRC32 del ejecutable frente al índice de cada zip
            if (exe != null)
            {
                var exeName = Path.GetFileName(exe);
                int checkedZips = 0;
                foreach (var r in releases.Take(MaxReleases))
                {
                    var zips = r.Assets.Where(x => x.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                                   ReleaseAssetPicker.MatchesFilter(x.Name, cand.AssetFilter) &&
                                                   ReleaseAssetPicker.Score(x.Name) >= 0)
                                       .OrderByDescending(x => ReleaseAssetPicker.Score(x.Name)).Take(3);
                    foreach (var z in zips)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report($"Comparando con {cand.Repository} {r.Tag} ({z.Name})...");
                        var entries = await ReadZipIndexAsync(z.Url, ct).ConfigureAwait(false);
                        checkedZips++;
                        if (entries == null) continue;
                        if (ZipIndex.FindMatch(entries, exeName, exeSize, exeCrc) is { } hit)
                            return new FingerprintResult(true, "CRC32 del ejecutable", cand, r.Tag, z.Name,
                                $"{exeName} es idéntico al de {z.Name} ({hit.Name}) del release {r.Tag} de {cand.Repository}.");
                    }
                }
                notes.Add($"{cand.Repository}: {exeName} no coincide con ninguno de {checkedZips} paquetes .zip");
            }
            else notes.Add($"{cand.Repository}: el SHA-256 instalado no coincide con ningún paquete publicado");
        }
        return new FingerprintResult(false, "", null, null, null, "Sin coincidencias. " + string.Join(" · ", notes));
    }

    /// <summary>SHA-256 del paquete que instaló Quiver (2.ª línea de installed-build.txt) o null.</summary>
    public static string? InstalledSha(string folder)
    {
        try
        {
            var p = Path.Combine(folder, "installed-build.txt");
            if (!File.Exists(p)) return null;
            var lines = File.ReadAllLines(p).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            return lines.Length >= 2 && lines[1].Length == 64 && lines[1].All(Uri.IsHexDigit) ? lines[1].ToLowerInvariant() : null;
        }
        catch { return null; }
    }

    private static async Task<List<UpdateService.ReleaseInfo>?> GetReleasesAsync(FingerprintCandidate c, AppConfig cfg, CancellationToken ct)
    {
        var key = c.Provider + ":" + c.Repository;
        lock (ReleaseCache) if (ReleaseCache.TryGetValue(key, out var cached)) return cached;
        var (list, error) = await UpdateService.ListReleasesAsync(c.Provider, c.Repository, cfg, ct).ConfigureAwait(false);
        if (error != null && list.Count == 0) { Log.Warn($"Huella: {c.Repository}: {error}"); return null; }
        lock (ReleaseCache) ReleaseCache[key] = list;
        return list;
    }

    /// <summary>Índice del zip remoto leyendo solo su final (Range). Null si el servidor no lo permite.</summary>
    private static async Task<List<ZipEntryInfo>?> ReadZipIndexAsync(string url, CancellationToken ct)
    {
        lock (ZipCache) if (ZipCache.TryGetValue(url, out var c)) return c;
        List<ZipEntryInfo>? result = null;
        try
        {
            var (tail, total) = await GetRangeAsync(url, null, 65536, ct).ConfigureAwait(false);
            if (tail != null && total > 0)
            {
                var loc = ZipIndex.FindCentralDirectory(tail, total);
                if (loc != null && loc.Size <= MaxCentralDirectory)
                {
                    byte[] cd;
                    var tailStart = total - tail.Length;
                    if (loc.Offset >= tailStart)
                        cd = tail[(int)(loc.Offset - tailStart)..(int)(loc.Offset - tailStart + loc.Size)];
                    else
                    {
                        var (part, _) = await GetRangeAsync(url, loc.Offset, loc.Size, ct).ConfigureAwait(false);
                        cd = part ?? Array.Empty<byte>();
                    }
                    if (cd.Length > 0) result = ZipIndex.ParseCentralDirectory(cd);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Warn($"Huella: índice de {url}: {ex.Message}"); }
        lock (ZipCache) ZipCache[url] = result;
        return result;
    }

    /// <summary>Bytes de un rango (start null = los últimos <paramref name="length"/>) y tamaño total del fichero.</summary>
    private static async Task<(byte[]? data, long total)> GetRangeAsync(string url, long? start, long length, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = start == null ? new RangeHeaderValue(null, length) : new RangeHeaderValue(start, start + length - 1);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.PartialContent) return (null, 0); // sin soporte de Range: no se descarga entero
        var total = resp.Content.Headers.ContentRange?.Length ?? 0;
        var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return (data, total);
    }

    /// <summary>Aplica un resultado positivo: vínculo verificado (seguro) y versión real en version.txt.</summary>
    public static void Apply(GameEntry g, FingerprintResult r, AppConfig cfg)
    {
        if (!r.Matched || r.Candidate == null) return;
        var c = r.Candidate;
        ConfigService.Update(cfg, conf => conf.LinkOverrides[g.FolderPath] = new LinkOverride
        {
            QuiverFolderName = c.QuiverFolderName,
            Repository = c.Repository, Provider = c.Provider, AssetFilter = c.AssetFilter ?? "",
            Verified = true, VerifiedNote = $"{r.Method}: {r.Version} ({r.AssetName})"
        });
        try
        {
            if (!string.IsNullOrWhiteSpace(r.Version) && !VersionUtil.Equivalent(UpdateService.ReadInstalledVersion(g.FolderPath), r.Version))
                File.WriteAllText(Path.Combine(g.FolderPath, "version.txt"), r.Version);
        }
        catch (Exception ex) { Log.Warn($"Huella: no se pudo escribir version.txt en {g.FolderPath}: {ex.Message}"); }
        Log.Info($"Huella verificada {g.Name}: {r.Message}");
    }
}
