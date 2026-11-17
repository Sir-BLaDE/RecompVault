using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using RecompVault.Models;

namespace RecompVault.Services;

public static class UpdateService
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "RecompVault/1.0");
        try
        {
            var token = ConfigService.Load().GitHubToken;
            if (!string.IsNullOrWhiteSpace(token))
                h.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + token.Trim());
        }
        catch { }
        return h;
    }

    public static async Task<bool> CheckUpdateAsync(GameEntry g, string quiverPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(quiverPath))
            {
                var versions = QuiverIntegrationService.LoadVersionCache(quiverPath);
                QuiverIntegrationService.ApplyUpdateInfo(g, versions);
            }

            // Try GitHub to get/refresh latest version and a Windows asset (fuente más fiable que cachés potencialmente obsoletas)
            if (!string.IsNullOrWhiteSpace(g.Repository))
            {
                var repo = g.Repository.Trim();
                var url = $"https://api.github.com/repos/{repo}/releases/latest";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "RecompVault/1.0");
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        // GitHub es la fuente más fiable para la última versión publicada
                        var prevLatest = g.LatestVersion;
                        g.LatestVersion = tag;

                        if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            if (string.IsNullOrWhiteSpace(g.UpdateAssetUrl)) PickWindowsAsset(g, assets);
                        }

                        // Priorizar siempre la versión instalada real en disco (evita reofrecer updates ya aplicados)
                        var inst = QuiverIntegrationService.ReadInstalledVersion(g.FolderPath);
                        if (!string.IsNullOrWhiteSpace(inst))
                            g.InstalledVersion = inst;
                        else if (string.IsNullOrWhiteSpace(g.InstalledVersion) && !string.IsNullOrWhiteSpace(prevLatest) && !QuiverIntegrationService.SameVersion(prevLatest, tag))
                            g.InstalledVersion = prevLatest; // baseline conocida anterior
                    }
                }
                else
                {
                    Log($"GitHub check failed {repo}: {(int)resp.StatusCode}");
                }
            }
        }
        catch (Exception ex) { Log($"CheckUpdate error {g.Name}: {ex.Message}"); }
        return g.HasUpdate;
    }

    private static void PickWindowsAsset(GameEntry g, System.Text.Json.JsonElement assets)
    {
        System.Text.Json.JsonElement? best = null; int bestScore = -999;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var an) ? (an.GetString() ?? "") : "";
            var lower = name.ToLowerInvariant();
            int score = 0;
            bool isWin = lower.Contains("windows") || lower.Contains("win64") || lower.Contains("win-x64") || lower.Contains("win32") || lower.Contains("-win-") || lower.Contains("_win") || lower.Contains(".exe");
            bool bad = lower.Contains("linux") || lower.Contains("android") || lower.Contains("macos") || lower.Contains("osx") || lower.Contains("darwin") || lower.Contains("switch") || lower.Contains("psp") || lower.Contains("vita");
            if (isWin) score += 50;
            if (bad) score -= 100;
            if (lower.EndsWith(".zip")) score += 20;
            if (lower.EndsWith(".exe")) score += 10;
            if (lower.EndsWith(".7z") || lower.EndsWith(".rar") || lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz")) score -= 5;
            if (score > bestScore) { bestScore = score; best = a; }
        }
        best ??= assets.GetArrayLength() > 0 ? assets[0] : (System.Text.Json.JsonElement?)null;
        if (best.HasValue && bestScore > -50)
        {
            var b = best.Value;
            g.UpdateAssetName = b.TryGetProperty("name", out var bn) ? (bn.GetString() ?? "") : "";
            g.UpdateAssetUrl = b.TryGetProperty("browser_download_url", out var bu) ? (bu.GetString() ?? "") : "";
            g.UpdateAssetDigest = b.TryGetProperty("digest", out var dg) ? (dg.GetString() ?? "") : "";
        }
    }

    private static async Task TryRefreshAssetFromGitHubAsync(GameEntry g)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(g.Repository)) return;
            var repo = g.Repository.Trim();
            var url = $"https://api.github.com/repos/{repo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "RecompVault/1.0");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) { Log($"Refresh asset failed {repo}: {(int)resp.StatusCode}"); return; }
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            if (!string.IsNullOrWhiteSpace(tag)) g.LatestVersion = tag;
            if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == System.Text.Json.JsonValueKind.Array)
                PickWindowsAsset(g, assets);
        }
        catch (Exception ex) { Log($"Refresh asset error {g.Name}: {ex.Message}"); }
    }

    public static async Task<bool> UpdateAsync(GameEntry g, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(g.UpdateAssetUrl) && !string.IsNullOrWhiteSpace(g.Repository))
        {
            progress?.Report($"Buscando asset de Windows para {g.Name}...");
            await TryRefreshAssetFromGitHubAsync(g);
        }
        if (string.IsNullOrWhiteSpace(g.UpdateAssetUrl))
        {
            progress?.Report($"No hay asset descargable para {g.Name}.");
            Log($"No asset URL: {g.Name} repo={g.Repository} latest={g.LatestVersion} hasUpdate={g.HasUpdate}");
            return false;
        }

        async Task<(bool ok, string path, string usedUrl, string usedName, string err)> DownloadOnceAsync(string url, string name, string tempRoot)
        {
            var downloadPath = Path.Combine(tempRoot, name);
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return (false, downloadPath, url, name, $"HTTP {(int)resp.StatusCode}");
                await using var fs = File.Create(downloadPath);
                await resp.Content.CopyToAsync(fs);
                return (true, downloadPath, url, name, "");
            }
            catch (Exception ex) { return (false, downloadPath, url, name, ex.Message); }
        }

        var url = g.UpdateAssetUrl!;
        var name = string.IsNullOrWhiteSpace(g.UpdateAssetName) ? Path.GetFileName(new Uri(url).AbsolutePath) : g.UpdateAssetName!;
        if (string.IsNullOrWhiteSpace(name)) name = "update.zip";
        var tempRoot = Path.Combine(Path.GetTempPath(), "RecompVaultUpdate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            progress?.Report($"Descargando {g.Name} ({g.LatestVersion})...");
            var dl = await DownloadOnceAsync(url, name, tempRoot);
            if (!dl.ok && (dl.err.Contains("404") || dl.err.Contains("403")) && !string.IsNullOrWhiteSpace(g.Repository))
            {
                progress?.Report("Asset no disponible, buscando versión actual...");
                await TryRefreshAssetFromGitHubAsync(g);
                if (!string.IsNullOrWhiteSpace(g.UpdateAssetUrl) && g.UpdateAssetUrl != url)
                {
                    url = g.UpdateAssetUrl!;
                    name = string.IsNullOrWhiteSpace(g.UpdateAssetName) ? Path.GetFileName(new Uri(url).AbsolutePath) : g.UpdateAssetName!;
                    if (string.IsNullOrWhiteSpace(name)) name = "update.zip";
                    dl = await DownloadOnceAsync(url, name, tempRoot);
                }
            }
            if (!dl.ok) throw new Exception($"Descarga fallida: {dl.err}");
            var downloadPath = dl.path;
            name = dl.usedName;

            if (!string.IsNullOrWhiteSpace(g.UpdateAssetDigest) && g.UpdateAssetDigest!.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("Verificando descarga...");
                var expected = g.UpdateAssetDigest.Substring("sha256:".Length).Trim().ToLowerInvariant();
                await using var s = File.OpenRead(downloadPath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(s)).ToLowerInvariant();
                if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("La verificación SHA256 no coincide.");
            }

            progress?.Report("Instalando actualización...");
            var extractDir = Path.Combine(tempRoot, "extract");
            Directory.CreateDirectory(extractDir);
            var lower = name.ToLowerInvariant();
            if (lower.EndsWith(".zip"))
            {
                ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
                ApplyExtracted(g.FolderPath, extractDir);
            }
            else if (lower.EndsWith(".exe") || lower.EndsWith(".msi"))
            {
                var dest = Path.Combine(g.FolderPath, name);
                File.Copy(downloadPath, dest, true);
                progress?.Report($"Descargado instalador: {name}. Ejecútalo manualmente si es necesario.");
            }
            else if (lower.EndsWith(".7z") || lower.EndsWith(".rar") || lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz"))
            {
                // cannot extract natively; keep file in folder for manual install
                var dest = Path.Combine(g.FolderPath, name);
                File.Copy(downloadPath, dest, true);
                progress?.Report($"Paquete descargado ({name}) en la carpeta del juego; requiere extracción manual.");
            }
            else
            {
                try { ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true); ApplyExtracted(g.FolderPath, extractDir); }
                catch { File.Copy(downloadPath, Path.Combine(g.FolderPath, name), true); }
            }

            if (!string.IsNullOrWhiteSpace(g.LatestVersion))
            {
                try { File.WriteAllText(Path.Combine(g.FolderPath, "version.txt"), g.LatestVersion); } catch { }
                // guardar también en formato compatible (nombre+hash/tag) para lecturas robustas
                try
                {
                    var buildInfo = (string.IsNullOrWhiteSpace(g.UpdateAssetName) ? "release" : g.UpdateAssetName) + "\n" + g.LatestVersion;
                    File.WriteAllText(Path.Combine(g.FolderPath, "installed-build.txt"), buildInfo);
                }
                catch { try { File.WriteAllText(Path.Combine(g.FolderPath, "installed-build.txt"), g.LatestVersion); } catch { } }
            }
            g.InstalledVersion = g.LatestVersion;
            var newLaunch = ScannerFindLaunch(g.FolderPath);
            if (!string.IsNullOrWhiteSpace(newLaunch)) g.LaunchPath = newLaunch;
            progress?.Report($"Actualizado: {g.Name} → {g.LatestVersion}");
            Log($"Updated {g.Name} to {g.LatestVersion} from {url}");
            return true;
        }
        catch (Exception ex)
        {
            progress?.Report($"Error actualizando {g.Name}: {ex.Message}");
            Log($"Update error {g.Name}: {ex}");
            return false;
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static void ApplyExtracted(string targetFolder, string extractDir)
    {
        // if single top-level folder, unwrap it
        var entries = Directory.GetFileSystemEntries(extractDir);
        string source = extractDir;
        if (entries.Length == 1 && Directory.Exists(entries[0]))
            source = entries[0];
        CopyAll(source, targetFolder);
    }

    private static void CopyAll(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(target, rel));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }

    private static string ScannerFindLaunch(string gameDir)
    {
        try
        {
            var rootExes = Directory.GetFiles(gameDir, "*.exe", SearchOption.TopDirectoryOnly).ToList();
            if (rootExes.Count == 0) rootExes = Directory.GetFiles(gameDir, "*.exe", SearchOption.AllDirectories).Take(50).ToList();
            if (rootExes.Count == 0)
            {
                var bat = Directory.GetFiles(gameDir, "*.bat", SearchOption.TopDirectoryOnly).FirstOrDefault();
                return bat ?? "";
            }
            string best = rootExes[0];
            long bestScore = -1;
            foreach (var f in rootExes)
            {
                var n = Path.GetFileName(f).ToLowerInvariant();
                if (n.Contains("unins") || n.Contains("setup") || n.Contains("crash")) continue;
                long score = new FileInfo(f).Length;
                if (n.Contains("launcher")) score += 1_000_000_000;
                if (score > bestScore) { bestScore = score; best = f; }
            }
            return best;
        }
        catch { return ""; }
    }

    private static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "recompvault_updates.log"), DateTime.Now.ToString("s") + " " + msg + Environment.NewLine); } catch { }
    }
}
