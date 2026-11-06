# Writing Glint plugins

Glint plugins are single JavaScript files. There are two kinds:

- **Effect plugins** add a lighting effect to the gallery.
- **Device plugins** add support for a USB HID device: RGB, fans, or both.

Put plugin files in `%APPDATA%\Glint\plugins\`; the dashboard's **Devices → Plugins → Plugins folder** opens it.

- Effect plugins reload as soon as you save the file.
- Device plugins load when Glint starts (**Devices → Restart Glint**).
- A bundled plugin with the same file name is replaced by yours.

Plugins run in a sandbox: V8 with no file, network or .NET access, a memory cap, and a time limit on every call. A broken plugin shows its error on the Devices page and can't take Glint down.

> **Using an AI to write one:** paste this whole file into your AI chat, then describe what you want.
> For hardware, include the device's VID/PID (Device Manager → Details → Hardware Ids) and a link to an
> existing open-source driver for it (OpenRGB, liquidctl, OpenLinkHub, or a Linux kernel driver), or a
> Wireshark/USBPcap capture of the vendor app changing colours.

---

## Effect plugins

```js
module.exports = {
  type: 'effect',
  name: 'My effect',                 // shown in the gallery (must be unique)
  description: 'One short line',
  preview: ['#ff0000', '#0000ff'],   // colours for the gallery thumbnail
  animated: true,                    // false = only re-rendered on change (static looks)
  params: {                          // the dashboard builds controls from this
    speed:  { type: 'number', label: 'Speed', min: 0, max: 2, step: 0.05, default: 0.5 },
    color:  { type: 'color',  label: 'Colour', default: '#ff8800' },
    colors: { type: 'colors', label: 'Palette', default: ['#ff0000', '#0000ff'] },
    flip:   { type: 'bool',   label: 'Reverse', default: false },
    mode:   { type: 'select', label: 'Mode', options: ['soft', 'hard'], default: 'soft' },
    sensor: { type: 'sensor', label: 'Sensor', default: 'cpu' },
  },
  init(ctx) { ctx.state.phase = 0; },          // optional, once per effect instance
  render(ctx, leds, out) {                     // every frame (up to the fps cap)
    for (let i = 0; i < leds.length; i++) {
      const l = leds[i];
      const c = glint.hsv(l.x * 360 + ctx.time * 60 * ctx.params.speed);
      out[i * 3] = c[0]; out[i * 3 + 1] = c[1]; out[i * 3 + 2] = c[2];
    }
  },
};
```

**`leds[i]`** describes one LED:

- `x` (0–1): position across the whole PC, so one sweep crosses every device.
- `u` (0–1): position within its own zone (around a fan ring, along a strip, or down a keyboard).
- `zone`: the zone's index.
- `ring`: true for fan rings.

**`out`** is a `Uint8ClampedArray` of `leds.length * 3` bytes (R, G, B per LED). Values are clamped to 0–255.

**`ctx`** contains:

- `time` and `dt`, both in seconds.
- `params`: the user's settings. `color` values arrive as `[r,g,b]`; `colors` values as an array of `[r,g,b]`.
- `sensors`: temperatures in °C, e.g. `ctx.sensors.cpu`, `gpu`, `liquid`, `gpu.hotspot`, `storage`, `ram`.
- `spectrum`: 16 audio bands, each 0–1 (only filled while an audio effect is active).
- `level`: overall audio loudness, 0–1.
- `state`: a per-instance object for anything you want to keep between frames.
- `colors`: the effect's base colours, as `[r,g,b]`.

**Helpers** on `glint`:

- `hsv(h, s=1, v=1)` and `hex('#rrggbb')` both return `[r,g,b]`.
- `mix(c1, c2, t)` blends two colours.
- `palette(colors, p)` samples a looping gradient.
- `noise(x, y)` returns smooth noise from 0 to 1; `fbm(x, y, octaves)` is a layered version.
- `clamp(v, a, b)` and `lerp(a, b, t)`.

**Performance:** render runs on V8 with a JIT compiler. A typical effect costs about 0.05–0.2 ms per frame for 550 LEDs. Avoid creating big arrays every frame.

---

## Device plugins

```js
module.exports = {
  type: 'device',
  id: 'myhub',                 // short id; zones become "myhub:<zone>", fans "myhub:<fan>"
  name: 'My RGB Hub',
  vendorId: 0x1234,
  productIds: [0x5678, 0x5679],
  interface: 0,                // optional: USB interface (MI_xx in the hardware id)
  usagePage: 0xff00,           // optional: HID usage page of the right collection
  reportSize: 65,              // optional: minimum output report size (incl. report-id byte)
  featureReports: false,       // optional: true if the device is driven with feature reports
  minGapMs: 0,                 // optional: idle time the device needs between frames

  // Identify the device and describe it. Return false if it isn't what you expect;
  // Glint then tries the next matching HID collection, or leaves the device alone.
  init(dev) {
    if (dev.reportSize !== 65) return false;
    // const fw = (dev.write([0x00, 0x01]), dev.read(200)); if (!fw) return false;
    return {
      zones: [
        { id: 'ch1', name: 'Channel 1', leds: dev.ledCount('ch1', 30), kind: 'strip' },
        { id: 'fan1', name: 'Fan 1', leds: 8, kind: 'ring' },
        { id: 'keys', name: 'Keyboard', leds: 6 * 22, kind: 'matrix', cols: 22 },
      ],
      fans: [ { id: 'fan1', name: 'Fan 1' }, { id: 'pump', name: 'Pump', pump: true } ],
    };
  },

  // Every frame: rgb is a Uint8Array [r,g,b, r,g,b, ...] for all zones' LEDs, in the order declared.
  render(dev, rgb) { /* dev.write([...]) */ },

  // Optional fans/sensors, called about once a second:
  poll(dev) { return { rpm: { fan1: 1200, pump: 2600 }, temps: { liquid: 31.5 } }; },
  setDuty(dev, duty) { /* duty = { fan1: 40, pump: 70 }  (percent) */ },

  // Optional: give control back to the device's own mode when Glint exits.
  shutdown(dev) {},
};
```

**Sensors:** `poll()` works on its own. A plugin with no `zones` and no `fans` that only returns
`{ temps: { ... } }` is a pure sensor, such as a USB thermometer, flow meter or read-only AIO. Its readings
appear as `<id>:<name>` (for example `probe:water`), show on the dashboard, and can drive fan curves.
They're also available to effects as `ctx.sensors['probe:water']`. A sensor named `liquid` also fills the
global `liquid` sensor if nothing else has. Units: temperatures in °C, fan speeds in RPM.

**Zone kinds:** `strip`, `ring` (fans), `matrix` (keyboards: LEDs row by row, and set `cols`), `single`, `stick` (RAM).
`dev.ledCount(zone, default)` lets the user set lengths the device can't report (Devices page).

The **`dev`** API:

| Call | What it does |
|---|---|
| `dev.write(bytes)` | Send an output report. `bytes[0]` is the report id (`0` if the device has none). Short reports are zero-padded. |
| `dev.read(timeoutMs)` | Read one input report as a `Uint8Array` (including the report id), or `null` on timeout. |
| `dev.sendFeature(bytes)` / `dev.getFeature(reportId)` | Feature reports. |
| `dev.vendorId`, `dev.productId`, `dev.product`, `dev.serial` | Device info. |
| `dev.reportSize`, `dev.featureSize` | Report sizes in bytes, including the report-id byte. |
| `dev.log(msg)` | Write to Glint's device log. |
| `dev.sleep(ms)` | Wait (up to 1000 ms). |

**Safety rules for device plugins:**

1. Identify the device in `init` before writing anything that changes it.
2. Never send firmware-update, bootloader or "save to flash" commands. Use the device's direct/software mode.
3. If the device needs a keep-alive, Glint already re-sends a frame at least every 4 seconds.

**SMBus plugins** (RAM sticks and other devices on the motherboard's SMBus) use `type: 'smbus'` and declare
`addresses: [0x19, ...]`. `init(bus)`, `render(bus, rgb)` and the rest get a `bus` object instead of `dev`:

| Call | What it does |
|---|---|
| `bus.readByte(addr, reg)` / `bus.writeByte(addr, reg, value)` | SMBus byte data; a read returns -1 if nothing answered. |
| `bus.readWord(addr, reg)` / `bus.writeWord(addr, reg, value)` | SMBus word data (16-bit, low byte first on the wire). |
| `bus.writeBlock(addr, reg, bytes)` | SMBus block write, up to 32 bytes. |

Glint enforces the safety rules: only the declared addresses, never the SPD EEPROM (0x50–0x57), and no write to
an address until a read from it has answered. SMBus plugins need the user's approval on the Plugins page.

**Built-in tests (strongly recommended).** A device plugin can carry its own proof that it speaks the
protocol correctly, checked against a simulated device. No hardware is needed:

```js
tests: [{
  name: 'firmware query, then one colour packet',
  device: { productId: 0x5678, reportSize: 65, inputSize: 65, interface: 0 },     // what the fake device looks like
  replies: [{ when: '0001', reply: '00010203' }],     // when a written report starts with these hex bytes, the device answers this
  colors: ['#ff0000'],                                // or omit for a ramp: LED i = (3i, 3i+1, 3i+2)
  expectZones: 30,                                    // total LEDs init should declare
  expectInit: ['0001'],                               // reports init must send (hex prefixes, report id first)
  expectFrame: ['0002FF0000'],                        // reports render must send, in order (hex prefixes)
}, { name: 'ignores the wrong collection', device: { reportSize: 8 }, expectDeclined: true }]
```

SMBus plugins use `bus: { '0x19': { '0x43': 26 } }` (register values per address), optionally `busMem: { '0x77': { '0x1000': 'DIMM_LED-0102' } }` (pointer-addressed memory: a word write to register 0x00 sets the byte-swapped pointer, register 0x81 reads it; values are a string or a byte array), `busInfo: 'hex'`
(bytes returned from register 0x40 after a 0x21 write), and `expectBusWrites: ['19:31#0C...']` (byte writes are `aa:rr=vv`, word writes `aa:rr=wvvvv`, blocks `aa:rr#hex`).
Register values above 0xFF are returned whole by `readWord`.
Writing to 0x50–0x57 always fails the test.

Derive replies and expected bytes from the reference implementation (OpenRGB, liquidctl, OpenLinkHub), or from a
capture of the real device. Run with `Glint.exe plugincheck my-plugin.js`, or `plugins.check` from the API.
Bundled plugins must pass their tests.

**Testing a device plugin** on the real hardware:
`Glint.exe plugintest <path\to\plugin.js> <log.txt> <seconds>` opens only the devices that plugin claims, plays a rainbow, and logs every error.

See `logitech-g213.js` (bundled, tested on a real G213) and `device-template.js` for complete examples.
