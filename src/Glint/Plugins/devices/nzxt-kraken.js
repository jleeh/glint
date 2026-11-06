// NZXT Kraken X53/X63/X73 (1e71:2007 and the revised 1e71:2014) and Kraken Z53/Z63/Z73 (1e71:3008): RGB only.
// X3: pump ring (8 LEDs), logo (1 LED) and the external HUE 2 channel (accessories detected). Z3: the external channel.
// Pump, fans and liquid temperature are left to LibreHardwareMonitor, so this plugin never sends the 0x70 status-stream
// setup or any speed command; it only reads the firmware / lighting-info reports to identify the device.
// Protocol per liquidctl kraken3.py (initialize: 0x10 0x01 / 0x20 0x03, parse_led_info, _write_colors "super-fixed") and
// OpenRGB Controllers/NZXTHue2Controller (SetChannelLEDs -> SendDirect 0x22 0x10|group, SendApply 0x22 0xA0). Numbered
// 64-byte reports; colour order GRB; channel = bit mask (external 1, ring 2, logo 4). Direct mode only, nothing persisted.
// The 2023 Kraken / Elite (300c/300e/3012) have no direct mode in the references (effects only) and are not supported.

const ACC = {
  0x01: ['HUE strip', 10], 0x02: ['Aer fan', 8], 0x04: ['HUE 2 strip 10', 10], 0x05: ['HUE 2 strip 8', 8], 0x06: ['HUE 2 strip 6', 6],
  0x08: ['Cable comb', 14], 0x09: ['Underglow 300', 15], 0x0a: ['Underglow 200', 10], 0x0b: ['Aer 2 120', 8], 0x0c: ['Aer 2 140', 8],
  0x13: ['F120 RGB', 18], 0x14: ['F140 RGB', 18], 0x15: ['F120 RGB Duo', 20], 0x16: ['F140 RGB Duo', 20], 0x17: ['F120 RGB Core', 8],
  0x18: ['F140 RGB Core', 8], 0x19: ['F120 RGB Core', 8], 0x1c: ['F280 RGB Core', 16], 0x1d: ['F360 RGB Core', 24], 0x1f: ['F420 RGB', 24],
};
const FANISH = new Set([0x02, 0x0b, 0x0c, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1c, 0x1d, 0x1f]);
const RING = 0x10, LOGO = 0x11, MAX_LEDS = 40;

let channels = [];          // { mask, leds }

const pad = a => { const b = new Uint8Array(64); b.set(a); return b; };

module.exports = {
  type: 'device',
  name: 'NZXT Kraken X3 / Z3 (RGB)',
  vendorId: 0x1e71,
  productIds: [0x2007, 0x2014, 0x3008],
  reportSize: 64,
  id: dev => dev.productId === 0x3008 ? 'krakenz' : 'krakenx',

  init(dev) {
    for (let i = 0; i < 64; i++) if (!dev.read(1)) break;
    dev.write(pad([0x10, 0x01]));                                // firmware info
    dev.write(pad([0x20, 0x03]));                                // lighting info
    let fw = null, list = null;
    for (let i = 0; i < 16 && !(fw && list); i++) {
      const r = dev.read(300);
      if (!r) continue;
      if (r[0] === 0x11 && r[1] === 0x01) fw = r;
      else if (r[0] === 0x21 && r[1] === 0x03) list = r;
    }
    if (!fw || !list) return false;                              // not an answering Kraken
    dev.log(`firmware ${fw[0x11]}.${fw[0x12]}.${fw[0x13]}, ${list[14]} colour channel(s)`);

    const zones = [];
    channels = [];
    const nch = list[14];
    const add = (mask, leds) => { channels.push({ mask, leds }); };
    // channel 0: external accessories (up to 6, <= 40 LEDs)
    if (nch >= 1) {
      let used = 0;
      for (let a = 0; a < 6; a++) {
        const id = list[15 + a];
        if (id === 0) break;
        const acc = ACC[id];
        if (!acc || used + acc[1] > MAX_LEDS) break;
        used += acc[1];
        zones.push({ id: 'ext' + (a + 1), name: 'External: ' + acc[0], leds: acc[1], kind: FANISH.has(id) ? 'ring' : 'strip', category: FANISH.has(id) ? 'fan' : 'strip' });
      }
      if (used > 0) add(1, used);
    }
    if (nch >= 2 && list[15 + 6] === RING) { zones.push({ id: 'ring', name: 'Pump ring', leds: 8, kind: 'ring', category: 'pump' }); add(2, 8); }
    if (nch >= 3 && list[15 + 12] === LOGO) { zones.push({ id: 'logo', name: 'Pump logo', leds: 1, kind: 'single', category: 'pump' }); add(4, 1); }
    if (!zones.length) return false;
    return { zones };
  },

  // rgb: channel order external, ring, logo. Per channel: GRB in groups of 20 LEDs, then the apply packet
  render(dev, rgb) {
    let off = 0;
    for (const { mask, leds } of channels) {
      const grb = new Uint8Array(leds * 3);
      for (let i = 0; i < leds; i++) { grb[i * 3] = rgb[off + i * 3 + 1]; grb[i * 3 + 1] = rgb[off + i * 3]; grb[i * 3 + 2] = rgb[off + i * 3 + 2]; }
      off += leds * 3;
      const g0 = new Uint8Array(64); g0[0] = 0x22; g0[1] = 0x10; g0[2] = mask; g0.set(grb.subarray(0, Math.min(60, grb.length)), 4);
      dev.write(g0);
      if (leds > 20) { const g1 = new Uint8Array(64); g1[0] = 0x22; g1[1] = 0x11; g1[2] = mask; g1.set(grb.subarray(60), 4); dev.write(g1); }
      const ap = new Uint8Array(64);
      ap[0] = 0x22; ap[1] = 0xa0; ap[2] = mask; ap[4] = 0x01; ap[7] = 0x28; ap[10] = 0x80; ap[12] = 0x32; ap[15] = 0x01;
      dev.write(ap);
    }
  },

  // Simulated-device tests (Glint.exe plugincheck)
  tests: (function () {
    const hex = a => a.map(v => (v & 255).toString(16).padStart(2, '0')).join('');
    const fwReply = '1101' + '00'.repeat(0x0f) + '020301';
    const list = (n, chans) => { const b = new Array(64).fill(0); b[0] = 0x21; b[1] = 0x03; b[14] = n; chans.forEach((ids, c) => ids.forEach((v, i) => b[15 + c * 6 + i] = v)); return hex(b); };
    const grb = (from, n) => { const o = []; for (let i = from; i < from + n; i++) o.push(3 * i + 1, 3 * i, 3 * i + 2); return o; };
    const apply = m => '22a0' + hex([m]) + '0001000028' + '0000' + '80' + '00' + '32' + '0000' + '01';
    return [
      {
        name: 'Kraken X3: ring (8) + logo (1), direct GRB + apply on channel masks 2 and 4',
        device: { productId: 0x2007, reportSize: 64, inputSize: 64 },
        replies: [{ when: '1001', reply: fwReply }, { when: '2003', reply: list(3, [[], [0x10], [0x11]]) }],
        expectZones: 9,
        expectInit: ['1001', '2003'],
        expectFrame: ['221002' + '00' + hex(grb(0, 8)), apply(2), '221004' + '00' + hex(grb(8, 1)), apply(4)],
      },
      {
        name: 'Kraken X3 (rev 2): external channel with an Aer 2 fan + ring + logo = 17 LEDs',
        device: { productId: 0x2014, reportSize: 64, inputSize: 64 },
        replies: [{ when: '1001', reply: fwReply }, { when: '2003', reply: list(3, [[0x0b], [0x10], [0x11]]) }],
        expectZones: 17,
        expectFrame: ['221001' + '00' + hex(grb(0, 8)), apply(1), '221002' + '00' + hex(grb(8, 8)), apply(2)],
      },
      {
        name: 'Kraken Z3 (3008): one external channel',
        device: { productId: 0x3008, reportSize: 64, inputSize: 64 },
        replies: [{ when: '1001', reply: fwReply }, { when: '2003', reply: list(1, [[0x04, 0x04]]) }],
        expectZones: 20,
        expectFrame: ['221001' + '00' + hex(grb(0, 20)), apply(1)],
      },
      { name: 'declined when the device does not answer the firmware query', device: { productId: 0x2007, reportSize: 64 }, expectDeclined: true },
    ];
  })(),
};
