// Lian Li Uni Hub (HID generation): SL Infinity (0cf2:a102), SL V2 / AL V2 / SL V2 v0.5 (a103 / a104 / a105) and AL (a101,
// firmware v1.7): per-channel fan-ring ARGB in direct (static, full data array) mode. Up to 4 channels (8 on SL Infinity),
// up to 6 fans of 16 LEDs per channel (AL: 4 fans of 8 fan + 12 edge LEDs).
// Protocol per OpenRGB Controllers/LianLiController/LianLiUniHubSLV2Controller, ...SLInfinityController and
// ...ALController (SetChannelLEDs, SendStartAction, SendColorData, SendCommitAction): every packet starts with report id 0xE0,
// colours are sent R,B,G. Brightness limiters as in OpenRGB (sum of channels capped at 460; AL greys capped at 153).
//
// What is NOT here, and why: the hubs' fan-speed control is not implemented in any of the allowed references (OpenRGB has
// RGB only), so there are no fans / RPM. The original SL (a100, fw v1.8, per-fan colour only, OpenRGB's SendColor writes
// sizeof(pointer) bytes) and the AL v1.0 / original Uni Hub (7750, libusb control transfers) are left out.
// Fan counts cannot be read: set LED counts per channel on the Devices page (default: 3 fans on channel 1, others empty).
// Safety: direct/static colour only; nothing is written to flash. Identification: VID/PID + the vendor collection
// (interface 1, usage page 0xff72) and, for AL, the "v1.x" firmware tag in the product string.

const FAMILY = {
  0xa101: { kind: 'al', name: 'Uni Hub AL', channels: 4, per: 20, max: 80, size: 146 },
  0xa102: { kind: 'inf', name: 'Uni Hub SL Infinity', channels: 8, per: 16, max: 96, size: 353 },
  0xa103: { kind: 'v2', name: 'Uni Hub SL V2', channels: 4, per: 16, max: 96, size: 353 },
  0xa104: { kind: 'v2', name: 'Uni Hub AL V2', channels: 4, per: 16, max: 96, size: 353 },
  0xa105: { kind: 'v2', name: 'Uni Hub SL V2 v0.5', channels: 4, per: 16, max: 96, size: 353 },
};
const REPORT = 0xe0;
const STATIC = 0x01, SPEED_SLOWEST = 0x02, DIR_LTR = 0x00, BRIGHT_100 = 0x00;

let fam = null, active = [];     // active: [{ c, leds }]

function send(dev, bytes) {
  const b = new Uint8Array(Math.max(dev.reportSize, 65));
  b.set(bytes);
  dev.write(b);
  dev.sleep(5);                  // OpenRGB waits 5 ms after every packet
}
const start = (dev, ch, n) => {
  if (fam.kind === 'v2') send(dev, [REPORT, 0x10, 0x60, (ch << 4) + n]);
  else if (fam.kind === 'inf') send(dev, [REPORT, 0x10, 0x60, 1 + (ch >> 1), 0x04]);   // two channels per fan array; 4 fans hard-coded upstream
  else send(dev, [REPORT, 0x10, 0x40, ch + 1, n]);                                     // AL
};
const commit = (dev, id) => send(dev, [REPORT, id, STATIC, SPEED_SLOWEST, DIR_LTR, BRIGHT_100]);

// sum-of-channels limiter (SL V2 / Infinity): never drive a LED above 460 total
function limited(r, g, b) {
  const s = r + g + b;
  const k = s > 460 ? 460 / s : 1;
  return [Math.floor(r * k), Math.floor(g * k), Math.floor(b * k)];
}

module.exports = {
  type: 'device',
  name: 'Lian Li Uni Hub (SL Infinity / SL V2 / AL V2 / AL)',
  vendorId: 0x0cf2,
  productIds: [0xa101, 0xa102, 0xa103, 0xa104, 0xa105],
  interface: 1,
  usagePage: 0xff72,
  reportSize: 65,
  id: dev => ({ 0xa101: 'unial', 0xa102: 'uniinf', 0xa103: 'unislv2', 0xa104: 'unialv2', 0xa105: 'unislv2' })[dev.productId] || 'unihub',

  init(dev) {
    fam = FAMILY[dev.productId];
    if (!fam) return false;
    if (dev.reportSize < fam.size) return false;                  // not the vendor collection of this hub
    const ver = /v(\d+\.\d+)/i.exec(dev.product || '');
    if (fam.kind === 'al' && ver && ver[1] !== '1.7') return false; // AL v1.0 firmware needs libusb transfers: unsupported
    dev.log(`${fam.name} ${dev.product || ''}`);

    const zones = [];
    active = [];
    for (let c = 0; c < fam.channels; c++) {
      let n = dev.ledCount('ch' + (c + 1), c === 0 ? 3 * fam.per : 0);
      n = Math.min(fam.max, Math.ceil(n / fam.per) * fam.per);
      if (n <= 0) continue;
      active.push({ c, leds: n });
      for (let f = 0; f < n / fam.per; f++)
        zones.push({ id: `ch${c + 1}f${f + 1}`, name: `Channel ${c + 1} fan ${f + 1}`, leds: fam.per, kind: 'ring', category: 'fan' });
    }
    return { zones };
  },

  render(dev, rgb) {
    let off = 0;
    for (const { c, leds } of active) {
      const fans = leds / fam.per;
      if (fam.kind === 'al') {
        // inner fan ring = first 8 LEDs of each fan, edge = the other 12; separate data / commit per part
        const parts = [new Uint8Array(fans * 8 * 3), new Uint8Array(fans * 12 * 3)];
        for (let i = 0; i < leds; i++) {
          let r = rgb[(off + i) * 3], g = rgb[(off + i) * 3 + 1], b = rgb[(off + i) * 3 + 2];
          if (r > 153 && r === g && g === b) { r = g = b = 153; }       // OpenRGB AL limiter for pure greys
          const f = Math.floor(i / 20), m = i % 20;
          const part = m < 8 ? 0 : 1, idx = (part === 0 ? m + f * 8 : (m - 8) + f * 12) * 3;
          parts[part][idx] = r; parts[part][idx + 1] = b; parts[part][idx + 2] = g;
        }
        for (let part = 0; part < 2; part++) {
          start(dev, c, fans);
          send(dev, [REPORT, 0x30 + part + c * 2, ...parts[part]]);
          commit(dev, 0x10 + part + c * 2);
        }
      } else {
        const data = new Uint8Array(leds * 3);
        for (let i = 0; i < leds; i++) {
          const [r, g, b] = limited(rgb[(off + i) * 3], rgb[(off + i) * 3 + 1], rgb[(off + i) * 3 + 2]);
          data[i * 3] = r; data[i * 3 + 1] = b; data[i * 3 + 2] = g;
        }
        start(dev, c, fans);
        send(dev, [REPORT, 0x30 + c, ...data]);
        commit(dev, 0x10 + c);
      }
      off += leds;
    }
  },

  // Simulated-device tests (Glint.exe plugincheck). Layouts from the OpenRGB controllers named at the top; colours are R,B,G.
  tests: (function () {
    const hex = a => Array.from(a).map(v => (v & 255).toString(16).padStart(2, '0')).join('');
    const rbg = (from, n) => { const o = []; for (let i = from; i < from + n; i++) o.push(3 * i, 3 * i + 2, 3 * i + 1); return o; };
    const commitHex = id => 'e0' + hex([id, 1, 2, 0, 0]);
    // AL ramp, 3 fans (60 LEDs): fan LEDs = first 8 of each 20, edge = remaining 12
    const alParts = (() => {
      const fan = [], edge = [];
      for (let f = 0; f < 3; f++) {
        fan.push(...rbg(f * 20, 8));
        edge.push(...rbg(f * 20 + 8, 12));
      }
      return [fan, edge];
    })();
    return [
      {
        name: 'SL V2: channel 1 default 3 fans = 48 LEDs: start (3 fans), R,B,G colour data, static commit',
        device: { productId: 0xa103, reportSize: 353, inputSize: 353, interface: 1 },
        expectZones: 48,
        expectFrame: ['e0106003', 'e030' + hex(rbg(0, 48)), commitHex(0x10)],
      },
      {
        name: 'AL V2 (a104) uses the same SL V2 packets; white is limited to 153 per channel (460 total)',
        device: { productId: 0xa104, reportSize: 353, inputSize: 353, interface: 1 },
        colors: ['#ffffff'],
        expectZones: 48,
        expectFrame: ['e0106003', 'e030' + '999999'.repeat(16), commitHex(0x10)],
      },
      {
        name: 'SL Infinity: start packet carries the fan-array number (1 + channel/2) and 4 fans',
        device: { productId: 0xa102, reportSize: 353, inputSize: 353, interface: 1 },
        expectZones: 48,
        expectFrame: ['e010600104', 'e030' + hex(rbg(0, 48)), commitHex(0x10)],
      },
      {
        name: 'AL (v1.7): fan ring (8/fan) and edge (12/fan) sent as separate data + commit pairs',
        device: { productId: 0xa101, reportSize: 146, inputSize: 146, interface: 1 },
        expectZones: 60,
        expectFrame: ['e010400103', 'e030' + hex(alParts[0]), commitHex(0x10), 'e010400103', 'e031' + hex(alParts[1]), commitHex(0x11)],
      },
      { name: 'declined when the report size is too small for the vendor collection', device: { productId: 0xa103, reportSize: 65, inputSize: 65 }, expectDeclined: true },
    ];
  })(),
};
