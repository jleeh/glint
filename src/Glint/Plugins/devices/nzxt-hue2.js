// NZXT HUE 2 family: HUE 2 (2001), HUE 2 Ambient (2002), Smart Device V2 (2006/200d/200f), RGB & Fan Controller
// (2009/200e/2010 and the 6-channel 2011/2019/2020/201f/2022), B650E / motherboard Hue 2 (201b, 2005, 200b):
// ARGB channels (per-accessory zones from the detected device list) and up to 3 fans (duty + RPM).
// Protocol per OpenRGB Controllers/NZXTHue2Controller (SendFirmwareRequest, UpdateDeviceList, SetChannelLEDs/SendDirect/
// SendApply, SendFan) and liquidctl smart_device.py SmartDevice2 (initialize: 0x60 fan reporting, parse_fan_info 0x67 0x02).
// Numbered reports: byte 0 of every report is the command / report id (0x22 colour, 0x62 fan duty, ...), 64 bytes.
//
// Direct colour mode only (<= 40 LEDs per channel: two groups of 20, as the hardware allows). The fan controller has no
// firmware fan curve: on exit shutdown() leaves the fans at a safe fixed 70 % rather than at whatever Glint last set.

// accessory id -> [name, leds]   (OpenRGB UpdateDeviceList)
const ACC = {
  0x01: ['HUE strip', 10], 0x02: ['Aer fan', 8], 0x04: ['HUE 2 strip 10', 10], 0x05: ['HUE 2 strip 8', 8], 0x06: ['HUE 2 strip 6', 6],
  0x08: ['Cable comb', 14], 0x09: ['Underglow 300', 15], 0x0a: ['Underglow 200', 10], 0x0b: ['Aer 2 120', 8], 0x0c: ['Aer 2 140', 8],
  0x10: ['Kraken ring', 8], 0x11: ['Kraken logo', 1], 0x13: ['F120 RGB', 18], 0x14: ['F140 RGB', 18], 0x15: ['F120 RGB Duo', 20],
  0x16: ['F140 RGB Duo', 20], 0x17: ['F120 RGB Core', 8], 0x18: ['F140 RGB Core', 8], 0x19: ['F120 RGB Core', 8],
  0x1c: ['F280 RGB Core', 16], 0x1d: ['F360 RGB Core', 24], 0x1e: ['Kraken Elite ring', 24], 0x1f: ['F420 RGB', 24],
};
const FANISH = new Set([0x02, 0x0b, 0x0c, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1c, 0x1d, 0x1f]);
// pid -> [rgb channels, fan channels, name]
const MODELS = {
  0x2001: [4, 0, 'HUE 2'], 0x2002: [2, 0, 'HUE 2 Ambient'],
  0x2006: [2, 3, 'Smart Device V2'], 0x200d: [2, 3, 'Smart Device V2'], 0x200f: [2, 3, 'Smart Device V2'],
  0x2009: [2, 3, 'RGB & Fan Controller'], 0x200e: [2, 3, 'RGB & Fan Controller'], 0x2010: [2, 3, 'RGB & Fan Controller'],
  0x2011: [6, 3, 'RGB & Fan Controller'], 0x2019: [6, 3, 'RGB & Fan Controller'], 0x2020: [6, 3, 'RGB & Fan Controller'],
  0x201f: [6, 3, 'RGB & Fan Controller'], 0x2022: [6, 3, 'RGB & Fan Controller 2024'], 0x201b: [6, 3, 'B650E motherboard controller'],
  0x2005: [2, 3, 'Hue 2 motherboard'], 0x200b: [2, 3, 'Hue 2 motherboard'],
};
const SAFE_DUTY = 70, MAX_LEDS = 40;

let spec = [0, 0], fanOn = [], channels = [];

const pad = a => { const b = new Uint8Array(64); b.set(a); return b; };
function drain(dev) { for (let i = 0; i < 64; i++) if (!dev.read(1)) break; }

// read reports until every wanted type (two-byte prefix) has been seen, up to `tries` reads
function collect(dev, want, tries) {
  const got = {};
  for (let i = 0; i < tries && Object.keys(got).length < want.length; i++) {
    const r = dev.read(300);
    if (!r) continue;
    const k = r[0].toString(16).padStart(2, '0') + r[1].toString(16).padStart(2, '0');
    if (want.includes(k)) got[k] = r;
  }
  return got;
}

module.exports = {
  type: 'device',
  name: 'NZXT HUE 2 / Smart Device V2 / RGB & Fan Controller',
  vendorId: 0x1e71,
  productIds: Object.keys(MODELS).map(Number),
  reportSize: 64,
  id: dev => 'nzxt' + dev.productId.toString(16),

  init(dev) {
    const m = MODELS[dev.productId];
    if (!m) return false;
    spec = m;
    drain(dev);
    if (m[1] > 0) {                                              // fan reporting (liquidctl initialize): status every 0.5 s
      dev.write(pad([0x60, 0x02, 0x01, 0xe8, 0x01, 0x01, 0xe8, 0x01]));
      dev.write(pad([0x60, 0x03]));
    }
    dev.write(pad([0x10, 0x01]));                                // firmware info
    dev.write(pad([0x20, 0x03]));                                // lighting (device list) info
    const want = ['1101', '2103']; if (m[1] > 0) want.push('6702');
    const got = collect(dev, want, 24);
    const fw = got['1101'];
    if (!fw) return false;                                       // no firmware answer: not a HUE 2 device
    dev.log(`${m[2]} firmware ${fw[0x11]}.${fw[0x12]}.${fw[0x13]}`);

    // accessories per channel: 6 ids from offset 15 (channel count at 14)
    const zones = [];
    channels = [];
    const list = got['2103'];
    if (list) {
      const nch = Math.min(list[14], m[0]);
      for (let c = 0; c < nch; c++) {
        let used = 0;
        const ch = { c, leds: 0 };
        for (let a = 0; a < 6; a++) {
          const id = list[15 + c * 6 + a];
          if (id === 0) break;
          const acc = ACC[id];
          if (!acc || acc[1] === 0 || used + acc[1] > MAX_LEDS) { if (!acc) dev.log(`unknown accessory 0x${id.toString(16)} on channel ${c + 1}`); else if (used + acc[1] > MAX_LEDS) break; continue; }
          used += acc[1];
          zones.push({ id: `ch${c + 1}a${a + 1}`, name: `Ch ${c + 1}: ${acc[0]}`, leds: acc[1], kind: FANISH.has(id) ? 'ring' : 'strip', category: FANISH.has(id) ? 'fan' : 'strip' });
        }
        ch.leds = used;
        if (used > 0) channels.push(ch);
      }
    }

    fanOn = [];
    const fans = [];
    const st = got['6702'];
    for (let i = 0; i < m[1]; i++) {
      const on = st ? st[16 + i] !== 0 : true;                    // control mode 0 = nothing plugged in
      fanOn.push(on);
      if (on) fans.push({ id: 'fan' + (i + 1), name: 'Fan ' + (i + 1) });
    }
    return { zones, fans };
  },

  // per channel: up to 20 LEDs in group 0x10, the rest in group 0x11 (GRB), then the apply packet
  render(dev, rgb) {
    let off = 0;
    for (const { c, leds } of channels) {
      const grb = new Uint8Array(leds * 3);
      for (let i = 0; i < leds; i++) { grb[i * 3] = rgb[off + i * 3 + 1]; grb[i * 3 + 1] = rgb[off + i * 3]; grb[i * 3 + 2] = rgb[off + i * 3 + 2]; }
      off += leds * 3;
      const mask = 1 << c;
      const g0 = new Uint8Array(64); g0[0] = 0x22; g0[1] = 0x10; g0[2] = mask; g0.set(grb.subarray(0, Math.min(60, grb.length)), 4);
      dev.write(g0);
      if (leds > 20) { const g1 = new Uint8Array(64); g1[0] = 0x22; g1[1] = 0x11; g1[2] = mask; g1.set(grb.subarray(60), 4); dev.write(g1); }
      const ap = new Uint8Array(64);
      ap[0] = 0x22; ap[1] = 0xa0; ap[2] = mask; ap[4] = 0x01; ap[7] = 0x28; ap[10] = 0x80; ap[12] = 0x32; ap[15] = 0x01;
      dev.write(ap);
    }
  },

  // the controller streams a 0x67 0x02 status report; keep the newest one (mode @16, RPM LE @24, duty @40)
  poll(dev) {
    if (spec[1] === 0) return {};
    let last = null;
    for (let i = 0; i < 64; i++) {
      const r = dev.read(last ? 1 : 700);
      if (!r) break;
      if (r[0] === 0x67 && r[1] === 0x02) last = r;
    }
    if (!last) return {};
    const rpm = {};
    for (let i = 0; i < spec[1]; i++) if (fanOn[i]) rpm['fan' + (i + 1)] = (last[25 + i * 2] << 8) | last[24 + i * 2];
    return { rpm };
  },

  setDuty(dev, duty) {
    for (let i = 0; i < spec[1]; i++) {
      const d = duty['fan' + (i + 1)];
      if (d === undefined || !fanOn[i]) continue;
      const msg = new Uint8Array(64);                            // liquidctl _write_fixed_duty: channel as bit flag, duty in byte 3+i
      msg[0] = 0x62; msg[1] = 0x01; msg[2] = 1 << i; msg[3 + i] = Math.max(0, Math.min(100, Math.round(d)));
      dev.write(msg);
    }
  },

  shutdown(dev) {
    try { this.setDuty(dev, { fan1: SAFE_DUTY, fan2: SAFE_DUTY, fan3: SAFE_DUTY }); } catch (e) { }
  },

  // Simulated-device tests (Glint.exe plugincheck): packet layouts from OpenRGB NZXTHue2Controller.cpp and liquidctl SmartDevice2.
  tests: (function () {
    const fwReply = '1101' + '00'.repeat(0x0f) + '010203';               // firmware at 0x11..0x13
    // 0x21 0x03: byte 14 = channel count, then 6 accessory ids per channel
    const list = (ch1, ch2) => { const b = new Array(64).fill(0); b[0] = 0x21; b[1] = 0x03; b[14] = 2; ch1.forEach((v, i) => b[15 + i] = v); ch2.forEach((v, i) => b[21 + i] = v); return b.map(v => v.toString(16).padStart(2, '0')).join(''); };
    const status = (() => { const b = new Array(64).fill(0); b[0] = 0x67; b[1] = 0x02; b[16] = 2; b[17] = 1; b[24] = 0xB0; b[25] = 0x04; return b.map(v => v.toString(16).padStart(2, '0')).join(''); })();
    const hex = a => a.map(v => (v & 255).toString(16).padStart(2, '0')).join('');
    const grb = (from, n) => { const o = []; for (let i = from; i < from + n; i++) o.push(3 * i + 1, 3 * i, 3 * i + 2); return o; };
    const apply = ch => '22a0' + hex([ch]) + '0001000028' + '0000' + '80' + '00' + '32' + '0000' + '01';
    return [
      {
        name: 'Smart Device V2: fan reporting init, 2 F120 fans on ch1 + HUE 2 strip on ch2, direct GRB groups + apply',
        device: { productId: 0x200d, reportSize: 64, inputSize: 64 },
        replies: [{ when: '1001', reply: fwReply }, { when: '2003', reply: list([0x13, 0x13], [0x04]) }, { when: '6003', reply: status }],
        expectZones: 46,
        expectInit: ['600201e80101e801', '6003', '1001', '2003'],
        expectFrame: [
          '221001' + '00' + hex(grb(0, 20)), '221101' + '00' + hex(grb(20, 16)), apply(1),
          '221002' + '00' + hex(grb(36, 10)), apply(2),
        ],
      },
      {
        name: 'HUE 2 (no fans): no fan reporting commands, 4 channels allowed',
        device: { productId: 0x2001, reportSize: 64, inputSize: 64 },
        replies: [{ when: '1001', reply: fwReply }, { when: '2003', reply: list([0x04, 0x04], [0x06]) }],
        expectZones: 26,
        expectFrame: ['221001' + '00' + hex(grb(0, 20)), apply(1), '221002' + '00' + hex(grb(20, 6)), apply(2)],
      },
      { name: 'declined when the firmware report never arrives', device: { productId: 0x200d, reportSize: 64 }, expectDeclined: true },
    ];
  })(),
};
