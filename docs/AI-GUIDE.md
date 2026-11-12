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
   Tested on NVIDIA; AMD/Intel depend on LibreHardwareMonitor (README → Graphics card fans). A Radeon fan stopped at
   ≤ 40% is its zero-RPM mode, not a conflict.
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

## Recipe: overclocking (only when the user asks)

Tuning is off by default (`tuning.enabled`); only turn it on when the user asks for overclocking. Every limit is
enforced by Glint, so a wrong request fails instead of crossing a limit. The goal is the best *sustained* performance
that is stable: a card that reaches its own temperature or power limit throttles, and the test is flagged
`thermalLimited` / `powerLimited` (a limit to optimise under, not a failure). Only emergency temperatures stop a test.
Clock offsets only: no voltage control, and the GPU power limit is raised only to the card's own maximum
(`tuning.unlockPower`). `tuning.status` also reports the running test (`testing`), a live sample (`live`) and
`compare` (stock vs the best tuned result); a monitor window shows the same to the user while a test runs.

1. `tuning.status`: the GPU, whether it may be tuned (`gpu.blocked` says why not: tuning off, a workstation card
   without `tuning.allowWorkstation`, no offsets in the driver), this card's limits, the saved result and the last 30 tests.
2. `tuning.gpu.test { core, memAb, seconds, light? }` starts a stress test (memory in MSI Afterburner units) and returns
   at once. Call `tuning.wait` until `busy` is false and read `result`: `ok`, `why`, clock under load, peak
   temperatures, VRAM bandwidth (`gBps`). The card is back at stock after every test.
3. Never retry a setting the history shows as failed, including "the PC crashed or froze during this test".
   Find the core edge first (steps of 50, then 15 MHz), confirm it with a `light` test, then memory in steps of 250
   while bandwidth still improves, then a 180 s soak of the combination.
4. `tuning.gpu.save` + `tuning.gpu.apply` with a margin (core −30, memory −250). Both refuse anything a full test
   didn't prove on this driver.
5. `tuning.crashes` shows what crash watch saw and did (back-off, revert to stock) and CPU cores to loosen.

CPU (AMD Ryzen 5000/7000/9000 only; Intel CPUs have nothing to tune, `tuning.cpu.status` shows the 13th/14th gen microcode advisory):

6. `tuning.cpu.status`: whether this CPU can be tuned (`curveOptimizer.tunable` / `why`), a sensible `startHint`, and the
   search so far (per-core `values`, `adjustments`, `failedCores`, `wheaSinceStart`, `summaryFound`, the BIOS sheet).
7. `tuning.cpu.start { startValue }` (-50..0; Ryzen 5000 allows -30 at most) starts the CPU tune (sweep, per-core check, soak)
   with y-cruncher, in about 30-40 minutes, and resumes by itself after a freeze (the user presses reset) or reboot. Glint
   stops at the CPU temperature limit or when the temperature sensor fails. Start it and end the run; don't poll.
   How it works (all inside Glint, hidden, no console windows):
   - **Sweep**: one all-core y-cruncher run stays up while every core is set to 0, -5, -10... in turn through the SMU (the
     reading that spans each change is dropped, the next two kept). Separate launches of the same test differ by up to ~20%
     on a 9950X3D; readings inside one run agree to ~1%, so values are only compared within a run. It stops at the first
     error or when scores fall two steps in a row, re-measures no-undervolt and the best three in the same run, then measures
     those on one core twice each, alternating, in one single-core run. Performance mode keeps the fastest value (60%
     all-core, 40% single-core); Efficiency mode the best speed per watt within 5% of stock.
   - **Each core alone** at that value (60 s, the full SFTv4 / FFTv4 / N63 mix, pinned to one logical CPU; an error loosens
     that core by 3).
   - **Soak**: every core together for 5 minutes with FFTv4 (the in-cache SFTv4 pins a whole CPU at its thermal limit within
     seconds, so it only runs per core). Reaching 3 °C under the limit after 60% of the time counts as passed; earlier means
     cool down and retry, twice = stop with "cooling limits it". An error loosens every core by 2.
   - **Finish**: final = found + 3 (never positive), saved into the tune's profile, then the BIOS values and the result are
     alternated inside one all-core and one single-core run for the profile's stock vs tuned figure (performance mode backs
     off 5 at a time if it measures slower). A stopped search puts the BIOS values back and re-applies the active profile's.
   A freeze mid-test is recorded before the value is set: the next start marks that value failed (sweep), loosens that core
   (per-core) or every core (soak) and resumes after 90 s.
8. `tuning.cpu.finish` returns the final values and a BIOS sheet for the board maker (the optional permanent route).
   Glint applies the result through the SMU whenever that profile is active (re-applied at every start, never right after
   a crash; two crashes in a row remove it) and puts the BIOS values back for profiles without one. `tuning.cpu.stop` ends
   the search early.
   Every AI run ends with its result saved: if the AI didn't save the GPU result itself, Glint saves the best full test of
   the run, ranked by work done (compute rate, then VRAM bandwidth), never by clock. `tuning.gpu.verify` then tests it and
   stock back to back (performance: kept if 1.5%+ faster with compute not below stock; efficiency: 5%+ more work per watt
   and no more than 10% slower), lowering or removing it otherwise.
   GPU efficiency mode: `tuning.gpu.test` / `tuning.gpu.save` take `powerPct` (50-100, % of the card's default power limit);
   the saved power limit is applied with the profile.
   `tuning.cpu.loadtest {seconds}` is an all-core y-cruncher run (20-90 s) that returns peak / sustained temperature vs the
   limit, package power and clocks, and the PPT / TDC / EDC use from the CPU's own power table, for a PBO limits / boost
   override recommendation (BIOS settings Glint can't set).
   `tuning.cpu.status` `power` = the limits in effect, read from the CPU's SMU power table through LibreHardwareMonitor
   (`raised` = the BIOS has PBO limits raised). The boost override can't be read.
   `tuning.fans.history {hours}` = temperatures and each fan rule's duty over the last 24 h of real use (a sample every 30 s,
   kept across restarts), with the sustained-load periods (5+ min: games, renders). Base fan curves on it.
   The dashboard's Stock row stops everything and holds stock clocks + the BIOS CPU values (also at startup) until a profile
   is picked or a tune starts.
   `tuning.cpu.check` is read-only: it reads the current per-core offsets to tell whether Windows can reach the CPU's SMU on
   this PC (`ok`, `offsets`, or why not + `fix`, with the Memory Integrity / PawnIO state), the PBO scalar and the power
   table. Virtualization (SVM) on is fine; with Memory Integrity on, the tool needs PawnIO. It never writes and is skipped
   while a search runs.

The dashboard's **Tune with AI** button runs exactly this with Claude Code and a tuning-only MCP server
(`Glint.exe mcp --only tuning`).

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
- `notifications.list`: the Windows notifications Glint raised since it started (a crash and what crash watch did, a
  saved overclock that was not applied at startup and why, fans at fail-safe speed, a device that stopped, an update),
  newest first, each with `shown` and, when a switch in `config.notifications` kept it back, `muted`. Read it when the
  user asks "what was that notification?" or "why isn't my overclock applied?". `notifications.test` sends one;
  `config.patch path=notifications.<kind> value=false` (kinds: crashes, tuning, cooling, devices, updates) or
  `notifications.enabled` switches them off.
- `log`: recent device and plugin messages (identification, errors, declined devices).
- `status`: live temperatures, fan speeds, per-device lighting frame rates and Glint's own CPU/memory use.
- `ha.status`: the Home Assistant connection.
- After a bad change: `config.get` shows what is applied; layouts can be restored from backups; plugins can be
  disabled. When in doubt, ask the user before changing more.
