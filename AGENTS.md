# AGENTS.md

Guidance for AI coding agents (and humans) working on Glint's source. If you are an assistant *using* a running
Glint over MCP, read [`docs/AI-GUIDE.md`](docs/AI-GUIDE.md) instead.

## What Glint is

A Windows tray app for RGB lighting, fan control, hardware monitoring and (opt-in) AI overclocking, built to use as little CPU and memory
as possible. C# / .NET 10 WinForms, a WebView2 dashboard that only exists while it is open, device support in
sandboxed JavaScript plugins.

## Layout

| Path | What lives there |
|---|---|
| `src/Glint/Program.cs` | Entry point and every command (`selftest`, `controltest`, `webtest`, `plugincheck`, `plugintest`, `ctl`, `mcp`, benches). |
| `src/Glint/Core/` | `GlintService` (owns everything), `Rig` (device discovery + LibreHardwareMonitor sensors + sensor catalog), `FanEngine`, `LightingEngine` + `Effects` + `Layout`, `Config` + `ConfigSchema` (schema + `ConfigValidator`), `ControlServer` (named-pipe API) + `Cli` (ctl + MCP), `HomeAssistant` + `Mqtt`, `SetupCheck` (on-demand setup report), `UpdateCheck` (optional GitHub releases check, the repo URL lives here), `SelfTest` (+ `SelfTestV02`). |
| `src/Glint/Devices/` | Device interfaces (`ILightDevice`, `IFanController`), the driver registry, SMBus access via PawnIO, the OpenRGB SDK bridge (`OpenRgbBridge`; tests use an in-process fake server on 127.0.0.1 only). |
| `src/Glint/Plugins/` | Plugin host: Jint for device plugins, one shared V8 runtime for effect plugins, trust store, `PluginCheck` (simulated-device tests). `devices/` and `effects/` hold the bundled plugins. |
| `src/Glint/Tuning/` | Overclocking, off by default: `TuningService` (owns the GPU, the safety rails and tuning-state.json), `Nvml` / `CudaStress` (native NVIDIA layer) with `SimulatedGpu` for tests, `CrashWatch` + `CrashEvents` (event-log crash detection and back-off), `CpuTuning` (Ryzen Curve Optimizer search through the bundled CoreCycler, fetched by `tools/fetch-corecycler.ps1` into the git-ignored `src/Glint/third_party/`; its thermal watchdog is a timer that exists only while a search runs), `AiTuner` (Claude Code over a tuning-only MCP server), `TuningLimits` (hard caps). |
| `src/Glint/Overlay/` | Game overlay: ETW present-event FPS (no injection), frame statistics, layered click-through window. |
| `src/Glint/UI/` | Tray (`TrayApp`) and the WebView2 host (`WebDashboard`). |
| `src/Glint/web/` | Dashboard: plain HTML/CSS/JS, no build step. `mock.js` fakes the host with a fictional demo rig. |
| `src/Glint/plugin-templates/` | `PLUGINS.md` (the plugin API, written to be pasted into an AI chat) and templates. |
| `tools/` | Headless dashboard tests (`uitest.js`) and screenshots (`screenshots.js`), puppeteer-core. |
| `installer/` | Inno Setup script + helper for `Glint-Setup-x.y.z.exe`. |

## Build and test

```powershell
dotnet build src/Glint/Glint.csproj -c Release
$g = 'src\Glint\bin\Release\net10.0-windows\Glint.exe'
& $g selftest selftest.json        # offline: logic, effects, layout, plugins on simulated devices, HA against a fake broker, overlay
& $g controltest control.json      # control API / ctl / MCP against a private pipe and a temp config folder
& $g webtest web.json              # the real WebView2 dashboard, off-screen
& $g plugincheck all               # every plugin's built-in tests
node tools/uitest.js <absolute path to src/Glint/web/index.html> ui.json   # dashboard vs mock.js (CHROME_PATH)
```

Every command writes a JSON/text report and exits with the number of failures. CI runs the build, `selftest` and
`plugincheck`; keep them green and add checks for what you change. A check that can't fail is not a check: prove
new checks fail against broken code.

## Hard rules

1. **Never touch real hardware from tests or automation.** No `selftest --hw`, `plugintest`, or running the tray
   app unless a human asked for it on their own machine. `plugincheck` and the offline suites use simulated devices.
   Tests must use `GLINT_CONFIG_DIR` / `GLINT_PIPE` (temp folder, private pipe), never the user's
   `%APPDATA%\Glint` or the running app's pipe.
2. **Stay light.** No new always-on threads, timers, polling or per-frame allocations without measuring the cost
   (`engbench`, `devbench`, `sensorbench`, `overlaybench`). Features that cost something run only while they are
   used (the Sensors page catalog, the overlay's ETW session, audio capture, the dashboard itself).
3. **Overclocking safety.** Tuning is off by default and every limit lives in `TuningService` / `TuningLimits` /
   `Nvml.CheckOffset`, never in the caller (dashboard, CLI, MCP, AI). Tests never load `nvml.dll` / `nvcuda.dll`: use
   `SimulatedGpu`. The AI tuner gets no built-in Claude Code tools and only the `tuning.*` MCP tools.
4. **Device safety.** Plugins identify the device before writing, never send firmware/bootloader/save-to-flash
   commands, fan plugins hand control back in `shutdown()`, SMBus plugins declare addresses and never touch
   0x50–0x57. Fan fail-safe (100% on a lost sensor) must always win over overrides.
5. **One validated path for config.** Dashboard, tray, CLI, MCP and Home Assistant all change settings through
   `GlintService.Apply` after `ConfigValidator`. New settings need an entry in `ConfigSchema` and validation.
6. **No personal data.** No real usernames, paths, hostnames, serials or someone's actual rig in code, tests, mock
   data, screenshots or logs. The demo rig in `mock.js` is fictional.
7. **Don't reset user layouts.** Auto-arrange only fills in zones without a saved position; replacing a layout
   needs explicit confirmation and makes a backup.

## Adding a bundled device plugin

Follow `src/Glint/plugin-templates/PLUGINS.md` and [`CONTRIBUTING.md`](CONTRIBUTING.md): one file per device family
in `src/Glint/Plugins/devices/<vendor>-<family>.js`, protocol ported from a cited open-source reference, built-in
`tests` with expected bytes derived from that reference, and an `expectDeclined` test. `plugincheck` must pass.
Mark it verified only if it was tested on the real device.

## Dashboard

Plain JS, no framework, no build. Keep the visual language in `styles.css` (`:root` tokens). Every UI change gets
`tools/uitest.js` checks; regenerate README screenshots with `node tools/screenshots.js` and look at them.
