# Contributing to Glint

Thanks for helping! The most valuable contributions are **device plugins**: every new plugin makes Glint
useful to more people.

## Adding a device

Using an AI? Point it at [docs/AI-GUIDE.md](docs/AI-GUIDE.md) (connected to a running Glint over MCP) or
[AGENTS.md](AGENTS.md) (working in this repo).

1. Read [`src/Glint/plugin-templates/PLUGINS.md`](src/Glint/plugin-templates/PLUGINS.md). It's written so you can
   paste it into an AI chat along with your device's VID/PID and a link to an open-source driver for it.
2. Start from `device-template.js`, or copy the closest bundled plugin in `src/Glint/Plugins/devices/`.
3. Give the plugin **built-in `tests`** whose expected bytes come from the reference driver (OpenRGB, liquidctl,
   OpenLinkHub, a kernel driver) or a USB capture, and cite the reference file + function in a comment.
4. Run `Glint.exe plugincheck path\to\your-plugin.js` (simulated device, no hardware needed) until it passes.
5. If you own the device: `Glint.exe plugintest path\to\your-plugin.js log.txt 30` drives only that device for
   30 s and logs every error. Say in the PR whether it was verified on real hardware.

### Safety rules (PRs that break these won't be merged)

- Identify the device in `init` and `return false` if it isn't what you expect.
- Never send firmware-update, bootloader or save-to-flash commands; direct/software modes only.
- Fan plugins must implement `shutdown()` that hands fans back to the device's own control.
- SMBus plugins declare their addresses, read-identify before writing, and never touch 0x50–0x57 (Glint enforces this).

## Building

Requirements: Windows 10/11 x64, .NET 10 SDK.

```powershell
dotnet build src/Glint/Glint.csproj -c Release
src\Glint\bin\Release\net10.0-windows\Glint.exe selftest selftest.json     # offline checks, simulated devices
src\Glint\bin\Release\net10.0-windows\Glint.exe plugincheck all            # every plugin's built-in tests
powershell -ExecutionPolicy Bypass -File install.ps1                        # install + start (asks for admin)
```

Dashboard work: the UI is plain HTML/CSS/JS in `src/Glint/web/`. Open `index.html` in a browser; `mock.js` fakes the
app with a demo rig. `node tools/uitest.js <abs path to index.html> out.json` (Node 22.12+, after `npm ci` in `tools/`) runs the headless UI tests
(set `CHROME_PATH` if Chrome isn't at the default location).

Building the setup exe (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php); CI does this on tags and in the `Installer test` workflow, which installs and uninstalls it silently):

```powershell
dotnet publish src/Glint/Glint.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o out/publish
ISCC.exe /DVersion=1.2.3 /DPublishDir=..\out\publish installer\glint.iss     # -> out\Glint-Setup-1.2.3.exe
```

The app icon is generated from `docs/brand/logo.svg` with `node tools/make-icon.js`.

## Pull requests

- Keep Glint light: no new background threads or per-frame allocations without a measurement showing the cost.
- Add or update tests for what you change; CI runs the selftest and every plugin's tests.
- Don't include personal data in logs, screenshots, or test fixtures.
