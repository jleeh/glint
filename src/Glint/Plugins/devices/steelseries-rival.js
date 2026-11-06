// SteelSeries Rival 3 (0x184C, old firmware 0x1824), Rival 310 / Sensei 310 / Sensei TEN, and Rival 600.
// Protocol per OpenRGB Controllers/SteelSeriesController: SteelSeriesRival3Controller::SetColor (report 05, zones 1-4),
// SteelSeriesSenseiController::SetColor (0x5B static-colour packet; Rival 310 uses the Sensei driver),
// SteelSeriesRivalController::SetRival600Color (report 0x1C 0x27, zone bitmask) and the zone/LED tables in the matching
// RGBController_*.cpp. Direct colour only; the "save to mouse" commands (0x09 / 0x59) are never sent.
// Not covered: Rival 650 (needs a 4-packet sequence per LED), Rival 100/300/700, Rival 5, Aerox, wireless versions.
const RIVAL3 = [[0x01, 'Front'], [0x02, 'Middle'], [0x03, 'Rear'], [0x04, 'Logo']];
const RIVAL600 = [[0x01, 'Logo'], [0x00, 'Scroll wheel'], [0x02, 'Left top'], [0x04, 'Left middle'], [0x06, 'Left bottom'],
  [0x03, 'Right top'], [0x05, 'Right middle'], [0x07, 'Right bottom']];
const SENSEI = [[0, 'Logo'], [1, 'Scroll wheel']];
const MODEL = {                                       // pid -> [kind, interface, zones]
  0x184c: ['r3', 3, RIVAL3], 0x1824: ['r3', 3, RIVAL3],
  0x1720: ['sensei', 0, SENSEI], 0x171e: ['sensei', 0, SENSEI], 0x1736: ['sensei', 0, SENSEI],   // Rival 310 (+ Howl, PUBG)
  0x1722: ['sensei', 0, SENSEI], 0x1832: ['sensei', 0, SENSEI], 0x1834: ['sensei', 0, SENSEI],    // Sensei 310, Sensei TEN (+ CS:GO)
  0x1724: ['r600', 0, RIVAL600], 0x172e: ['r600', 0, RIVAL600],
};

module.exports = {
  type: 'device',
  id: dev => 'rival' + dev.productId.toString(16),
  name: 'SteelSeries Rival',
  vendorId: 0x1038,
  productIds: Object.keys(MODEL).map(Number),
  minGapMs: 40,

  init(dev) {
    const m = MODEL[dev.productId];
    if (!m || dev.interfaceNumber !== m[1]) return false;
    if (dev.reportSize < 8) return false;
    this.m = m; this.last = null;
    const zones = m[2].map((z, i) => ({ id: 'z' + i, name: z[1], leds: 1, kind: 'single', category: 'mouse' }));
    return { zones };
  },

  render(dev, rgb) {
    const [kind, , zones] = this.m;
    if (!this.last) this.last = new Array(zones.length).fill(-1);
    for (let i = 0; i < zones.length; i++) {
      const r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
      const key = (r << 16) | (g << 8) | b;
      if (this.last[i] === key) continue;                // only write zones whose colour changed
      this.last[i] = key;
      const id = zones[i][0];
      if (kind === 'r3') dev.write([0x00, 0x05, 0x00, id, r, g, b, 0x64]);   // zone, R, G, B, brightness 100
      else if (kind === 'r600') dev.write([0x1c, 0x27, 0x00, 1 << id, r, g, b]);   // report id 0x1C, zone bitmask
      else {                                             // Sensei / Rival 310: 0x5B packet, static colour flag
        const p = new Uint8Array(Math.max(dev.reportSize, 66));
        p[1] = 0x5b; p[3] = id;
        p[0x14] = 0x01; p[0x1c] = 0x01;                  // OpenRGB offsets 0x13 / 0x1B + the leading report id byte
        p[0x1d] = r; p[0x1e] = g; p[0x1f] = b; p[0x20] = r; p[0x21] = g; p[0x22] = b;
        dev.write(p.subarray(0, dev.reportSize));
      }
    }
  },

  tests: [
    {
      name: 'Rival 3: four zones, 00 05 00 <zone> R G B 64',
      device: { productId: 0x184c, reportSize: 65, interface: 3 },
      colors: ['#102030', '#000000', '#ff0000', '#00ff00'],
      expectZones: 4,
      expectFrame: ['00050001102030' + '64', '00050002000000' + '64', '00050003ff0000' + '64', '0005000400ff00' + '64'],
    },
    {
      name: 'Rival 310: Sensei 0x5B static colour packet per zone',
      device: { productId: 0x1720, reportSize: 65, interface: 0 },
      colors: ['#102030', '#ff8000'],
      expectZones: 2,
      // wire = report id 00, 5B, 00, zone, ... flag 01 at +0x14, count 01 at +0x1C, colour at +0x1D..0x22 (OpenRGB offsets + 1)
      expectFrame: [
        '005B0000' + '00'.repeat(16) + '01' + '00'.repeat(7) + '01' + '102030' + '102030',
        '005B0001' + '00'.repeat(16) + '01' + '00'.repeat(7) + '01' + 'ff8000' + 'ff8000',
      ],
    },
    {
      name: 'Rival 600: eight LEDs via report 1C 27, bitmask 1<<id',
      device: { productId: 0x1724, reportSize: 65, interface: 0 },
      colors: ['#102030', '#000000', '#ff0000'],
      expectZones: 8,
      expectFrame: ['1C270002102030', '1C270001000000', '1C270004ff0000'],
    },
    { name: 'declines the wrong interface', device: { productId: 0x184c, reportSize: 65, interface: 0 }, expectDeclined: true },
  ],
};
