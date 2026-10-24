namespace MonitorConfig;

static class Program
{
    const string MutexName = @"Local\MonitorConfig";
    const string ShowEventName = @"Local\MonitorConfig.Show";

    [STAThread]
    static void Main(string[] args)
    {
        bool background = args.Contains("--background");

        using var mutex = new Mutex(true, MutexName, out bool first);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            // Already running: ask that instance to bring up its window.
            if (!background) showEvent.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(background, showEvent));
    }
}

/// <summary>Owns the controller, the tray icon and the (lazily created) window.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly Controller controller;
    readonly Api api;
    readonly NotifyIcon tray;
    readonly Icon icon;
    readonly RegisteredWaitHandle showWait;
    readonly SynchronizationContext ui;
    MainForm? window;
    bool quitting;

    public TrayApp(bool background, EventWaitHandle showEvent)
    {
        // WinForms installs its sync context with the first control; make sure it exists now.
        using (new Control()) { }
        ui = SynchronizationContext.Current!;

        using (var s = typeof(TrayApp).Assembly.GetManifestResourceStream("app.ico")!) icon = new Icon(s);

        string home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorConfig");
        controller = new Controller(Path.Combine(home, "settings.json"), new Autostart("MonitorConfig", Application.ExecutablePath));
        api = new Api(controller);
        controller.QuitRequested += () => ui.Post(_ => Quit(), null);
        controller.Changed += () => ui.Post(_ => { UpdateTooltip(); ApplyTaskbar(); }, null);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        foreach (var p in new[] { 10, 35, 65, 100 })
            menu.Items.Add($"Яркость {p}", null, async (_, _) => await controller.SetAllBrightness(_ => p));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Завершить", null, (_, _) => Quit());

        tray = new NotifyIcon { Icon = icon, Text = "Монитор", ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent,
            (_, _) => ui.Post(_ => ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);

        _ = Start(background);
    }

    async Task Start(bool background)
    {
        if (!background) ShowWindow();
        await controller.StartAsync();
        UpdateTooltip();
    }

    void ShowWindow()
    {
        if (window == null || window.IsDisposed)
        {
            window = new MainForm(api, () => controller.BackgroundWanted, icon);
            window.FormClosed += (_, _) => { if (!controller.BackgroundWanted) Quit(); };
            ApplyTaskbar();
        }
        window.Show();
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.Activate();
    }

    void ApplyTaskbar()
    {
        // no-op unless the value actually changes (the setter recreates the window handle)
        if (window is { IsDisposed: false }) window.ShowInTaskbar = !controller.Settings.TrayOnly;
    }

    void UpdateTooltip()
    {
        tray.Text = controller.PrimaryBrightnessPercent() is int p ? $"Монитор · яркость {p}" : "Монитор";
    }

    void Quit()
    {
        if (quitting) return;
        quitting = true;
        showWait.Unregister(null);
        window?.CloseForGood();
        tray.Visible = false;
        tray.Dispose();
        controller.Dispose();
        ExitThread();
    }
}
