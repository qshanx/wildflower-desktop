using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WildflowerDesktop;

var tests = new (string Name, Func<Task> Run)[]
{
    ("upgrade, crash recovery, save preservation and rejected-version quarantine", async () =>
    {
        using var fixture = new Fixture();
        var store = fixture.Create();
        var old = store.ActiveDirectory;
        Check(await store.StageUpdateAsync(), "update not staged");
        Check(store.ActiveDirectory == old, "staging activated too soon");
        store.ActivateStaged();
        var updated = store.ActiveDirectory;
        Check(updated != old && Directory.Exists(old), "previous theme lost");
        var restarted = fixture.Create();
        Check(restarted.ActiveDirectory == old, "unconfirmed theme did not roll back");
        Check(!await restarted.StageUpdateAsync(), "failed version should not crash-loop");
        Check(File.ReadAllText(fixture.SavePath) == "existing garden", "garden changed");
        Check(Directory.Exists(updated), "rollback deleted resources");
    }),
    ("confirmed update survives restart and does not redownload", async () =>
    {
        using var fixture = new Fixture();
        var store = fixture.Create();
        await store.StageUpdateAsync();
        store.ActivateStaged();
        store.ConfirmActivation();
        var restarted = fixture.Create();
        Check(restarted.ActiveDirectory == store.ActiveDirectory, "confirmed update rolled back");
        Check(!await restarted.StageUpdateAsync(), "current version staged again");
    }),
    ("explicit rollback retains original and allows newer version", async () =>
    {
        using var fixture = new Fixture();
        var store = fixture.Create();
        var old = store.ActiveDirectory;
        await store.StageUpdateAsync(); store.ActivateStaged(); store.Rollback();
        Check(store.ActiveDirectory == old, "manual rollback failed");
        fixture.Version = "0.1.2";
        Check(await store.StageUpdateAsync(), "newer fix should stage after rejection");
    }),
    ("hash tampering is rejected without switching resources", async () =>
    {
        using var fixture = new Fixture { BadHash = true };
        await ExpectRejected(fixture);
    }),
    ("failed recheck preserves an already verified staged candidate", async () =>
    {
        using var fixture = new Fixture();
        var store = fixture.Create();
        var before = store.ActiveDirectory;
        await store.StageUpdateAsync();
        fixture.BadHash = true;
        try { await store.StageUpdateAsync(); throw new Exception("tampered recheck accepted"); }
        catch (InvalidDataException) { }
        store.ActivateStaged();
        Check(store.ActiveDirectory != before, "valid staged update lost after network failure");
        store.ConfirmActivation();
    }),
    ("incorrect declared length is rejected", async () =>
    {
        using var fixture = new Fixture { SizeDelta = 1 };
        await ExpectRejected(fixture);
    }),
    ("future host requirement is rejected", async () =>
    {
        using var fixture = new Fixture { MinHost = "9.0.0" };
        await ExpectRejected(fixture);
    }),
    ("save schema mismatch is rejected", async () =>
    {
        using var fixture = new Fixture { SaveSchema = 3 };
        await ExpectRejected(fixture);
    }),
    ("external URL and redirect are rejected", async () =>
    {
        using var direct = new Fixture { Url = "https://github.com/other/repo/releases/download/v1/theme.zip" };
        await ExpectRejected(direct);
        using var redirected = new Fixture { Redirect = "https://example.com/theme.zip" };
        await ExpectRejected(redirected);
    }),
    ("missing required artwork is rejected", async () =>
    {
        using var fixture = new Fixture { Archive = Zip(new() { ("flower-meadow.html", "meadowDesktop pond-garden-v2", 0) }) };
        await ExpectRejected(fixture);
    }),
    ("zip traversal, Windows aliases, case duplicates and symlinks are rejected", async () =>
    {
        foreach (var name in new[] { "../outside", "/absolute", "C:/absolute", "nested/../../outside", "nested\\outside", "CON.txt", "folder./file", "file:stream" })
        {
            using var fixture = new Fixture { Archive = Zip(new() { (name, "x", 0) }) };
            await ExpectRejected(fixture);
        }
        foreach (var entries in new List<(string, string, int)>[]
        {
            new() { ("same.html", "a", 0), ("SAME.html", "b", 0) },
            new() { ("Dir/a", "a", 0), ("dir/b", "b", 0) },
            new() { ("link", "../outside", unchecked((int)0xa1ff0000)) },
            new() { ("link", "outside", (int)FileAttributes.ReparsePoint) }
        })
        {
            using var fixture = new Fixture { Archive = Zip(entries) };
            await ExpectRejected(fixture);
        }
    }),
    ("expanded zip bomb is rejected", async () =>
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            using var data = zip.CreateEntry("bomb", CompressionLevel.SmallestSize).Open();
            var block = new byte[1024 * 1024];
            for (var n = 0; n < 81; n++) data.Write(block);
        }
        using var fixture = new Fixture { Archive = buffer.ToArray() };
        await ExpectRejected(fixture);
    }),
    ("corrupted state falls back without touching garden", () =>
    {
        using var fixture = new Fixture();
        var old = fixture.Create().ActiveDirectory;
        File.WriteAllText(Path.Combine(fixture.Root, "state.json"), "{\"active\":null,\"pending\":true}");
        Check(fixture.Create().ActiveDirectory == old, "invalid state did not recover");
        return Task.CompletedTask;
    })
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine("PASS " + test.Name);
}
Console.WriteLine($"{tests.Length} theme updater regression groups passed. Network responses are synthetic; real Windows upgrade remains separate.");

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}

static async Task ExpectRejected(Fixture fixture)
{
    var store = fixture.Create();
    var before = store.ActiveDirectory;
    try { await store.StageUpdateAsync(); throw new Exception("unsafe update was accepted"); }
    catch (InvalidDataException) { }
    Check(store.ActiveDirectory == before, "rejected update changed active directory");
    Check(File.ReadAllText(fixture.SavePath) == "existing garden", "rejected update changed save");
    Check(!Directory.GetDirectories(fixture.Root).Any(x => Path.GetFileName(x).StartsWith(".stage-")), "staging debris retained");
}

static byte[] Zip(List<(string Name, string Body, int Attributes)> entries)
{
    using var buffer = new MemoryStream();
    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        foreach (var item in entries)
        {
            var entry = zip.CreateEntry(item.Name);
            entry.ExternalAttributes = item.Attributes;
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(item.Body));
        }
    return buffer.ToArray();
}

sealed class Fixture : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "wildflower-theme-test-" + Guid.NewGuid().ToString("N"));
    private readonly string bundle;
    private readonly HttpClient http;
    public string Root { get; }
    public string SavePath { get; }
    public byte[] Archive { get; set; }
    public string Version { get; set; } = "0.1.1";
    public string MinHost { get; set; } = "0.1.0";
    public int SaveSchema { get; set; } = 2;
    public bool BadHash { get; set; }
    public long SizeDelta { get; set; }
    public string Url { get; set; } = "https://github.com/qshanx/wildflower-desktop/releases/download/theme-0.1.1/pond-garden.zip";
    public string? Redirect { get; set; }

    public Fixture()
    {
        Root = Path.Combine(temporary, "Themes", "pond-garden");
        SavePath = Path.Combine(temporary, "WebView2", "synthetic-garden.txt");
        bundle = Path.Combine(temporary, "bundle");
        Directory.CreateDirectory(bundle);
        Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
        File.WriteAllText(SavePath, "existing garden");
        File.WriteAllText(Path.Combine(bundle, "flower-meadow.html"), "meadowDesktop pond-garden-v2");
        File.WriteAllText(Path.Combine(bundle, "pond-botanical.webp"), "synthetic art");
        File.WriteAllText(Path.Combine(bundle, "pond-botanical-atlas.webp"), "synthetic atlas");
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            foreach (var file in Directory.GetFiles(bundle))
            {
                using var output = zip.CreateEntry(Path.GetFileName(file)).Open();
                output.Write(File.ReadAllBytes(file));
            }
        Archive = bytes.ToArray();
        http = new HttpClient(new Handler(this));
    }

    public ThemeStore Create() => new(Root, bundle, new Version(0, 1, 0), http);
    public void Dispose() { http.Dispose(); Directory.Delete(temporary, true); }

    private sealed class Handler(Fixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/theme.json"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        schema = 1, id = "pond-garden", version = fixture.Version, minHostVersion = fixture.MinHost,
                        saveSchema = fixture.SaveSchema, url = fixture.Url, size = fixture.Archive.LongLength + fixture.SizeDelta,
                        sha256 = fixture.BadHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(fixture.Archive))
                    }))
                });
            if (fixture.Redirect is not null)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri(fixture.Redirect);
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Archive) });
        }
    }
}
