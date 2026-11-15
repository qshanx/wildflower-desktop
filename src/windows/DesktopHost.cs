using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Text.Json;

namespace WildflowerDesktop;

internal sealed class DesktopHost : Form
{
    internal const string Origin = "https://pond.wildflower.local";
    internal static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WildflowerDesktopData");
    private CoreWebView2Environment? environment;
    private CoreWebView2Controller? controller;
    private CoreWebView2 Core => controller!.CoreWebView2;
    private readonly DesktopShell shell = new();
    private readonly SemaphoreSlim pauseGate = new(1, 1);
    private DesktopMouse? mouse;
    private readonly System.Windows.Forms.Timer visibilityTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer diagnosticsTimer = new() { Interval = 2000 };
    private readonly bool desktop, diagnostics, emptyHost;
    private bool paused, checking, held, ready, closing;
    private JsonElement? lastPage;
    private long frameEvents, reportedFrameCount;
    private DateTime? lastFrameUtc;
    internal bool IsDesktop => desktop;
    internal bool IsPaused => paused;
    internal bool CanActivateUpdate => desktop && ready && paused && !held && lastPage is { } page
        && page.ValueKind == JsonValueKind.Object && page.TryGetProperty("plantingMode", out var planting)
        && planting.ValueKind == JsonValueKind.False;
    internal event Action<string>? HostFailed;
    protected override bool ShowWithoutActivation => desktop;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; if (desktop) p.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020; return p; }
    }

    internal DesktopHost(bool desktop, bool diagnostics, bool emptyHost)
    {
        this.desktop = desktop; this.diagnostics = diagnostics; this.emptyHost = diagnostics && emptyHost;
        Text = "野花原 · 主题预览";
        BackColor = Color.FromArgb(27, 54, 40);
        ShowInTaskbar = !desktop;
        if (desktop) { FormBorderStyle = FormBorderStyle.None; Bounds = Screen.PrimaryScreen!.Bounds; }
        else { ClientSize = new Size(1100, 680); StartPosition = FormStartPosition.CenterScreen; }
        Resize += (_, _) => { if (controller != null) controller.Bounds = ClientRectangle; };
        visibilityTimer.Tick += async (_, _) => await CheckVisibilityAsync();
        diagnosticsTimer.Tick += async (_, _) => await WriteDiagnosticsAsync();
        if (desktop) SystemEvents.DisplaySettingsChanged += DisplayChanged;
    }

    private void DisplayChanged(object? sender, EventArgs e)
    {
        if (closing || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (closing) return;
            try { shell.Attach(this); if (controller != null) controller.Bounds = ClientRectangle; }
            catch (Exception ex) { HostFailed?.Invoke("显示器变化后请重新应用主题：" + ex.Message); }
        });
    }

    internal async Task LoadThemeAsync(string directory)
    {
        if (desktop) { shell.Connect(); _ = Handle; shell.Attach(this); }
        Show();
        if (desktop) Native.EnableWindow(Handle, false);
        environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDirectory, "WebView2"));
        controller = await environment.CreateCoreWebView2ControllerAsync(Handle);
        controller.Bounds = ClientRectangle;
        controller.IsVisible = true;
        controller.DefaultBackgroundColor = Color.FromArgb(27, 54, 40);
        Core.Settings.AreDevToolsEnabled = diagnostics;
        Core.Settings.AreDefaultContextMenusEnabled = false;
        Core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        Core.Settings.IsStatusBarEnabled = false;
        Core.Settings.IsZoomControlEnabled = false;
        Core.Settings.AreHostObjectsAllowed = false;
        Core.NavigationStarting += (_, e) => { if (!Allowed(e.Uri)) e.Cancel = true; };
        Core.FrameNavigationStarting += (_, e) => { if (!Allowed(e.Uri)) e.Cancel = true; };
        Core.NewWindowRequested += (_, e) => e.Handled = true;
        Core.DownloadStarting += (_, e) => e.Cancel = true;
        Core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        Core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        Core.WebResourceRequested += (_, e) =>
        {
            if (!Allowed(e.Request.Uri)) e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "Content-Type: text/plain");
        };
        Core.WebMessageReceived += (_, e) =>
        {
            if (!diagnostics || !Allowed(e.Source)) return;
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                if (doc.RootElement.GetProperty("type").GetString() == "frame")
                {
                    reportedFrameCount = doc.RootElement.GetProperty("frameCount").GetInt64();
                    frameEvents++; lastFrameUtc = DateTime.UtcNow;
                }
            }
            catch (Exception) { }
        };
        Core.ProcessFailed += (_, _) => { ready = false; mouse?.Dispose(); HostFailed?.Invoke("主题渲染进程已停止，请重新应用主题。"); };
        await NavigateAsync(directory);
        if (desktop) mouse = new DesktopMouse(this, OnMouse);
        visibilityTimer.Start();
        if (diagnostics) { diagnosticsTimer.Start(); await WriteDiagnosticsAsync(); }
    }

    private static bool Allowed(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && u.Host == "pond.wildflower.local" && u.IsDefaultPort;

    internal async Task NavigateAsync(string directory)
    {
        ready = false;
        await SetPausedAsync(false);
        if (emptyHost)
        {
            directory = Path.Combine(DataDirectory, "DiagnosticsEmpty");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "flower-meadow.html"), """
                <!doctype html><meta charset="utf-8"><title>Empty host baseline</title>
                <style>html,body{margin:0;background:#1b3628;width:100%;height:100%;}</style>
                <script>
                let frameCount=0;
                window.meadowDesktop=(action)=>action==='flush';
                window.meadowDiagnostics=()=>({ready:frameCount>0,frameCount,flowerCount:0,width:innerWidth,height:innerHeight});
                requestAnimationFrame(()=>{frameCount++;window.chrome.webview.postMessage({type:'frame',frameCount});});
                </script>
                """);
        }
        Core.SetVirtualHostNameToFolderMapping("pond.wildflower.local", directory, CoreWebView2HostResourceAccessKind.DenyCors);
        var completed = new TaskCompletionSource<bool>();
        void Finished(object? sender, CoreWebView2NavigationCompletedEventArgs e) => completed.TrySetResult(e.IsSuccess);
        Core.NavigationCompleted += Finished;
        try
        {
            Core.Navigate(Origin + "/flower-meadow.html?host=windows" + (emptyHost ? "&emptyHost=1" : "") + (diagnostics ? "&diagnostics=1" : ""));
            if (!await completed.Task.WaitAsync(TimeSpan.FromSeconds(20))) throw new InvalidOperationException("主题页面加载失败。");
            await Core.ExecuteScriptAsync($"window.meadowDesktop?.('wallpaper',{(desktop ? "true" : "false")});");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var json = await Core.ExecuteScriptAsync("window.meadowDiagnostics?.() ?? null");
                using var parsed = JsonDocument.Parse(json);
                var page = parsed.RootElement;
                if (page.ValueKind == JsonValueKind.Object && page.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True && page.TryGetProperty("frameCount", out var f) && f.GetInt64() > 0)
                { lastPage = page.Clone(); ready = true; return; }
                await Task.Delay(100);
            }
            throw new InvalidOperationException("主题没有完成首帧绘制，已保留原版本。");
        }
        finally { Core.NavigationCompleted -= Finished; }
    }

    internal async Task FlushAsync()
    {
        held = false;
        if (controller == null) return;
        await SetPausedAsync(false);
        var flushed = await Core.ExecuteScriptAsync("window.meadowDesktop?.('flush') === true");
        if (flushed != "true") throw new InvalidOperationException("花园存档未能确认保存，已取消本次更新或切换。");
    }

    internal async Task PauseForSwitchAsync()
    {
        var wasReady = ready;
        var wasPaused = paused;
        try
        {
            await FlushAsync();
            ready = false;
            await SetPausedAsync(true);
        }
        catch (Exception failure)
        {
            ready = false;
            try
            {
                await SetPausedAsync(wasPaused, force: true);
                ready = wasReady;
            }
            catch (Exception recoveryFailure)
            {
                throw new InvalidOperationException("更新已取消，但主题恢复失败，请重新应用主题。", new AggregateException(failure, recoveryFailure));
            }
            throw;
        }
    }

    private async void OnMouse(uint message, Native.POINT point)
    {
        if (!ready || paused || closing) return;
        try
        {
            if (message == 0x0202)
            {
                if (held) { held = false; await Core.ExecuteScriptAsync("window.meadowDesktop('up',0,0)"); }
                return;
            }
            if (message == 0x0200 && !held) return;
            if (!shell.IsEmptyDesktop(point))
            {
                if (held) { held = false; await Core.ExecuteScriptAsync("window.meadowDesktop('up',0,0)"); }
                return;
            }
            if (!Native.ScreenToClient(Handle, ref point) || !ClientRectangle.Contains(point.X, point.Y)) return;
            if (message == 0x0201) held = true;
            // Native points are physical pixels (PerMonitorV2); page coordinates are CSS pixels.
            var action = message == 0x0201 ? "windowsDown" : "drag";
            await Core.ExecuteScriptAsync($"window.meadowDesktop('{action}',{point.X}*innerWidth/{Math.Max(1, ClientSize.Width)},{point.Y}*innerHeight/{Math.Max(1, ClientSize.Height)})");
        }
        catch (Exception ex) { HostFailed?.Invoke("桌面互动已暂停：" + ex.Message); ready = false; }
    }

    private async Task CheckVisibilityAsync()
    {
        if (checking || closing || controller == null || !ready) return;
        checking = true;
        try
        {
            if (desktop && (!Native.IsWindow(shell.Parent) || !Native.IsWindow(shell.IconView)))
            {
                mouse?.Dispose(); mouse = null; ready = false;
                await SetPausedAsync(true);
                HostFailed?.Invoke("Windows 桌面已重启，请重新应用主题。");
                return;
            }
            var covered = desktop ? DesktopShell.FullyCovered(Screen.PrimaryScreen!.WorkingArea, Handle) : WindowState == FormWindowState.Minimized || !Visible;
            await SetPausedAsync(covered);
        }
        catch (Exception ex) { HostFailed?.Invoke("暂停检测失败：" + ex.Message); }
        finally { checking = false; }
    }

    private async Task SetPausedAsync(bool value, bool force = false)
    {
        await pauseGate.WaitAsync();
        try
        {
            if (controller == null || (!force && paused == value)) return;
            if (value)
            {
                if (force) Core.Resume();
                held = false;
                await Core.ExecuteScriptAsync("window.meadowDesktop?.('up',0,0);window.meadowDesktop?.('visible',false);");
                await ReadPageDiagnosticsAsync();
                controller.IsVisible = false;
                paused = true;
                await Core.TrySuspendAsync();
            }
            else
            {
                Core.Resume();
                controller.IsVisible = true;
                await Core.ExecuteScriptAsync("window.meadowDesktop?.('visible',true);");
                paused = false;
            }
        }
        finally { pauseGate.Release(); }
    }

    private async Task ReadPageDiagnosticsAsync()
    {
        var json = await Core.ExecuteScriptAsync("window.meadowDiagnostics?.() ?? null");
        using var doc = JsonDocument.Parse(json);
        lastPage = doc.RootElement.Clone();
    }

    private async Task WriteDiagnosticsAsync()
    {
        if (closing || controller == null || environment == null) return;
        try
        {
            // A suspended renderer is not queried: reading it must not wake it for measurement.
            if (!paused) await ReadPageDiagnosticsAsync();
            var processes = environment.GetProcessInfos().Select(p => new { processId = (int)p.ProcessId, kind = p.Kind.ToString() }).ToList();
            processes.Add(new { processId = Environment.ProcessId, kind = "Host" });
            var result = new { timestampUtc = DateTime.UtcNow, desktop, paused, ready, emptyHost, screen = Screen.PrimaryScreen!.Bounds.Size, page = lastPage, pageSampleFrozenWhileSuspended = paused, frameEvents, reportedFrameCount, lastFrameUtc, processes };
            Directory.CreateDirectory(DataDirectory);
            var path = Path.Combine(DataDirectory, "diagnostics.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception) { /* Diagnostics are opt-in and never interrupt the desktop. */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !closing)
        {
            closing = true;
            if (desktop) SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            visibilityTimer.Stop(); diagnosticsTimer.Stop(); mouse?.Dispose();
            try { controller?.Close(); }
            finally
            {
                controller = null; shell.Dispose();
                visibilityTimer.Dispose(); diagnosticsTimer.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
