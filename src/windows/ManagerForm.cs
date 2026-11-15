using System.Text.Json;

namespace WildflowerDesktop;

internal sealed class ManagerForm : Form
{
    private readonly ThemeStore themes = new();
    private readonly ProgramUpdates updates = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private DesktopHost? host;
    private readonly bool diagnostics, emptyHost;
    private readonly NotifyIcon tray;
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(560, 0), Text = "预览主题后，点击应用到桌面。" };
    private readonly CheckBox automatic = new() { AutoSize = true, Text = "自动更新（主题闲置时应用，程序下次启动安装）", Checked = true };
    private readonly Button install = new() { AutoSize = true, Text = "安装已下载更新", Enabled = false };
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 60 * 60 * 1000 };
    private readonly System.Windows.Forms.Timer idleUpdateTimer = new() { Interval = 15000 };
    private bool quitting, programReady, themeReady, restoreDesktop;
    private readonly string settingsPath = Path.Combine(DesktopHost.DataDirectory, "settings.json");

    internal ManagerForm(bool diagnostics, bool emptyHost)
    {
        this.diagnostics = diagnostics; this.emptyHost = diagnostics && emptyHost;
        Text = "野花原"; ClientSize = new Size(620, 340); MinimumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        panel.Controls.Add(new Label { Text = "池塘花园", AutoSize = true, Font = new Font(Font.FontFamily, 22, FontStyle.Bold) });
        panel.Controls.Add(new Label { Text = "开木篮 · 选花种 · 点草地\n应用后关闭此窗口，花园会继续留在桌面。", AutoSize = true, Margin = new Padding(3, 8, 3, 20) });
        var actions = new FlowLayoutPanel { AutoSize = true };
        AddButton(actions, "预览", async () => await ShowThemeAsync(false));
        AddButton(actions, "应用到桌面", async () => await ShowThemeAsync(true));
        AddButton(actions, "恢复系统壁纸", StopThemeAsync);
        AddButton(actions, "检查更新", () => CheckUpdatesAsync(false));
        panel.Controls.Add(actions); panel.Controls.Add(automatic); panel.Controls.Add(install); panel.Controls.Add(status);
        Controls.Add(panel);
        try { if (File.Exists(settingsPath)) { var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath)); automatic.Checked = saved?.AutomaticUpdates ?? true; restoreDesktop = saved?.DesktopApplied ?? false; } }
        catch (Exception) { automatic.Checked = false; status.Text = "设置未能读取，自动更新已暂停，可以继续使用主题。"; }
        automatic.CheckedChanged += (_, _) =>
        {
            try { SaveSettings(); }
            catch (Exception ex) { status.Text = "未能保存更新设置：" + ex.Message; }
        };
        install.Click += async (_, _) => await RunAsync(InstallUpdatesAsync);
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开野花原", null, (_, _) => OpenManager());
        menu.Items.Add("应用到桌面", null, async (_, _) => await RunAsync(() => ShowThemeAsync(true)));
        menu.Items.Add("恢复系统壁纸", null, async (_, _) => await RunAsync(StopThemeAsync));
        menu.Items.Add("检查更新", null, async (_, _) => await RunAsync(() => CheckUpdatesAsync(false)));
        menu.Items.Add("退出", null, async (_, _) => await RunAsync(async () => { await StopThemeAsync(); quitting = true; Close(); }));
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "野花原", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => OpenManager();
        updateTimer.Tick += async (_, _) => { if (automatic.Checked) await RunAsync(() => CheckUpdatesAsync(true)); };
        updateTimer.Start();
        idleUpdateTimer.Tick += async (_, _) =>
        {
            if (automatic.Checked && themeReady && host?.CanActivateUpdate == true)
                await RunAsync(InstallThemeAsync);
        };
        idleUpdateTimer.Start();
        Shown += async (_, _) =>
        {
            await RunAsync(() =>
            {
                programReady = updates.PendingVersion != null;
                install.Enabled = programReady;
                if (programReady)
                {
                    status.Text = "程序更新已下载，可点击安装。";
                    if (automatic.Checked && !emptyHost) updates.ApplyAndRestart();
                }
                return Task.CompletedTask;
            });
            if (restoreDesktop) await RunAsync(() => ShowThemeAsync(true));
            if (automatic.Checked) await RunAsync(() => CheckUpdatesAsync(true));
        };
        FormClosing += (_, e) => { if (!quitting && e.CloseReason == CloseReason.UserClosing && host is { IsDesktop: true }) { e.Cancel = true; Hide(); } };
        FormClosed += (_, _) => { host?.Close(); tray.Dispose(); updateTimer.Dispose(); idleUpdateTimer.Dispose(); };
    }

    private void AddButton(Control panel, string title, Func<Task> action)
    {
        var button = new Button { Text = title, AutoSize = true, Margin = new Padding(3, 3, 8, 10) };
        button.Click += async (_, _) => await RunAsync(action); panel.Controls.Add(button);
    }
    private void OpenManager() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private async Task RunAsync(Func<Task> action)
    {
        if (!await operations.WaitAsync(0)) return;
        try { await action(); }
        catch (Exception ex) { status.Text = ex.Message; tray.ShowBalloonTip(5000, "野花原", ex.Message, ToolTipIcon.Warning); }
        finally { operations.Release(); }
    }
    private async Task ShowThemeAsync(bool desktop)
    {
        if (host != null) await host.FlushAsync();
        await StopThemeAsync();
        var candidate = new DesktopHost(desktop, diagnostics, emptyHost);
        candidate.HostFailed += message => { status.Text = message; tray.ShowBalloonTip(5000, "野花原", message, ToolTipIcon.Warning); };
        candidate.FormClosed += (_, _) => { if (host == candidate) { host = null; status.Text = "已恢复系统壁纸。"; } };
        try
        {
            await candidate.LoadThemeAsync(themes.ActiveDirectory);
            host = candidate;
            restoreDesktop = desktop; SaveSettings();
            status.Text = desktop ? "已应用到主屏幕。回到桌面，左键开篮、选花、点草地。" : "预览中。应用后可在真实桌面种花。";
        }
        catch { candidate.Close(); throw; }
    }
    private async Task StopThemeAsync()
    {
        if (host != null)
        {
            var previous = host;
            try { await previous.FlushAsync(); }
            catch (Exception ex) { tray.ShowBalloonTip(5000, "野花原", "存档确认失败：" + ex.Message, ToolTipIcon.Warning); }
            finally { previous.Close(); host = null; }
        }
        restoreDesktop = false; SaveSettings();
        status.Text = "已恢复系统壁纸。";
    }
    private async Task CheckUpdatesAsync(bool background)
    {
        if (emptyHost) { status.Text = "空宿主测量模式不执行更新验收。"; return; }
        status.Text = "正在分别检查程序和主题更新…";
        var errors = new List<string>();
        try { themeReady |= await themes.StageUpdateAsync(); }
        catch (Exception ex) { errors.Add("主题：" + ex.Message); }
        try { programReady |= await updates.DownloadAsync() != null; }
        catch (Exception ex) { errors.Add("程序：" + ex.Message); }
        install.Enabled = themeReady || programReady;
        status.Text = install.Enabled ? "更新已下载。点击安装后，花园存档会保留。" : "当前没有可用更新。";
        if (errors.Count > 0) status.Text += " " + string.Join("；", errors);
        if (background && install.Enabled) tray.ShowBalloonTip(5000, "野花原有更新", "更新已下载，可打开野花原安装。", ToolTipIcon.Info);
    }
    private async Task InstallThemeAsync()
    {
        if (!themeReady) return;
        if (host != null) await host.PauseForSwitchAsync();
        try
        {
            themes.ActivateStaged();
            // Validate the actual page and first draw before committing resources.
            if (host == null) await ShowThemeAsync(false);
            else await host.NavigateAsync(themes.ActiveDirectory);
            themes.ConfirmActivation(); themeReady = false;
        }
        catch
        {
            themes.Rollback(); themeReady = false; install.Enabled = programReady;
            if (host != null) await host.NavigateAsync(themes.ActiveDirectory);
            throw;
        }
        install.Enabled = programReady;
        status.Text = "主题已自动更新，原花园存档已保留。";
    }
    private async Task InstallUpdatesAsync()
    {
        await InstallThemeAsync();
        if (programReady)
        {
            var wasDesktop = host?.IsDesktop == true;
            if (host != null) await host.FlushAsync();
            await StopThemeAsync();
            restoreDesktop = wasDesktop; SaveSettings();
            updates.ApplyAndRestart();
        }
        install.Enabled = themeReady || programReady;
    }
    private void SaveSettings()
    {
        Directory.CreateDirectory(DesktopHost.DataDirectory);
        var staged = settingsPath + ".tmp";
        File.WriteAllText(staged, JsonSerializer.Serialize(new AppSettings(automatic.Checked, restoreDesktop)));
        File.Move(staged, settingsPath, true);
    }
    private sealed record AppSettings(bool AutomaticUpdates, bool DesktopApplied = false);
}
