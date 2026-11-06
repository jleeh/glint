// Logitech G402 Hyperion Fury: single blue logo LED via HID++ 2.0 feature 0x1300 (LED software control).
// Protocol per libratbag (src/hidpp20.c). Measured on a real G402 (046d:c07e): the logo LED only supports
// BREATHING (caps 0x80; the DPI LED does on/off but belongs to the mouse). So Glint sets the breathing peak
// brightness from its lighting zone (0 = off), sending only when that changes.
const FEATURE = 0x1300, SW = 0x0d;              // software id in the low nibble of the function byte
const MODE_BREATHING = 0x80;
const PERIOD_MS = 4000;
const hex = a => Array.from(a.subarray(0, 10)).map(x => x.toString(16).padStart(2, '0')).join(' ');

// HID++ long request: 11 FF <feature index> <function|sw> params...; returns the matching reply (or null).
function request(dev, featIdx, fn, params) {
  const b = new Uint8Array(20);
  b.set([0x11, 0xff, featIdx, (fn << 4) | SW]);
  if (params) b.set(params, 4);
  dev.write(b);
  for (let i = 0; i < 3; i++) {
    const r = dev.read(150);
    if (!r) return null;
    if (r[0] === 0x11 && r[2] === 0xff && r[3] === featIdx) throw new Error('HID++ error 0x' + r[5].toString(16));
    if (r[0] === 0x11 && r[2] === featIdx && r[3] === ((fn << 4) | SW)) return r;
  }
  return null;
}

module.exports = {
  type: 'device',
  id: 'g402',
  name: 'Logitech G402',
  vendorId: 0x046d,
  productIds: [0xc07e],
  interface: 1,
  reportSize: 20,
  // This 2014 mouse's controller locks up if it's sent commands too fast (measured: frame-rate updates froze
  // the mouse until it was replugged). Glint never calls render more than twice a second for it.
  minGapMs: 500,

  init(dev) {
    if (dev.reportSize !== 20) return false;                      // only the HID++ long collection
    const root = request(dev, 0x00, 0, [FEATURE >> 8, FEATURE & 0xff]);
    if (!root || !root[4]) { dev.log('no LED control feature (0x1300)'); return false; }
    this.feat = root[4];
    const count = request(dev, this.feat, 0)?.[4] ?? 0;
    this.leds = [];
    for (let i = 0; i < count; i++) {
      const info = request(dev, this.feat, 1, [i]);
      // info: index, type (1 battery, 2 DPI, 3 profile, 4 logo, 5 cosmetic), physical count, caps (u16 BE: supported modes)
      if (info) this.leds.push({ index: info[4], type: info[5], caps: (info[7] << 8) | info[8] });
    }
    dev.log(`LED control at index 0x${this.feat.toString(16)}: ${this.leds.map(l => `#${l.index} type ${l.type} caps 0x${l.caps.toString(16)}`).join(', ')}`);
    // drive only the logo / cosmetic LEDs (the DPI indicator belongs to the mouse), via breathing (all it supports)
    this.leds = this.leds.filter(l => (l.type === 4 || l.type === 5) && (l.caps & MODE_BREATHING));
    if (!this.leds.length) { dev.log('no logo LED that supports breathing'); return false; }
    request(dev, this.feat, 3, [1]);                              // take software control
    this.state = -1;
    return { zones: [{ id: 'logo', name: 'Logo', leds: 1, kind: 'single', category: 'mouse' }] };
  },

  render(dev, rgb) {
    // peak brightness from the zone colour's brightness, quantised so tiny changes don't spam the mouse
    const lum = 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2];
    const level = lum < 12 ? 0 : Math.min(255, Math.round(lum / 64) * 64 + 63);   // 4 levels: few, meaningful changes
    if (level === this.state) return;
    // state: index, mode (u16 BE), brightness (u16 BE), period ms (u16 BE), timeout (u16 BE, 0 = forever)
    for (const l of this.leds)
      request(dev, this.feat, 5, [l.index, 0x00, MODE_BREATHING, 0x00, level, PERIOD_MS >> 8, PERIOD_MS & 0xff, 0x00, 0x00]);
    this.state = level;
  },

  // give the LED back to the mouse's own firmware behaviour
  shutdown(dev) { try { request(dev, this.feat, 3, [0]); } catch (e) { } },

  // Simulated-device tests: run with `Glint.exe plugincheck` (also part of the selftest).
  tests: [
    {
      name: 'finds LED control, drives only the breathing logo LED',
      device: { reportSize: 20, interface: 1 },
      replies: [
        { when: '11FF000D1300', reply: '11FF000D05' },
        { when: '11FF050D', reply: '11FF050D02' },
        { when: '11FF051D00', reply: '11FF051D000201000300' },
        { when: '11FF051D01', reply: '11FF051D010401008000' },
        { when: '11FF053D', reply: '11FF053D' },
        { when: '11FF055D', reply: '11FF055D' },
      ],
      colors: ['#ffffff'],
      expectZones: 1,
      expectInit: ['11FF053D01'],
      expectFrame: ['11FF055D010080'],
    },
  ],
};
