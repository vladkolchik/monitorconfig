using System.Text.Json;
using Microsoft.Win32;

namespace MonitorConfig;

public sealed class ScheduleItem
{
    public string Time { get; set; } = "12:00";
    public int Value { get; set; }
}

public sealed class Schedule
{
    public bool Enabled { get; set; }
    public List<ScheduleItem> Items { get; set; } = new();
}

public sealed class Settings
{
    public bool Hotkeys { get; set; }
    public bool TrayOnly { get; set; } // no taskbar button; the window opens from the tray icon
    public Schedule Schedule { get; set; } = new()
    {
        Items =
        {
            new() { Time = "08:00", Value = 65 },
            new() { Time = "19:00", Value = 35 },
            new() { Time = "22:30", Value = 10 },
        },
    };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Settings Load(string path)
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new(); }
        catch { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>Start with Windows via the per-user Run key — the standard mechanism, no scripts.</summary>
public sealed class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    readonly string valueName;
    readonly string command;

    public Autostart(string valueName, string exePath)
    {
        this.valueName = valueName;
        command = $"\"{exePath}\" --background";
    }

    public bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(valueName) is string s && s == command;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(valueName, command);
            else key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    public string Command => command;
}
