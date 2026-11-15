using System.Runtime.InteropServices;
using Velopack;
using Velopack.Sources;

namespace WildflowerDesktop;

public sealed class ProgramUpdates
{
    public const string FeedUrl = "https://github.com/qshanx/wildflower-desktop/releases/download/update-feed/";
    private readonly UpdateManager manager;
    private readonly SemaphoreSlim gate = new(1, 1);
    private VelopackAsset? downloaded;

    public ProgramUpdates()
    {
        var channel = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            Architecture.X64 => "win-x64",
            _ => throw new PlatformNotSupportedException("仅支持 Windows ARM64 和 x64。")
        };
        manager = new UpdateManager(new SimpleWebSource(FeedUrl), new UpdateOptions
        {
            ExplicitChannel = channel,
            AllowVersionDowngrade = false
        });
    }

    public async Task<string?> DownloadAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!manager.IsInstalled) return null;
            var update = await manager.CheckForUpdatesAsync();
            if (update is null) return null;
            await manager.DownloadUpdatesAsync(update);
            downloaded = update.TargetFullRelease;
            return downloaded.Version.ToString();
        }
        finally { gate.Release(); }
    }

    // The host must stop input and successfully flush garden state before calling this.
    public void ApplyAndRestart()
    {
        var target = downloaded ?? throw new InvalidOperationException("尚无已校验的软件更新。请先检查更新。");
        manager.ApplyUpdatesAndRestart(target);
    }
}
