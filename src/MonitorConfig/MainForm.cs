using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace MonitorConfig;

/// <summary>The panel window: a WebView2 showing the embedded UI, served from memory.</summary>
sealed class MainForm : Form
{
    const string Origin = "https://monitorconfig.local";

    static readonly Dictionary<string, string> Types = new()
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".svg"] = "image/svg+xml",
    };

    readonly Api api;
    readonly Func<bool> keepRunning;
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    bool reallyClose;

    public MainForm(Api api, Func<bool> keepRunning, Icon icon)
    {
        this.api = api;
        this.keepRunning = keepRunning;
        Text = "Монитор";
        Icon = icon;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(360, 540);
        MinimumSize = new Size(320, 420);
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        bool dark = SystemDark();
        BackColor = dark ? Color.FromArgb(21, 20, 18) : Color.FromArgb(243, 241, 236);
        web.DefaultBackgroundColor = BackColor;
        Controls.Add(web);

        HandleCreated += (_, _) =>
        {
            int on = dark ? 1 : 0;
            Native.DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        };
        Load += async (_, _) => await InitWebView();
    }

    static bool SystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    async Task InitWebView()
    {
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorConfig", "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(null, data);
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;

        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;

        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnRequest;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WindowCloseRequested += (_, _) => Close();
        core.Navigate(Origin + "/");
    }

    async void OnRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var uri = new Uri(e.Request.Uri);
            string path = uri.AbsolutePath;
            int status;
            string type;
            Stream content;

            if (path.StartsWith("/api/"))
            {
                string body = "";
                if (e.Request.Content != null)
                    using (var reader = new StreamReader(e.Request.Content)) body = await reader.ReadToEndAsync();
                var res = await api.Handle(e.Request.Method, path, uri.Query, body);
                status = res.Status;
                type = "application/json";
                content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(res.Json));
            }
            else
            {
                string name = "wwwroot/" + (path == "/" ? "index.html" : path.TrimStart('/'));
                var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
                status = stream == null ? 404 : 200;
                type = Types.GetValueOrDefault(Path.GetExtension(name), "application/octet-stream");
                content = stream ?? new MemoryStream();
            }

            e.Response = web.CoreWebView2.Environment.CreateWebResourceResponse(
                content, status, status == 200 ? "OK" : "Error", $"Content-Type: {type}\nCache-Control: no-store");
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Close for real (app exit), instead of hiding to the tray.</summary>
    public void CloseForGood()
    {
        reallyClose = true;
        Close();
    }

    protected override void OnResize(EventArgs e)
    {
        // Without a taskbar button a minimized window would park as a stub above the taskbar; hide it to the tray.
        if (WindowState == FormWindowState.Minimized && !ShowInTaskbar)
        {
            Hide();
            WindowState = FormWindowState.Normal;
            return;
        }
        base.OnResize(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // With automation on, the window just hides; the app keeps running in the tray.
        if (!reallyClose && e.CloseReason == CloseReason.UserClosing && keepRunning())
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
