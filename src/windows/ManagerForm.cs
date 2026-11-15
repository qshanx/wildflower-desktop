using System.Text.Json;

namespace WildflowerDesktop;

internal enum WallpaperTheme { Pond, Rain, PinkRain }

internal sealed class ManagerForm : Form
{
    private readonly ThemeStore themes = new();
    private readonly ProgramUpdates updates = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private DesktopHost? host;
    private DesktopHost? preview;
    private static readonly Color Paper = Color.FromArgb(245, 246, 242);
    private static readonly Color Ink = Color.FromArgb(27, 47, 48);
    private static readonly Color Muted = Color.FromArgb(69, 87, 84);
    private static readonly Color Accent = Color.FromArgb(30, 85, 71);
    private readonly TabControl pages = new() { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
    private readonly Icon brandIcon =
        new(typeof(ManagerForm).Assembly.GetManifestResourceStream("WildflowerDesktop.Assets.outside.ico")!);
    private readonly Label pondUsage = new() { AutoSize = true, Text = "未应用" };
    private readonly Label rainUsage = new() { AutoSize = true, Text = "未应用" };
    private readonly Label pinkUsage = new() { AutoSize = true, Text = "未应用" };
    private WallpaperTheme activeTheme;
    private readonly Button restore = new() { Text = "恢复系统壁纸", AutoSize = true, Enabled = false };
    private readonly bool diagnostics, emptyHost;
    private readonly NotifyIcon tray;
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(560, 0), Text = "预览主题后，点击应用到桌面。" };
    private readonly Label updateStatus = new() { AutoSize = true, MaximumSize = new Size(560, 0) };
    private readonly CheckBox automatic = new() { AutoSize = true, Text = "自动更新（主题闲置时应用，程序下次启动安装）", Checked = true };
    private readonly Button install = new() { AutoSize = true, Text = "重启更新程序", Enabled = false };
    private readonly Button installTheme = new() { AutoSize = true, Text = "应用主题更新", Enabled = false };
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 60 * 60 * 1000 };
    private readonly System.Windows.Forms.Timer idleUpdateTimer = new() { Interval = 15000 };
    private bool quitting, programReady, themeReady, restoreDesktop, checkingUpdates;
    private readonly string settingsPath = Path.Combine(DesktopHost.DataDirectory, "settings.json");

    internal ManagerForm(bool diagnostics, bool emptyHost)
    {
        this.diagnostics = diagnostics; this.emptyHost = diagnostics && emptyHost;
        var version = typeof(ManagerForm).Assembly.GetName().Version;
        Text = version is null ? "窗外" : $"窗外 {version.ToString(3)}";
        Icon = brandIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9);
        ForeColor = Ink;
        BackColor = Paper;
        ClientSize = new Size(890, 505); MinimumSize = new Size(350, 420);
        StartPosition = FormStartPosition.CenterScreen;
        var library = AddPage("壁纸库");
        library.Controls.Add(new Label {
            Text = "窗外", AutoSize = true, ForeColor = Ink,
            Font = new Font(Font.FontFamily, 23, FontStyle.Bold), Margin = new Padding(3, 0, 0, 3)
        });
        library.Controls.Add(new Label {
            Text = "选一扇喜欢的风景，放到桌面。", AutoSize = true,
            ForeColor = Muted, Margin = new Padding(3, 0, 0, 17)
        });
        var cards = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        library.ClientSizeChanged += (_, _) => FitCards();
        void FitCards()
        {
            cards.MaximumSize = new Size(Math.Max(1, library.ClientSize.Width - library.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth), 0);
        }
        FitCards();
        cards.Controls.Add(CreateCard(WallpaperTheme.Pond, "池塘花园", "开木篮、选花种、点草地", pondUsage));
        cards.Controls.Add(CreateCard(WallpaperTheme.Rain, "雨夜窗台", "点玻璃，散开一圈水珠", rainUsage));
        cards.Controls.Add(CreateCard(WallpaperTheme.PinkRain, "粉暮窗台", "粉色暮景 · 点玻璃散开水珠", pinkUsage));
        library.Controls.Add(cards);
        restore.Click += async (_, _) => await RunAsync(StopThemeAsync);
        var footer = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 14, 0, 0) };
        StyleButton(restore);
        footer.Controls.Add(restore);
        library.Controls.Add(footer);
        status.ForeColor = Muted;
        status.Margin = new Padding(3, 6, 3, 12);
        library.Controls.Add(status);

        var settings = AddPage("软件设置");
        var check = new Button { Text = "检查更新", AutoSize = true, Margin = new Padding(3, 3, 8, 10) };
        StyleButton(check);
        StyleButton(install);
        StyleButton(installTheme);
        check.Click += async (_, _) => await CheckUpdatesAsync(false);
        settings.Controls.Add(check);
        settings.Controls.Add(automatic);
        var updatesPanel = new FlowLayoutPanel { AutoSize = true };
        updatesPanel.Controls.Add(install);
        updatesPanel.Controls.Add(installTheme);
        settings.Controls.Add(updatesPanel);
        settings.Controls.Add(updateStatus);
        Controls.Add(pages);
        try { if (File.Exists(settingsPath)) { var saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath)); automatic.Checked = saved?.AutomaticUpdates ?? true; restoreDesktop = saved?.DesktopApplied ?? false; activeTheme = saved?.PinkRainTheme == true ? WallpaperTheme.PinkRain : saved?.RainTheme == true ? WallpaperTheme.Rain : WallpaperTheme.Pond; } }
        catch (Exception) { automatic.Checked = false; status.Text = "设置未能读取，自动更新已暂停，可以继续使用主题。"; }
        automatic.CheckedChanged += (_, _) =>
        {
            try { SaveSettings(); }
            catch (Exception ex) { status.Text = "未能保存更新设置：" + ex.Message; }
        };
        install.Click += async (_, _) => await RunAsync(InstallUpdatesAsync);
        installTheme.Click += async (_, _) => await RunAsync(InstallThemeAsync);
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开壁纸库", null, (_, _) => OpenLibrary());
        menu.Items.Add("应用池塘花园", null, async (_, _) => await RunAsync(() => ShowThemeAsync(WallpaperTheme.Pond)));
        menu.Items.Add("应用雨夜窗台", null, async (_, _) => await RunAsync(() => ShowThemeAsync(WallpaperTheme.Rain)));
        menu.Items.Add("恢复系统壁纸", null, async (_, _) => await RunAsync(StopThemeAsync));
        menu.Items.Add("检查更新", null, async (_, _) => { OpenLibrary(); pages.SelectedIndex = 1; await CheckUpdatesAsync(false); });
        menu.Items.Add("退出", null, async (_, _) => await RunAsync(async () => { await StopThemeAsync(); quitting = true; Close(); }));
        tray = new NotifyIcon { Icon = brandIcon, Text = "窗外", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => OpenLibrary();
        updateTimer.Tick += async (_, _) => { if (automatic.Checked) await CheckUpdatesAsync(true); };
        updateTimer.Start();
        idleUpdateTimer.Tick += async (_, _) =>
        {
            if (automatic.Checked && !checkingUpdates && themeReady && activeTheme == WallpaperTheme.Pond && host?.CanActivateUpdate == true)
                await RunAsync(InstallThemeAsync);
        };
        idleUpdateTimer.Start();
        Shown += async (_, _) =>
        {
            var restarting = false;
            await RunAsync(() =>
            {
                programReady = updates.PendingVersion != null;
                install.Enabled = programReady;
                if (programReady)
                {
                    status.Text = "程序更新已下载，可点击安装。";
                    if (automatic.Checked && !emptyHost)
                    {
                        updates.ApplyAndRestart();
                        restarting = true;
                    }
                }
                return Task.CompletedTask;
            });
            if (restarting || IsDisposed) return;
            if (restoreDesktop) await RunAsync(() => ShowThemeAsync(activeTheme));
            if (automatic.Checked) await CheckUpdatesAsync(true);
        };
        FormClosing += (_, e) => { if (!quitting && e.CloseReason == CloseReason.UserClosing && host is { IsDesktop: true }) { e.Cancel = true; ClosePreview(); Hide(); } };
        FormClosed += (_, _) => { ClosePreview(); host?.Close(); tray.Dispose(); brandIcon.Dispose(); updateTimer.Dispose(); idleUpdateTimer.Dispose(); };
    }

    private Control CreateCard(WallpaperTheme theme, string title, string description, Label usage)
    {
        var card = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = Color.White, Padding = new Padding(12), Margin = new Padding(0, 0, 12, 12)
        };
        using var coverStream = typeof(ManagerForm).Assembly.GetManifestResourceStream(theme switch { WallpaperTheme.Pond => "WildflowerDesktop.Assets.pond-cover.jpg", WallpaperTheme.Rain => "WildflowerDesktop.Assets.rain-cover.jpg", _ => "WildflowerDesktop.Assets.pink-cover.jpg" })!;
        using var coverSource = Image.FromStream(coverStream);
        var cover = new PictureBox { Image = new Bitmap(coverSource), Size = new Size(224, 126), SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = title + "封面" };
        cover.Disposed += (_, _) => cover.Image?.Dispose();
        card.Controls.Add(cover);
        card.Controls.Add(new Label {
            Text = title, AutoSize = true, Font = new Font(Font.FontFamily, 15, FontStyle.Bold),
            Margin = new Padding(3, 8, 3, 5)
        });
        usage.Padding = new Padding(6, 2, 6, 2);
        usage.Margin = new Padding(3, 0, 3, 5);
        card.Controls.Add(usage);
        SetUsage(usage, "未应用");
        card.Controls.Add(new Label { Text = description, ForeColor = Muted, AutoSize = true, Margin = new Padding(3, 0, 3, 8) });
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        AddButton(actions, "预览", () => PreviewThemeAsync(theme));
        var apply = AddButton(actions, "应用到桌面", () => ShowThemeAsync(theme), primary: true);
        usage.TextChanged += (_, _) => apply.Enabled = usage.Text != "使用中";
        card.Controls.Add(actions);
        return card;
    }

    private FlowLayoutPanel AddPage(string title)
    {
        var page = new TabPage(title) { BackColor = Paper };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        page.Controls.Add(panel);
        pages.TabPages.Add(page);
        return panel;
    }

    private Button AddButton(Control panel, string title, Func<Task> action, bool primary = false)
    {
        var button = new Button { Text = title, AutoSize = true, Margin = new Padding(3, 3, 8, 10) };
        StyleButton(button, primary);
        button.Click += async (_, _) => await RunAsync(action);
        panel.Controls.Add(button);
        return button;
    }

    private static void StyleButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(206, 217, 211);
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(25, 68, 57) : Color.FromArgb(234, 241, 236);
        button.BackColor = primary ? Accent : Color.White;
        button.ForeColor = primary ? Color.White : Ink;
        button.Padding = new Padding(8, 4, 8, 4);
    }

    private static void SetUsage(Label label, string value)
    {
        label.BackColor = value == "使用中" ? Color.FromArgb(221, 241, 226) : Paper;
        label.ForeColor = value == "使用中" ? Accent : Muted;
        label.Text = value;
    }
    internal void OpenLibrary() { pages.SelectedIndex = 0; Show(); WindowState = FormWindowState.Normal; Activate(); }
    private async Task RunAsync(Func<Task> action)
    {
        if (!await operations.WaitAsync(0)) return;
        try { await action(); }
        catch (Exception ex) { status.Text = ex.Message; tray.ShowBalloonTip(5000, "窗外", ex.Message, ToolTipIcon.Warning); }
        finally
        {
            var current = host is { IsDesktop: true } ? (host.CanFlush ? "使用中" : "需要重新应用") : "未应用";
            SetUsage(pondUsage, activeTheme == WallpaperTheme.Pond ? current : "未应用");
            SetUsage(rainUsage, activeTheme == WallpaperTheme.Rain ? current : "未应用");
            SetUsage(pinkUsage, activeTheme == WallpaperTheme.PinkRain ? current : "未应用");
            restore.Enabled = host is { IsDesktop: true };
            operations.Release();
        }
    }
    private async Task PreviewThemeAsync(WallpaperTheme theme)
    {
        ClosePreview();
        var candidate = new DesktopHost(false, diagnostics: false, emptyHost: false);
        preview = candidate;
        candidate.FormClosed += (_, _) => { if (preview == candidate) preview = null; };
        candidate.HostFailed += message => { status.Text = message; candidate.Close(); };
        try { await candidate.LoadThemeAsync(ThemeDirectory(theme), theme); }
        catch { candidate.Close(); throw; }
    }
    private void ClosePreview()
    {
        var previous = preview;
        preview = null;
        previous?.Close();
    }
    private string ThemeDirectory(WallpaperTheme theme) => theme != WallpaperTheme.Pond
        ? Path.Combine(AppContext.BaseDirectory, "themes", "rain-window") : themes.ActiveDirectory;

    private async Task ShowThemeAsync(WallpaperTheme theme)
    {
        ClosePreview();
        var previous = host is { IsDesktop: true };
        var previousTheme = activeTheme;
        if (host is { CanFlush: true }) await host.FlushAsync();
        ReleaseHost();
        try { await StartHostAsync(true, theme); }
        catch
        {
            if (previous) await StartHostAsync(true, previousTheme);
            throw;
        }
        activeTheme = theme;
        restoreDesktop = true;
        SaveSettings();
    }
    private async Task StartHostAsync(bool desktop, WallpaperTheme theme = WallpaperTheme.Pond)
    {
        var candidate = new DesktopHost(desktop, diagnostics, emptyHost);
        candidate.HostFailed += message =>
        {
            status.Text = message;
            (theme switch { WallpaperTheme.Pond => pondUsage, WallpaperTheme.Rain => rainUsage, _ => pinkUsage }).Text = "需要重新应用";
            tray.ShowBalloonTip(5000, "窗外", message, ToolTipIcon.Warning);
        };
        candidate.FormClosed += (_, _) => { if (host == candidate) { host = null; status.Text = "已恢复系统壁纸。"; } };
        try
        {
            await candidate.LoadThemeAsync(ThemeDirectory(theme), theme);
            host = candidate;
            status.Text = desktop ? (theme == WallpaperTheme.Pond ? "已应用池塘花园。回到桌面，左键开篮、选花、点草地。" : "已应用到主屏幕。回到桌面，左键点玻璃。") : "预览中。应用后可在真实桌面种花。";
        }
        catch { candidate.Close(); throw; }
    }
    private void ReleaseHost()
    {
        var previous = host;
        host = null;
        previous?.Close();
    }
    private async Task StopThemeAsync()
    {
        ClosePreview();
        if (host != null)
        {
            var previous = host;
            try { await previous.FlushAsync(); }
            catch (Exception ex) { tray.ShowBalloonTip(5000, "窗外", "存档确认失败：" + ex.Message, ToolTipIcon.Warning); }
            finally { ReleaseHost(); }
        }
        restoreDesktop = false; SaveSettings();
        status.Text = "已恢复系统壁纸。";
    }
    private async Task CheckUpdatesAsync(bool background)
    {
        if (emptyHost) { updateStatus.Text = "空宿主测量模式不执行更新验收。"; return; }
        if (checkingUpdates || operations.CurrentCount == 0) return;
        checkingUpdates = true;
        install.Enabled = false;
        installTheme.Enabled = false;
        updateStatus.Text = "正在分别检查程序和主题更新…";
        var errors = new List<string>();
        try { themeReady |= await themes.StageUpdateAsync(); }
        catch (Exception ex) { errors.Add("主题：" + ex.Message); }
        try { programReady |= await updates.DownloadAsync() != null; }
        catch (Exception ex) { errors.Add("程序：" + ex.Message); }
        checkingUpdates = false;
        if (IsDisposed) return;
        install.Enabled = programReady;
        installTheme.Enabled = themeReady;
        var hasUpdate = themeReady || programReady;
        updateStatus.Text = hasUpdate ? "更新已下载，程序和主题可分别安装。" :
            errors.Count > 0 ? "更新检查未完成，稍后可重试。" : "当前没有可用更新。";
        if (errors.Count > 0) updateStatus.Text += " " + string.Join("；", errors);
        if (background && hasUpdate) tray.ShowBalloonTip(5000, "窗外有更新", "更新已下载，可打开窗外安装。", ToolTipIcon.Info);
    }
    private async Task InstallThemeAsync()
    {
        if (checkingUpdates || !themeReady) return;
        if (activeTheme != WallpaperTheme.Pond && host is { IsDesktop: true })
        {
            updateStatus.Text = "池塘花园更新已下载。切回池塘花园或恢复系统壁纸后即可应用。";
            return;
        }
        var wasDesktop = host?.IsDesktop == true;
        if (host != null) await host.FlushAsync();
        ClosePreview();
        ReleaseHost();
        try
        {
            themes.ActivateStaged();
            // Validate the actual page and first draw before committing resources.
            await StartHostAsync(wasDesktop);
            themes.ConfirmActivation(); themeReady = false;
        }
        catch (Exception updateError)
        {
            ReleaseHost();
            themes.Rollback(); themeReady = false; installTheme.Enabled = false;
            try { await StartHostAsync(wasDesktop); }
            catch (Exception restoreError)
            {
                throw new InvalidOperationException($"主题更新失败，旧主题也未能重新加载：{restoreError.Message}。请重新应用主题。", updateError);
            }
            throw new InvalidOperationException($"主题更新失败，已恢复旧主题：{updateError.Message}", updateError);
        }
        finally { if (!wasDesktop) ReleaseHost(); }
        installTheme.Enabled = false;
        updateStatus.Text = "主题已更新，花园存档已保留。";
    }
    private async Task InstallUpdatesAsync()
    {
        if (checkingUpdates) return;
        if (programReady)
        {
            var wasDesktop = host?.IsDesktop == true;
            if (host != null) await host.FlushAsync();
            await StopThemeAsync();
            restoreDesktop = wasDesktop; SaveSettings();
            updates.ApplyAndRestart();
        }
    }
    private void SaveSettings()
    {
        Directory.CreateDirectory(DesktopHost.DataDirectory);
        var staged = settingsPath + ".tmp";
        File.WriteAllText(staged, JsonSerializer.Serialize(new AppSettings(automatic.Checked, restoreDesktop, activeTheme != WallpaperTheme.Pond, activeTheme == WallpaperTheme.PinkRain)));
        File.Move(staged, settingsPath, true);
    }
    private sealed record AppSettings(bool AutomaticUpdates, bool DesktopApplied = false, bool RainTheme = false, bool PinkRainTheme = false);
}
