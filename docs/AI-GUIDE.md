# Glint for AI agents

This guide is for an AI assistant connected to a user's running Glint, through the built-in MCP server or the
`Glint.exe ctl` command line. It explains how to do common jobs safely. (Working on Glint's own source code
instead? Read [`AGENTS.md`](../AGENTS.md).)

## Connecting

MCP (Claude Desktop, Claude Code, Cursor, any MCP client):

```json
{ "mcpServers": { "glint": { "command": "C:\\Program Files\\Glint\\Glint.exe", "args": ["mcp"] } } }
```

Every control method is a tool named `glint_<method>` (dots become underscores, e.g. `glint_fans_list`).
Without MCP, use the same methods from a terminal: `Glint.exe ctl <method> key=value ...`, or pass JSON
(`Glint.exe ctl lighting.set '{"target":"all","effect":{"type":"comet"}}'`), or a file (`value=@file.json`).

The API talks to the Glint that is running on this PC. It only listens to the current Windows user.

## Ground rules

1. **Look before you change.** Call `status`, `devices`, `fans.list` or `config.get` first, and `config.schema`
   when you aren't sure what a setting means or what values it allows.
2. **Writes are validated.** Invalid changes come back as `{ "ok": false, "errors": [...] }` and nothing is saved.
   Read the errors and fix the request; don't work around validation by editing `config.json` directly.
3. **Respect the user's hand-tuned layout.** Never call `lighting.autoLayout` unless the user asked for it. It
   needs `confirm=true`; the old layout is backed up (`lighting.layoutBackups`, `lighting.restoreLayout`).
4. **Fans are safety equipment.** Don't set pumps or fans below the rule's minimum, and never remove the CPU or
   liquid input from the rules that cool them unless the user asks. Glint's fail-safe (100% when a sensor is lost)
   always wins over manual overrides.
5. **Device plugins talk to real hardware.** Follow the plugin workflow below. Never send firmware-update,
   bootloader or save-to-flash commands.
6. **Tell the user what you changed**, and how to undo it.

## Recipe: add support for a new device

The goal is a single JavaScript device plugin. The full API is in `plugins.docs`; read it first.

1. **Find the device.** `hid.list` (optionally `vendorId=<decimal vid>`) lists every USB HID device: VID, PID,
   interface, usage page, report sizes, and which plugin claims it, if any. Confirm with the user which one it is.
2. **Find the protocol.** Ask for, or look up, an open-source driver for that VID/PID: OpenRGB
   (`Controllers/`), liquidctl (`liquidctl/driver/`), OpenLinkHub (`src/devices/`) or a Linux kernel driver. Port
   the logic; don't guess packets.
3. **Start from the template.** `plugins.template kind=device`, or a similar bundled plugin from `plugins.list`
   + `plugins.get file=<name>.js` (for example `corsair-lighting-node.js` for a write-only controller,
   `corsair-commander-core.js` for request/response with fans and temperatures, `logitech-g213.js` for HID++).
4. **Write the plugin with tests.** Identify the device in `init` and `return false` if it isn't the expected
   one. Add a `tests: [...]` block whose expected bytes come from the reference driver (cite it in a comment),
   plus an `expectDeclined` test for the wrong interface or report size.
5. **Add and check it.** `plugins.add file=<vendor>-<family>.js code=<source>` saves it to the user's plugin
   folder and reports load errors. `plugins.check file=<name>.js` runs its tests against a simulated device.
   Iterate until every test passes. Nothing has touched the hardware yet.
6. **Load it.** Device plugins load on start: `app.restart`, then `log` and `devices` to confirm it was found.
   If it says "init declined", the identification didn't match; check `hid.list` again.
7. **Confirm on the real device.** `lighting.identify target=<device id>` flashes it white for a few seconds;
   ask the user whether the right device flashed. For fans, read `status` and check RPM changes after
   `fans.override`.
8. **If anything misbehaves** (device stops responding, wrong colours, fans don't react): `plugins.setEnabled
   file=<name>.js enabled=false`, then `app.restart`. Unplugging and replugging the device resets most
   controllers.

SMBus plugins (RAM sticks, some motherboard controllers) also need the user to approve them on the dashboard's
Plugins page; the API cannot approve them. They can only use the addresses they declare, must read-identify a
device before writing, and can never touch the SPD EEPROM (0x50–0x57).

## Recipe: add a lighting effect

1. `plugins.template kind=effect` and `plugins.docs` (Effect plugins section).
2. Write `render(ctx, leds, out)`. Each LED has `x`/`y` (its position on the user's layout canvas) and `u` (its
   position within its own zone), so effects can sweep across the whole PC.
3. `plugins.add file=<name>.js code=<source>`. Effect plugins load immediately; they appear in the dashboard's
   effect gallery and can be applied with `lighting.set` (`{"type":"plugin","plugin":"<plugin name>"}`).

## Recipe: change lighting

- Everything: `lighting.preset name=<preset>` (`lighting.presets` lists them), or
  `lighting.set target=all effect={"type":"colorWave","colors":["#ff0040","#00a0ff"],"speed":0.3}`.
- One device or zone: `lighting.set target=<device id | zone id> effect=...` (ids from `devices`). This creates an
  override; `config.patch path=lighting.overrides.<id> value=null` removes it again.
- Brightness: `lighting.brightness value=60`. Lights off: `config.patch path=lighting.enabled value=false`.
- Effect types and their parameters are listed in `config.schema`.

## Recipe: tune fans

1. `fans.list` shows each profile's rules: which fans, which sensors with which curves, how they combine, min/max,
   ramps, any manual override, and live duty/RPM. It also lists the sensor ids you can use as inputs.
2. Change a curve: `curves.set name=gpu-quiet points=[[50,25],[70,45],[80,70],[88,100]]` (temperature °C → duty %,
   temperatures increasing).
3. Change a rule: `fans.setRule profile=Quiet rule={"name":"Radiator fans","maxDuty":80}` (partial fields merge).
   Make a rule follow more sensors by adding inputs; with `combine: "max"` the hottest one wins.
4. Hold fans at a speed for a while: `fans.override name="Radiator fans" percent=80`; back to automatic with
   `percent=null`.
5. Experiment safely: `profiles.create name=Test from=Balanced`, edit the copy, `profile.set name=Test`.
6. Graphics-card fans are outputs too: `gpu:Fan 1`, `gpu:Fan 2` (a second card is `gpu2:`). While no rule names
   them they stay on the GPU driver's own curve; put them in a rule (usually input `gpu` or `gpu.hotspot`) to drive
   them, remove them to hand control back. Some cards have a minimum duty (often 30%); lower requests are raised to it.
7. If another program (MSI Afterburner's fan curve, FanControl, a vendor tool) changes a fan Glint drives through
   LibreHardwareMonitor (board header, cooler, GPU fan), Glint notices from the level the hardware reports, hands that
   fan back to automatic control and stops driving it (a fail-safe duty is still written). `fans.list` explains it and
   `setup.check` reports it; Glint tries again every 10 minutes, or take the fan out of its rule and back in. Hub fans (Corsair...) report no
   duty, so for those `setup.check` flags the conflicting app instead.
8. Turn all fan control off: `config.patch path=fanControl value=false` (tray: Fan profile → Fan control off). Board,
   cooler and GPU fans go back to their BIOS / driver curves; hubs go back to their firmware only if their plugin can
   release fans without the lighting (`releaseFans`), otherwise they stay on Glint's curves.

## Recipe: sensors and the game overlay

- `sensors.list`: friendly sensors used by fan curves (`cpu`, `gpu`, `liquid`, ...). `sensors.all`: every
  hardware sensor (clocks, loads, power, voltages, ...) with min/max, like HWMonitor.
- Overlay: `overlay.get`, then `overlay.set` with only the fields to change, e.g.
  `{"items":[{"source":"fps"},{"source":"low1"},{"source":"gpu"},{"source":"<sensor id from sensors.all>","label":"GPU clock"}]}`.
  `overlay.toggle` shows or hides it; `overlay.stats` returns the live FPS of the game in front.

## Recipe: updates

- `update.status`: current version, `latest`, `available`, `canInstall` (false for a portable or non-elevated copy,
  with `cannotInstall` saying why), `autoInstall`, and `install` = `{ state: idle|downloading|verifying|installing|failed, message }`.
- `update.install` with `confirm=true` **only after the user agreed**: it downloads the setup from Glint's GitHub
  release, verifies it (SHA256SUMS.txt + GitHub build attestation from the release workflow for that tag) and runs
  it silently. Glint closes and restarts, so the pipe drops; that is expected. Poll `update.status` until then; on
  `failed`, relay `install.message` (e.g. an older release without an attestation: point the user to the release page).
- Automatic install: `config.patch path=updates.autoInstall value=true` (needs `updates.check`); it only runs when the
  PC is idle for 10 minutes, no game is in front and it's not on battery.

## Troubleshooting

- **Start with `setup.check`.** It returns one report: admin rights, the PawnIO driver, WebView2, whether the CPU
  temperature is readable, other RGB / fan apps running or starting with Windows (SignalRGB, iCUE, Armoury Crate,
  MSI Center, NZXT CAM, L-Connect, Synapse, G HUB, OpenRGB, FanControl), plugins needing approval or failing,
  devices that declined, the startup task, version + update, and the OpenRGB bridge. Each item has `status`
  (ok / warn / fail), a plain-language `detail` and a `fix` (`restartAdmin`, `openUrl`, `startupApps`, `page`,
  `restart`, `installUpdate` = `update.install`). Relay the failures to the user in their words; most fixes (installing PawnIO, quitting another app,
  approving a plugin) need the user, not the API.
- No CPU temperature / board fans: Glint isn't elevated or PawnIO isn't installed (`setup.check` says which).
- Flickering colours or fans jumping: another app drives the same device (`setup.check` lists it).
- LED strip / ARGB header the wrong length: the user sets `lighting.ledCounts.<zone>` (or the Devices page, whose
  test pattern shows white on the first LED and red on the last); then `app.restart`.
- Device not supported but OpenRGB knows it: `config.patch path=openRgb.enabled value=true` (with OpenRGB's SDK
  server running), then `app.restart`; its devices appear as `orgb-…` in `devices`.
- `log`: recent device and plugin messages (identification, errors, declined devices).
- `status`: live temperatures, fan speeds, per-device lighting frame rates and Glint's own CPU/memory use.
- `ha.status`: the Home Assistant connection.
- After a bad change: `config.get` shows what is applied; layouts can be restored from backups; plugins can be
  disabled. When in doubt, ask the user before changing more.
