// Logitech HID++ 2.0 wired mice with zone lighting: feature 0x8071 (RGBEffects) or 0x8070 (ColorLEDEffects).
// G102/G203 Lightsync, G203, G303, G403 (Prodigy/Hero), G502 (Proteus/Hero/X Plus), G Pro (+Hero, wireless when
// wired), G703, G903. NOT the G402 (own plugin: only a breathing logo LED) or G213 (own plugin).
// Protocol per OpenRGB Controllers/LogitechController/LogitechHIDPP20Controller/LogitechHIDPP20Controller.cpp
// (DiscoverRGBEffects, SetSWControl, ClaimSWControlIfNeeded, SetZoneEffect) with feature indices looked up via IRoot.
// Every zone is driven with the firmware's Static effect (effect id 0x0001); nothing is persisted (persist byte 0).
// These mice lock up if spammed, so Glint sends at most ~8 frames/s and only writes zones whose colour changed.
const SW = 0x07;                                     // HID++ software id (low nibble of the function byte)
const FEAT_8071 = 0x8071, FEAT_8070 = 0x8070;
const LOCATIONS = { 1: 'Primary', 2: 'Logo', 3: 'Left side', 4: 'Right side', 5: 'Combined', 6: 'Primary 1', 7: 'Primary 2', 8: 'Primary 3', 9: 'Primary 4', 10: 'Primary 5', 11: 'Primary 6' };

// HID++ long request: 11 FF <feature index> <fn|sw> params...; returns the matching reply or null (also on HID++ error).
function request(dev, idx, fn, params) {
  const b = new Uint8Array(20);
  b.set([0x11, 0xff, idx, fn | SW]);
  if (params) b.set(params, 4);
  dev.write(b);
  for (let i = 0; i < 4; i++) {
    const r = dev.read(150);
    if (!r) return null;
    if (r[0] === 0x11 && r[2] === 0xff && r[3] === idx) return null;           // HID++ 2.0 error report
    if (r[0] === 0x11 && r[2] === idx && r[3] === (fn | SW)) return r;
  }
  return null;
}

module.exports = {
  type: 'device',
  id: 'logihidpp-mouse',
  name: 'Logitech mouse (HID++ zones)',
  vendorId: 0x046d,
  productIds: [
    0xc092,                                          // G102 / G203 Lightsync
    0xc084, 0xc080, 0xc083, 0xc08f,                  // G203 Prodigy, G303, G403 Prodigy, G403 Hero
    0xc332, 0xc08b, 0xc08d, 0xc095,                  // G502 Proteus Spectrum, Hero, Lightspeed (wired), X Plus (wired)
    0xc085, 0xc08c, 0xc088,                          // G Pro, G Pro Hero, G Pro Wireless (wired)
    0xc082, 0xc087, 0xc090, 0xc086, 0xc091,          // G403/G703/G703 Hero/G903/G903 Hero Lightspeed (wired)
  ],
  usagePage: 0xff00,
  reportSize: 20,
  // Old Logitech controllers freeze if commands arrive too fast (see the G402 plugin): at most ~8 frames/s.
  minGapMs: 120,

  init(dev) {
    if (dev.reportSize !== 20) return false;                                    // only the HID++ long collection
    let page = FEAT_8071, root = request(dev, 0x00, 0x00, [0x80, 0x71]);
    if (!root || !root[4]) { page = FEAT_8070; root = request(dev, 0x00, 0x00, [0x80, 0x70]); }
    if (!root || !root[4]) { dev.log('no 0x8071 / 0x8070 lighting feature'); return false; }
    const f = root[4], is71 = page === FEAT_8071;
    this.f = f; this.is71 = is71;
    this.fnSet = is71 ? 0x10 : 0x30;
    // GetInfo -> zone ("cluster") count: 0x8071 sends [FF FF 00] and reads data[2]; 0x8070 sends nothing and reads data[0]
    const info = request(dev, f, 0x00, is71 ? [0xff, 0xff, 0x00] : null);
    const count = info ? (is71 ? info[6] : info[4]) : 0;
    if (!count || count > 8) { dev.log('zone count ' + count + ' not plausible'); return false; }
    this.zones = [];
    for (let i = 0; i < count; i++) {
      const ci = is71 ? request(dev, f, 0x00, [i, 0xff]) : request(dev, f, 0x10, [i, 0x00]);
      if (!ci) continue;
      const loc = is71 ? (ci[6] << 8) | ci[7] : (ci[5] << 8) | ci[6];
      const n = is71 ? ci[8] : ci[7];
      let staticIdx = -1;
      for (let j = 0; j < n && staticIdx < 0; j++) {
        const ei = request(dev, f, is71 ? 0x00 : 0x20, [i, j, 0x00, 0x00]);
        if (ei && ((ei[6] << 8) | ei[7]) === 0x0001) staticIdx = j;              // effect id 1 = Static
      }
      if (staticIdx < 0) { dev.log(`zone ${i} has no Static effect`); continue; }
      this.zones.push({ cluster: i, eff: staticIdx, name: LOCATIONS[loc] || ('Zone 0x' + loc.toString(16)) });
    }
    if (!this.zones.length) return false;
    // take software control: 0x8070 [enable, persist]; 0x8071 [1, mode 3, flags 6] (effect engine keeps running until our first paint)
    const claim = is71 ? request(dev, f, 0x50, [0x01, 0x03, 0x06]) : request(dev, f, 0x80, [0x01, 0x01]);
    if (!claim) { dev.log('software control claim refused'); return false; }
    this.upgraded = !is71;
    this.last = null;
    dev.log(`${is71 ? '0x8071' : '0x8070'} at index 0x${f.toString(16)}: ${this.zones.map(z => z.name).join(', ')}`);
    return { zones: this.zones.map((z, i) => ({ id: 'z' + z.cluster, name: z.name, leds: 1, kind: 'single', category: 'mouse' })) };
  },

  render(dev, rgb) {
    if (!this.last) this.last = new Array(this.zones.length).fill(null);
    for (let i = 0; i < this.zones.length; i++) {
      const r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
      const key = (r << 16) | (g << 8) | b;
      if (this.last[i] === key) continue;                                       // only write when the colour changed
      const z = this.zones[i];
      // SetEffectByIndex: [cluster, effect idx, R, G, B, 0x02 "fixed colour" marker (not for black), ... persist byte [12] = 0]
      const p = new Uint8Array(16);
      p[0] = z.cluster; p[1] = z.eff; p[2] = r; p[3] = g; p[4] = b;
      if (key) p[5] = 0x02;
      request(dev, this.f, this.fnSet, p);
      this.last[i] = key;
    }
    if (!this.upgraded) { request(dev, this.f, 0x50, [0x01, 0x03, 0x05]); this.upgraded = true; }
  },

  // hand the LEDs back to the firmware (0x8071 [1,0,0]; 0x8070 [0,0])
  shutdown(dev) {
    try { if (this.is71) request(dev, this.f, 0x50, [0x01, 0x00, 0x00]); else request(dev, this.f, 0x80, [0x00, 0x00]); } catch (e) { }
  },

  tests: [
    {
      // 0x8071 mouse (G502 Hero style): two zones, each with Off + Static. Bytes per OpenRGB DiscoverRGBEffects / SetZoneEffect.
      name: '0x8071: lookup, discovery, claim 3/6, static zones, upgrade 3/5',
      device: { productId: 0xc08b, reportSize: 20 },
      replies: [
        { when: '11FF00078071', reply: '11FF0007050000' },
        { when: '11FF0507FFFF00', reply: '11FF05070000020000000000' },
        { when: '11FF050700FF', reply: '11FF050700000001' + '02' },
        { when: '11FF050701FF', reply: '11FF050701000002' + '02' },
        { when: '11FF05070000000000', reply: '11FF050700000000' },
        { when: '11FF05070001000000', reply: '11FF050700010001' },
        { when: '11FF05070100000000', reply: '11FF050701000000' },
        { when: '11FF05070101000000', reply: '11FF050701010001' },
        { when: '11FF0557', reply: '11FF0557' },
        { when: '11FF0517', reply: '11FF0517' },
      ],
      colors: ['#102030', '#000000'],
      expectZones: 2,
      expectInit: ['11FF000780' + '71', '11FF0507FFFF00', '11FF050700FF', '11FF050701FF', '11FF0557010306'],
      expectFrame: ['11FF05170001102030' + '02', '11FF0517' + '01' + '01' + '000000' + '00', '11FF0557010305'],
    },
    {
      // 0x8070 mouse (G303 style): no 0x8071; one zone; GetInfo has no params; sw control [1,1]; SetEffect is fn 3.
      name: '0x8070: lookup fallback, claim 1/1, static zone via fn 3',
      device: { productId: 0xc080, reportSize: 20 },
      replies: [
        { when: '11FF00078071', reply: '11FF0007' },
        { when: '11FF00078070', reply: '11FF000709' },
        { when: '11FF0907', reply: '11FF0907' + '01' },
        { when: '11FF091700', reply: '11FF091700' + '0000' + '01' + '00' + '01' },
        { when: '11FF092700000000', reply: '11FF092700000001' },
        { when: '11FF0987', reply: '11FF0987' },
        { when: '11FF0937', reply: '11FF0937' },
      ],
      colors: ['#ff8000'],
      expectZones: 1,
      expectInit: ['11FF0907', '11FF091700', '11FF092700000000', '11FF09870101'],
      expectFrame: ['11FF093700' + '00' + 'FF8000' + '02'],
    },
    { name: 'declines the 64-byte collection', device: { productId: 0xc08b, reportSize: 64 }, expectDeclined: true },
    { name: 'declines a mouse with no lighting feature', device: { productId: 0xc08b, reportSize: 20 }, replies: [{ when: '11FF0007', reply: '11FF0007' }], expectDeclined: true },
  ],
};
