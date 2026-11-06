// Logitech G213 Prodigy keyboard — 5 lighting zones.
// Protocol from OpenRGB (Controllers/LogitechController/LogitechG213Controller): 20-byte HID++ long
// reports on interface 1, usage page 0xFF43. Note: Logitech G HUB will fight over the lighting if it's running.
module.exports = {
  type: 'device',
  id: 'g213',
  name: 'Logitech G213',
  vendorId: 0x046d,
  productIds: [0xc336],
  interface: 1,
  usagePage: 0xff43,
  reportSize: 20,
  minGapMs: 40,

  init(dev) {
    // The keyboard exposes two vendor collections; only the 20-byte (HID++ long, id 0x11) one takes these packets.
    if (dev.reportSize !== 20) return false;
    // Find the ColorLEDEffects (0x8070) feature's index instead of assuming 0x0C (it varies by firmware).
    const q = new Uint8Array(20); q.set([0x11, 0xff, 0x00, 0x0d, 0x80, 0x70]); dev.write(q);
    this.feat = 0;
    for (let i = 0; i < 8 && !this.feat; i++) { const r = dev.read(200); if (!r) break; if (r[0] === 0x11 && r[2] === 0x00 && r[3] === 0x0d) this.feat = r[4]; }
    if (!this.feat) { dev.log('ColorLEDEffects (0x8070) not found'); return false; }
    return { zones: [{ id: 'keys', name: 'Keyboard', leds: 5, kind: 'strip', category: 'keyboard' }] };
  },

  render(dev, rgb) {
    // one packet per zone: 11 FF <feature> 3A <zone 1-5> 01 R G B 02
    for (let z = 0; z < 5; z++) {
      dev.write([0x11, 0xff, this.feat, 0x3a, z + 1, 0x01, rgb[z * 3], rgb[z * 3 + 1], rgb[z * 3 + 2], 0x02]);
      dev.read(30);                                  // consume the ack
    }
  },

  // Simulated-device tests: run with `Glint.exe plugincheck` (also part of the selftest).
  tests: [
    {
      name: 'looks up ColorLEDEffects, then one packet per zone',
      device: { reportSize: 20 },
      replies: [{ when: '11FF000D8070', reply: '11FF000D0C' }, { when: '11FF0C3A', reply: '11FF0C3A' }],
      expectZones: 5,
      expectFrame: ['11FF0C3A0101000102020000', '11FF0C3A0201030405'],
    },
    { name: 'declines the 64-byte collection', device: { reportSize: 64 }, expectDeclined: true },
  ],
};
