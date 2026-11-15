using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WildflowerDesktop;

public sealed class ThemeStore
{
    private const string FeedUrl = "https://github.com/qshanx/wildflower-desktop/releases/download/update-feed/";
    private const long MaxDownload = 20 * 1024 * 1024;
    private const long MaxUnpacked = 80 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly HttpClient Network = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(3)
    };
    private readonly string root;
    private readonly Version hostVersion;
    private readonly HttpClient http;
    private readonly ThemeReference bundled;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ThemeState state;
    private ThemeReference? staged;

    public ThemeStore() : this(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WildflowerDesktopData", "Themes", "pond-garden"),
        Path.Combine(AppContext.BaseDirectory, "themes", "pond-garden"),
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0), Network) { }

    internal ThemeStore(string root, string bundledDirectory, Version hostVersion, HttpClient http)
    {
        this.root = Path.GetFullPath(root);
        this.hostVersion = new Version(hostVersion.Major, hostVersion.Minor, Math.Max(0, hostVersion.Build), Math.Max(0, hostVersion.Revision));
        this.http = http;
        Directory.CreateDirectory(this.root);
        bundled = InstallBundled(bundledDirectory);
        state = ReadState();
        if (state.Pending) Rollback();
    }

    public string ActiveDirectory => GetDirectory(state.Active);

    public async Task<bool> StageUpdateAsync()
    {
        await gate.WaitAsync();
        string? temporary = null;
        try
        {
            if (state.Pending) throw new InvalidOperationException("请先确认或回滚当前主题。");
            var manifestBytes = await DownloadAsync(new Uri(FeedUrl + "theme.json"), 16 * 1024);
            var manifest = JsonSerializer.Deserialize<ThemeManifest>(manifestBytes, Json)
                ?? throw new InvalidDataException("主题清单为空。");
            ValidateManifest(manifest);
            if (ParseVersion(manifest.Version) <= ParseVersion(state.Active.Version)
                || manifest.Version == state.RejectedVersion) return false;
            var archive = await DownloadAsync(new Uri(manifest.Url), manifest.Size);
            if (archive.LongLength != manifest.Size || !Convert.ToHexString(SHA256.HashData(archive)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("主题包长度或 SHA256 校验失败。");

            // The hash is integrity against the GitHub HTTPS manifest, not a publisher signature.
            var reference = new ThemeReference(manifest.Version, manifest.Version + "-" + manifest.Sha256.ToLowerInvariant());
            temporary = Path.Combine(root, ".stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            ExtractArchive(archive, temporary);
            ValidateTheme(temporary);
            var destination = GetDirectory(reference);
            if (Directory.Exists(destination))
            {
                // Never overwrite a previous immutable version, including after an interrupted activation.
                ValidateTheme(destination);
                if (!TreesEqual(temporary, destination)) throw new InvalidDataException("已有主题目录与下载包不一致。");
            }
            else Directory.Move(temporary, destination);
            staged = reference;
            return true;
        }
        finally
        {
            if (temporary is not null && Directory.Exists(temporary)) Directory.Delete(temporary, true);
            gate.Release();
        }
    }

    public void ActivateStaged()
    {
        if (state.Pending) throw new InvalidOperationException("当前主题尚未确认。");
        var next = staged ?? throw new InvalidOperationException("尚无已校验的主题更新。");
        ValidateTheme(GetDirectory(next));
        WriteState(new ThemeState(next, state.Active, true, state.RejectedVersion));
        staged = null;
    }

    public void ConfirmActivation()
    {
        if (state.Pending) WriteState(state with { Pending = false });
    }

    public void Rollback()
    {
        if (!state.Pending) return;
        var previous = state.Previous ?? bundled;
        if (!IsUsable(previous)) previous = bundled;
        WriteState(new ThemeState(previous, null, false, state.Active.Version));
        staged = null;
    }

    private void ValidateManifest(ThemeManifest m)
    {
        if (m.Schema != 1 || m.Id != "pond-garden" || m.SaveSchema != 2)
            throw new InvalidDataException("不兼容的主题或存档协议。");
        _ = ParseVersion(m.Version);
        if (ParseVersion(m.MinHostVersion) > hostVersion) throw new InvalidDataException("该主题需要更新的软件版本。");
        if (m.Size <= 0 || m.Size > MaxDownload || !Regex.IsMatch(m.Sha256 ?? "", "\\A[0-9a-fA-F]{64}\\z"))
            throw new InvalidDataException("主题包大小或校验值无效。");
        if (!Uri.TryCreate(m.Url, UriKind.Absolute, out var uri) || !IsReleaseUrl(uri))
            throw new InvalidDataException("主题来源必须是本项目的 GitHub 发布资产。");
    }

    private static Version ParseVersion(string value)
    {
        if (value is null || !Regex.IsMatch(value, "\\A[0-9]+\\.[0-9]+\\.[0-9]+(?:\\.[0-9]+)?\\z") || !Version.TryParse(value, out var version))
            throw new InvalidDataException("主题版本格式无效。");
        return new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
    }

    private static bool IsReleaseUrl(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.Query.Length == 0
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(uri.AbsolutePath, "\\A/qshanx/wildflower-desktop/releases/download/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+\\z");

    private static bool IsAssetRedirect(Uri uri) => IsReleaseUrl(uri)
        || (uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
            && uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private async Task<byte[]> DownloadAsync(Uri uri, long limit)
    {
        if (!IsReleaseUrl(uri)) throw new InvalidDataException("不允许的主题下载地址。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("WildflowerDesktop/0.1");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location ?? throw new InvalidDataException("下载跳转地址缺失。");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!IsAssetRedirect(uri)) throw new InvalidDataException("下载跳转离开 GitHub 资产服务。");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("主题下载超过限制。");
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (output.Length + count > limit) throw new InvalidDataException("主题下载超过限制。");
                await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
            }
            return output.ToArray();
        }
        throw new InvalidDataException("主题下载跳转次数过多。");
    }

    internal static void ExtractArchive(byte[] bytes, string destination)
    {
        using var stream = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 4096) throw new InvalidDataException("主题包文件数量超过限制。");
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            var directory = name.EndsWith('/');
            var clean = directory ? name[..^1] : name;
            var parts = clean.Split('/');
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (name.Contains('\\') || parts.Any(UnsafeSegment) || !entries.Add(clean)
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || (unixType != 0 && unixType != 0x8000 && unixType != 0x4000)
                || (unixType == 0x4000 && !directory))
                throw new InvalidDataException("主题包含不安全、重复或链接路径。");
            for (var i = 1; i <= parts.Length; i++)
            {
                var path = string.Join('/', parts.Take(i));
                if (names.TryGetValue(path, out var existing) && existing != path)
                    throw new InvalidDataException("主题包含大小写冲突的路径。");
                names[path] = path;
            }
            var target = Path.Combine(destination, Path.Combine(parts));
            if (directory)
            {
                if (entry.Length != 0) throw new InvalidDataException("主题目录含意外内容。");
                Directory.CreateDirectory(target);
                continue;
            }
            if (entry.Length > MaxUnpacked - expanded) throw new InvalidDataException("主题解压大小超过限制。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            int count;
            long actual = 0;
            while ((count = input.Read(buffer)) != 0)
            {
                actual += count;
                expanded += count;
                if (expanded > MaxUnpacked || actual > entry.Length) throw new InvalidDataException("主题解压大小超过限制。");
                output.Write(buffer, 0, count);
            }
            if (actual != entry.Length) throw new InvalidDataException("主题压缩包不完整。");
        }
    }

    private static bool UnsafeSegment(string segment) => string.IsNullOrEmpty(segment) || segment is "." or ".."
        || segment.EndsWith(' ') || segment.EndsWith('.') || segment.Any(c => c < 32 || "<>:\"|?*".Contains(c))
        || Regex.IsMatch(segment.Split('.')[0], "\\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\\z", RegexOptions.IgnoreCase);

    private static void ValidateTheme(string directory)
    {
        foreach (var name in new[] { "flower-meadow.html", "pond-botanical.webp", "pond-botanical-atlas.webp" })
        {
            var file = new FileInfo(Path.Combine(directory, name));
            if (!file.Exists || file.Length == 0 || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("主题缺少必要入口或画面资源。");
        }
        var html = File.ReadAllText(Path.Combine(directory, "flower-meadow.html"));
        if (!html.Contains("meadowDesktop") || !html.Contains("pond-garden-v2"))
            throw new InvalidDataException("主题缺少桌面接口或兼容的存档入口。");
    }

    private ThemeReference InstallBundled(string directory)
    {
        ValidateTheme(directory);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        foreach (var file in files)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, file).Replace('\\', '/')));
            hash.AppendData(File.ReadAllBytes(file));
        }
        var reference = new ThemeReference("0.1.0", "bundled-" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        var destination = GetDirectory(reference);
        if (!Directory.Exists(destination))
        {
            var temporary = Path.Combine(root, ".bundle-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temporary);
                foreach (var file in files)
                {
                    var target = Path.Combine(temporary, Path.GetRelativePath(directory, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, false);
                }
                ValidateTheme(temporary);
                Directory.Move(temporary, destination);
            }
            finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        }
        ValidateTheme(destination);
        return reference;
    }

    private static bool TreesEqual(string first, string second)
    {
        var left = Directory.GetFiles(first, "*", SearchOption.AllDirectories);
        var right = Directory.GetFiles(second, "*", SearchOption.AllDirectories);
        return left.Length == right.Length && left.All(path =>
        {
            var other = Path.Combine(second, Path.GetRelativePath(first, path));
            return File.Exists(other) && SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(other)));
        });
    }

    private string GetDirectory(ThemeReference reference)
    {
        if (reference is null || reference.Directory is null || !Regex.IsMatch(reference.Directory, "\\A(?:bundled-[0-9a-f]{64}|[0-9.]+-[0-9a-f]{64})\\z"))
            throw new InvalidDataException("主题状态目录无效。");
        _ = ParseVersion(reference.Version);
        return Path.Combine(root, reference.Directory);
    }

    private bool IsUsable(ThemeReference reference)
    {
        try { ValidateTheme(GetDirectory(reference)); return true; }
        catch (InvalidDataException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private ThemeState ReadState()
    {
        var path = Path.Combine(root, "state.json");
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<ThemeState>(File.ReadAllText(path), Json);
                if (loaded is not null && loaded.Active is not null)
                {
                    _ = GetDirectory(loaded.Active);
                    if (loaded.Pending) return loaded;
                    if (IsUsable(loaded.Active)) return loaded;
                    if (loaded.Previous is not null && IsUsable(loaded.Previous))
                        return new ThemeState(loaded.Previous, null, false, loaded.Active?.Version);
                }
            }
            catch (JsonException) { }
            catch (InvalidDataException) { }
        }
        return new ThemeState(bundled, null, false, null);
    }

    private void WriteState(ThemeState next)
    {
        var temporary = Path.Combine(root, ".state-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(file, next, Json);
                file.Flush(true);
            }
            File.Move(temporary, Path.Combine(root, "state.json"), true);
            state = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record ThemeReference(string Version, string Directory);
    private sealed record ThemeState(ThemeReference Active, ThemeReference? Previous, bool Pending, string? RejectedVersion);
    private sealed record ThemeManifest(int Schema, string Id, string Version, string MinHostVersion, int SaveSchema, string Url, long Size, string Sha256);
}
