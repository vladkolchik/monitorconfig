# Monitor Config

[Русский](README.md) · **English**

A compact panel for external monitors on Windows: brightness, contrast and volume over **DDC/CI**, plus automation — a brightness schedule, global hotkeys and starting with Windows.

A single `MonitorConfig.exe` on .NET 7 (WinForms + WebView2) that lives in the tray. The interface is in Russian.

<p>
  <img src="docs/main-light.png" width="300" alt="Main screen, light theme">
  <img src="docs/main-dark.png" width="300" alt="Main screen, dark theme">
  <img src="docs/settings.png" width="300" alt="Automation">
</p>

## Getting started

Download `MonitorConfig.exe` from [Releases](https://github.com/vladkolchik/monitorconfig/releases/latest) and run it — no installation needed.

Requires the [.NET 7 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/7.0) (x64) and the WebView2 Runtime, which usually ships with Windows 10/11.

Building from source (requires the .NET 7 SDK):

```
git clone https://github.com/vladkolchik/monitorconfig
cd monitorconfig
build.cmd
```

The result is `dist\MonitorConfig.exe` — a single file you can put anywhere.

Launching it again while it is already running just brings up its window.

## Controls

| What | How |
| --- | --- |
| Brightness | slider, mouse wheel (±2), arrow keys (±5) |
| Presets | buttons or keys `1`–`4`: 10 / 35 / 65 / 100 |
| Contrast, volume | shown if the monitor supports them |
| Multiple monitors | tabs at the top |

### Automation (the sliders button in the top right)

- **Start with Windows** — adds the exe to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` with the `--background` flag: the app starts in the tray, without a window.
- **Hotkeys** — `Ctrl+Alt+PgUp` / `Ctrl+Alt+PgDn` change the brightness of all monitors by ±10 from any app. A plate with the level pops up at the bottom of the screen for a second; it doesn't take focus and lets clicks through.

  <img src="docs/osd.png" width="300" alt="Brightness plate">
- **Tray only** — the window has no taskbar button and opens from the tray icon. Minimizing hides it to the tray.
- **Schedule** — sets the brightness of all monitors automatically at the given times. When enabled, the level for the current time is applied right away.

While the schedule, hotkeys or tray-only mode are on, closing the window keeps the app in the tray: click the icon to open the window; the menu has brightness presets and Quit («Завершить»). You can also stop it with the Quit button on the automation screen. Without automation, closing the window exits the app.

Settings: `%APPDATA%\MonitorConfig\settings.json`.

## If a monitor "doesn't respond"

- Enable **DDC/CI** in the monitor's on-screen menu (it is often off by default).
- Built-in laptop screens can't be controlled over DDC/CI.
- Some docking stations and USB-C → HDMI adapters don't pass DDC/CI through.

## How it works

```
src/MonitorConfig/
  wwwroot/        UI (plain HTML/CSS/JS, no build step), embedded in the exe as resources
  MainForm.cs     WebView2 window; requests to https://monitorconfig.local/api/* are handled in-process, no network
  Api.cs          JSON API for the UI
  Controller.cs   monitor state, hotkeys, schedule, autostart
  Ddc.cs          DDC/CI via dxva2.dll on a dedicated thread
  Osd.cs          brightness plate
  Program.cs      single instance, tray icon
tests/MonitorConfig.Smoke/   end-to-end test on a real monitor
```

Requests to the monitor go strictly one at a time, and fast slider movements collapse to the latest value — the DDC/CI bus is slow, and this keeps it from choking.

## Tests

```
dotnet build MonitorConfig.sln
dotnet run --project tests/MonitorConfig.Smoke            # API, monitor writes, hotkeys and plate, schedule, tray-only mode, autostart, exe launch
dotnet run --project tests/MonitorConfig.Smoke -- --full  # also waits up to ~70 s for a schedule slot to fire on its own
```

The test works with a real monitor, uses a temporary settings folder and a separate name in the Run key. It presses the hotkeys and restores the original brightness at the end. Close any running MonitorConfig before starting it.

## License

[MIT](LICENSE)
