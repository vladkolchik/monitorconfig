// End-to-end smoke test against real hardware. Drives the same API the UI uses, presses the global
// hotkeys, checks schedule/autostart, then launches the built app itself. Restores the original
// brightness at the end.
//   dotnet run --project tests/MonitorConfig.Smoke            — everything but the minute-boundary wait
//   dotnet run --project tests/MonitorConfig.Smoke -- --full  — also waits (up to ~70s) for a slot to fire
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MonitorConfig;

static class Smoke
{
    static int passed;

    static void Check(bool cond, string name, string extra = "")
    {
        if (!cond) throw new Exception($"FAIL: {name} {extra}");
        passed++;
        Console.WriteLine($"  ok  {name}");
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    static void Press(byte vk) // Ctrl+Alt+<vk>; PgUp/PgDn are extended keys
    {
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 1, UIntPtr.Zero);
        keybd_event(vk, 0, 3, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }

    static Api api = null!;

    static async Task<(int status, JsonElement body)> Call(string method, string path, object? body = null, string query = "")
    {
        var res = await api.Handle(method, path, query, body == null ? "" : JsonSerializer.Serialize(body));
        return (res.Status, JsonDocument.Parse(res.Json).RootElement);
    }

    static async Task<int> Brightness(bool rescan = true)
    {
        var (_, b) = await Call("GET", "/api/monitors", query: rescan ? "?refresh=1" : "");
        return b.GetProperty("monitors")[0].GetProperty("features").GetProperty("brightness").GetProperty("value").GetInt32();
    }

    static async Task<int> Rev() => (await Call("GET", "/api/ping")).body.GetProperty("rev").GetInt32();

    static async Task<int> Main(string[] args)
    {
        bool full = args.Contains("--full");
        string tmp = Directory.CreateTempSubdirectory("monitorconfig-test-").FullName;
        string settingsPath = Path.Combine(tmp, "settings.json");

        // The controller needs a WinForms UI thread (hotkeys + OSD live there).
        var created = new TaskCompletionSource<Controller>();
        var uiThread = new Thread(() =>
        {
            using (new Control()) { }
            created.SetResult(new Controller(settingsPath, new Autostart("MonitorConfig.SmokeTest", @"C:\fake\MonitorConfig.exe")));
            Application.Run();
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.IsBackground = true;
        uiThread.Start();
        var c = await created.Task;
        api = new Api(c);
        int? original = null;

        try
        {
            Console.WriteLine("monitors");
            await c.StartAsync();
            var (_, list) = await Call("GET", "/api/monitors");
            var monitors = list.GetProperty("monitors");
            Check(monitors.GetArrayLength() >= 1, "at least one monitor found");
            Check(monitors[0].GetProperty("features").GetProperty("brightness").ValueKind == JsonValueKind.Object, "brightness readable over DDC/CI");
            original = await Brightness(false);

            Console.WriteLine("writes");
            Check((await Call("POST", "/api/set", new { id = 0, control = "brightness", value = 30 })).body.GetProperty("ok").GetBoolean(), "set brightness 30");
            Check(await Brightness() == 30, "hardware reads back 30");
            await Task.WhenAll(new[] { 31, 33, 35, 37, 39, 40 }.Select(v => Call("POST", "/api/set", new { id = 0, control = "brightness", value = v })));
            Check(await Brightness() == 40, "burst of writes settles on the last value (40)");
            Check((await Call("POST", "/api/set", new { id = 0, control = "gamma", value = 1 })).status == 400, "unknown control rejected");
            Check((await Call("POST", "/api/set", new { id = 0, control = "brightness", value = -5 })).status == 400, "negative value rejected");
            Check((await Call("POST", "/api/set", "{oops")).status == 400, "malformed body rejected");

            Console.WriteLine("hotkeys");
            var (_, hk) = await Call("POST", "/api/settings", new { hotkeys = true });
            string? hkErr = hk.GetProperty("hotkeyError").GetString();
            Check(hk.GetProperty("hotkeys").GetBoolean() && hkErr == null, "Ctrl+Alt+PgUp/PgDn registered", hkErr ?? "");
            int rev0 = await Rev();
            Press(0x22); await Task.Delay(500);
            Check(c.OsdVisible, "OSD plate shown after the hotkey");
            Check(await Brightness() == 30, "Ctrl+Alt+PgDn: 40 → 30");
            Press(0x21); await Task.Delay(500);
            Check(await Brightness() == 40, "Ctrl+Alt+PgUp: 30 → 40");
            await Task.Delay(1800);
            Check(!c.OsdVisible, "OSD plate hides by itself");
            Check(await Rev() > rev0, "rev bumped so the open window refreshes");
            await Call("POST", "/api/settings", new { hotkeys = false });
            Press(0x22); await Task.Delay(500);
            Check(await Brightness() == 40, "hotkeys off: key press ignored");

            Console.WriteLine("schedule");
            var now = DateTime.Now;
            Check((await Call("POST", "/api/settings", new { schedule = new { enabled = true, items = new[] { new { time = "25:00", value = 5 } } } })).status == 400, "invalid time rejected");
            var (_, s) = await Call("POST", "/api/settings", new
            {
                schedule = new
                {
                    enabled = true,
                    items = new[] { new { time = now.AddHours(-1).ToString("HH:mm"), value = 25 }, new { time = now.AddHours(3).ToString("HH:mm"), value = 90 } },
                },
            });
            Check(s.GetProperty("schedule").GetProperty("enabled").GetBoolean() && s.GetProperty("schedule").GetProperty("items").GetArrayLength() == 2, "schedule saved");
            Check(await Brightness() == 25, "enabling applies the slot in effect now (25)");
            var saved = JsonDocument.Parse(File.ReadAllText(settingsPath)).RootElement;
            Check(saved.GetProperty("schedule").GetProperty("enabled").GetBoolean() && saved.GetProperty("schedule").GetProperty("items")[0].GetProperty("value").GetInt32() == 25, "settings persisted to disk");

            if (full)
            {
                var next = DateTime.Now.AddMinutes(1);
                await Call("POST", "/api/settings", new { schedule = new { enabled = false, items = Array.Empty<object>() } });
                await Call("POST", "/api/settings", new { schedule = new { enabled = true, items = new[] { new { time = next.ToString("HH:mm"), value = 45 } } } });
                await Call("POST", "/api/set", new { id = 0, control = "brightness", value = 20 });
                Console.WriteLine($"  ..  waiting for {next:HH:mm}");
                bool hit = false;
                for (int i = 0; i < 80 && !hit; i++) { await Task.Delay(1000); hit = await Brightness(false) == 45; }
                Check(hit, "slot fires on its own at the minute boundary");
            }
            await Call("POST", "/api/settings", new { schedule = new { enabled = false, items = Array.Empty<object>() } });

            Console.WriteLine("autostart");
            await Call("POST", "/api/settings", new { autostart = true });
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                Check(key?.GetValue("MonitorConfig.SmokeTest") as string == "\"C:\\fake\\MonitorConfig.exe\" --background", "Run key points at the exe with --background");
            Check((await Call("GET", "/api/settings")).body.GetProperty("autostart").GetBoolean(), "autostart reported on");
            await Call("POST", "/api/settings", new { autostart = false });
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                Check(key?.GetValue("MonitorConfig.SmokeTest") == null, "Run key removed");
        }
        finally
        {
            if (original is int o) await Call("POST", "/api/set", new { id = 0, control = "brightness", value = o });
            new Autostart("MonitorConfig.SmokeTest", "").Enabled = false;
            c.Dispose();
            Directory.Delete(tmp, true);
        }

        await AppChecks();

        Console.WriteLine($"\n{passed} checks passed");
        return 0;
    }

    // ---------- the real app ----------

    static async Task AppChecks()
    {
        Console.WriteLine("app");
        string exe = Path.Combine(AppContext.BaseDirectory, "MonitorConfig.exe");
        Check(File.Exists(exe), "MonitorConfig.exe built");
        Check(Process.GetProcessesByName("MonitorConfig").Length == 0, "no other instance running (close it before testing)");

        var app = Process.Start(exe);
        IntPtr hwnd = IntPtr.Zero;
        for (int i = 0; i < 50 && hwnd == IntPtr.Zero; i++) { await Task.Delay(200); app.Refresh(); hwnd = app.MainWindowHandle; }
        Check(hwnd != IntPtr.Zero && app.MainWindowTitle == "Монитор", "window opens");
        await Task.Delay(2500); // let WebView2 render

        var second = Process.Start(exe);
        Check(second.WaitForExit(5000) && Process.GetProcessesByName("MonitorConfig").Length == 1, "second launch hands over to the running instance");

        app.CloseMainWindow();
        Check(app.WaitForExit(5000), "closing the window exits when automation is off");

        var bg = Process.Start(exe, "--background");
        await Task.Delay(2500);
        bg.Refresh();
        Check(!bg.HasExited && bg.MainWindowHandle == IntPtr.Zero, "--background runs without a window");
        bg.Kill();
        bg.WaitForExit(3000);
    }
}
