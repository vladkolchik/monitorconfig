using System.Text.RegularExpressions;

namespace MonitorConfig;

/// <summary>
/// Everything the app does, independent of the window: monitor state, writes, hotkeys, OSD,
/// schedule and autostart. Must be created on a WinForms UI thread (hotkeys and OSD live there).
/// </summary>
public sealed partial class Controller : IDisposable
{
    const int HotkeyStep = 10;

    readonly string settingsPath;
    readonly SynchronizationContext ui;
    readonly Hotkeys hotkeys = new();
    readonly Osd osd = new();
    readonly System.Threading.Timer scheduleTimer;
    readonly object sync = new();
    string? lastFired;

    public DdcBus Ddc { get; } = new();
    public Autostart Autostart { get; }
    public Settings Settings { get; private set; }
    public List<MonitorInfo> Monitors { get; private set; } = new();
    public int Rev { get; private set; } // bumped when something other than the UI changes a value
    public string? HotkeyError { get; private set; }

    public event Action? QuitRequested;
    public event Action? Changed;

    public Controller(string settingsPath, Autostart autostart)
    {
        this.settingsPath = settingsPath;
        Autostart = autostart;
        ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Controller needs a UI thread");
        Settings = Settings.Load(settingsPath);
        hotkeys.Pressed += id => _ = OnHotkey(id);
        scheduleTimer = new System.Threading.Timer(_ => _ = TickSchedule(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Read monitors, then switch on whatever the saved settings ask for.</summary>
    public async Task StartAsync()
    {
        await Refresh();
        if (Settings.Hotkeys) ApplyHotkeys();
        await ApplyCurrentScheduleItem();
        scheduleTimer.Change(0, 10_000);
    }

    public bool BackgroundWanted => Settings.Hotkeys || Settings.Schedule.Enabled;

    public void RequestQuit() => QuitRequested?.Invoke();

    // ---------- monitors ----------

    public async Task<List<MonitorInfo>> Refresh()
    {
        Monitors = await Ddc.ListAsync();
        return Monitors;
    }

    public async Task<bool> SetControl(int id, string control, uint value)
    {
        bool ok = await Ddc.SetAsync(id, DdcBus.Vcp[control], value);
        var f = id >= 0 && id < Monitors.Count ? Monitors[id].Features.GetValueOrDefault(control) : null;
        if (ok && f != null) f.Value = value;
        return ok;
    }

    /// <summary>Set brightness on every monitor, as a percentage of each monitor's own range.</summary>
    public async Task SetAllBrightness(Func<int, int> percent)
    {
        var jobs = Monitors
            .Where(m => m.Features.GetValueOrDefault("brightness") != null)
            .Select(m =>
            {
                var f = m.Features["brightness"]!;
                int p = Math.Clamp(percent((int)Math.Round(f.Value * 100.0 / f.Max)), 0, 100);
                return SetControl(m.Id, "brightness", (uint)Math.Round(p / 100.0 * f.Max));
            });
        await Task.WhenAll(jobs);
        Rev++;
        Changed?.Invoke();
    }

    public int? PrimaryBrightnessPercent()
    {
        var m = Monitors.FirstOrDefault(x => x.Primary && x.Features.GetValueOrDefault("brightness") != null)
             ?? Monitors.FirstOrDefault(x => x.Features.GetValueOrDefault("brightness") != null);
        if (m == null) return null;
        var f = m.Features["brightness"]!;
        return (int)Math.Round(f.Value * 100.0 / f.Max);
    }

    // ---------- hotkeys + OSD ----------

    async Task OnHotkey(int id)
    {
        // snap to the step grid so 23 → 30 / 20 rather than 33 / 13
        const int s = HotkeyStep;
        await SetAllBrightness(p => id == Hotkeys.Up ? p / s * s + s : (p + s - 1) / s * s - s);
        if (PrimaryBrightnessPercent() is int p) ui.Post(_ => osd.Pop(p), null);
    }

    public bool OsdVisible
    {
        get { bool v = false; ui.Send(_ => v = osd.IsShowing, null); return v; }
    }

    void ApplyHotkeys()
    {
        ui.Send(_ =>
        {
            HotkeyError = null;
            if (!Settings.Hotkeys) hotkeys.Unregister();
            else if (!hotkeys.Register()) HotkeyError = "hotkey is taken by another app";
        }, null);
    }

    // ---------- schedule ----------

    static int Minutes(string t) => int.Parse(t[..2]) * 60 + int.Parse(t[3..]);

    ScheduleItem? CurrentItem(DateTime now)
    {
        var items = Settings.Schedule.Items.OrderBy(i => Minutes(i.Time)).ToList();
        if (items.Count == 0) return null;
        int cur = now.Hour * 60 + now.Minute;
        return items.LastOrDefault(i => Minutes(i.Time) <= cur) ?? items[^1];
    }

    async Task TickSchedule()
    {
        ScheduleItem? item;
        string stamp;
        lock (sync)
        {
            if (!Settings.Schedule.Enabled) return;
            var now = DateTime.Now;
            string hhmm = now.ToString("HH:mm");
            item = Settings.Schedule.Items.FirstOrDefault(i => i.Time == hhmm);
            stamp = now.ToString("yyyy-MM-dd ") + hhmm;
            if (item == null || lastFired == stamp) return;
            lastFired = stamp;
        }
        await SetAllBrightness(_ => item.Value);
    }

    /// <summary>When the schedule is switched on, jump to the level that is in effect right now.</summary>
    async Task ApplyCurrentScheduleItem()
    {
        var item = Settings.Schedule.Enabled ? CurrentItem(DateTime.Now) : null;
        if (item != null) await SetAllBrightness(_ => item.Value);
    }

    // ---------- settings API ----------

    public object PublicSettings() => new
    {
        autostart = Autostart.Enabled,
        hotkeys = Settings.Hotkeys,
        hotkeyError = HotkeyError,
        schedule = Settings.Schedule,
    };

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex TimeRx();

    public static bool ValidSchedule(Schedule? s) =>
        s != null && s.Items.Count <= 12 && s.Items.All(i => TimeRx().IsMatch(i.Time) && i.Value is >= 0 and <= 100);

    public async Task UpdateSettings(bool? autostart, bool? hotkeysOn, Schedule? schedule)
    {
        if (autostart is bool a) Autostart.Enabled = a;
        if (hotkeysOn is bool h) { Settings.Hotkeys = h; ApplyHotkeys(); }
        if (schedule != null)
        {
            bool wasOn;
            lock (sync)
            {
                wasOn = Settings.Schedule.Enabled;
                Settings.Schedule = new Schedule
                {
                    Enabled = schedule.Enabled,
                    Items = schedule.Items.Select(i => new ScheduleItem { Time = i.Time, Value = i.Value }).ToList(),
                };
            }
            if (Settings.Schedule.Enabled && !wasOn) await ApplyCurrentScheduleItem();
        }
        Settings.Save(settingsPath);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        scheduleTimer.Dispose();
        ui.Send(_ => { hotkeys.Dispose(); osd.Dispose(); }, null);
        Ddc.Dispose();
    }
}
