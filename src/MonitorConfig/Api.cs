using System.Text.Json;

namespace MonitorConfig;

/// <summary>
/// The small JSON API the web UI talks to. Requests never leave the process: the window intercepts
/// https://monitorconfig.local/api/* and hands them here.
/// </summary>
public sealed class Api
{
    readonly Controller c;

    public Api(Controller controller) => c = controller;

    public sealed record Response(int Status, object Body)
    {
        public string Json => JsonSerializer.Serialize(Body, Settings.Json);
    }

    sealed class SetBody { public int? Id { get; set; } public string? Control { get; set; } public double? Value { get; set; } }
    sealed class SettingsBody { public bool? Autostart { get; set; } public bool? Hotkeys { get; set; } public bool? TrayOnly { get; set; } public Schedule? Schedule { get; set; } }

    static Response Ok(object body) => new(200, body);
    static Response Bad(string error) => new(400, new { ok = false, error });

    public async Task<Response> Handle(string method, string path, string query, string body)
    {
        switch ($"{method} {path}")
        {
            case "GET /api/monitors":
                if (query.Contains("refresh") || c.Monitors.Count == 0) await c.Refresh();
                return Ok(new { rev = c.Rev, monitors = c.Monitors });

            case "POST /api/set":
            {
                var b = Parse<SetBody>(body);
                if (b?.Id is not int id || b.Control is not string control || !DdcBus.Vcp.ContainsKey(control) ||
                    b.Value is not double raw || double.IsNaN(raw) || raw < 0 || raw > 65535)
                    return Bad("bad request");
                return Ok(new { ok = await c.SetControl(id, control, (uint)Math.Round(raw)) });
            }

            case "GET /api/settings":
                return Ok(c.PublicSettings());

            case "POST /api/settings":
            {
                var b = Parse<SettingsBody>(body);
                if (b == null) return Bad("bad request");
                if (b.Schedule != null && !Controller.ValidSchedule(b.Schedule)) return Bad("bad schedule");
                await c.UpdateSettings(b.Autostart, b.Hotkeys, b.TrayOnly, b.Schedule);
                return Ok(c.PublicSettings());
            }

            case "GET /api/ping":
                return Ok(new { rev = c.Rev });

            case "POST /api/quit":
                c.RequestQuit();
                return Ok(new { ok = true });

            default:
                return new(404, new { error = "not found" });
        }
    }

    static T? Parse<T>(string body) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(body) ? "{}" : body, Settings.Json); }
        catch (JsonException) { return null; }
    }
}
