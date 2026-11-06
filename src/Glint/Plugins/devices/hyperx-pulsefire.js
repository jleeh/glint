// HyperX Pulsefire Surge (32-LED ring + logo), Pulsefire FPS Pro / Core (one LED), Pulsefire Haste (one LED), wired, VID 0x0951.
// Protocol per OpenRGB Controllers/HyperXMouseController: HyperXPulsefireSurgeController::SendDirect (264-byte report 07 14,
// R/G/B planes of 32 at 0x08/0x28/0x48 plus logo RGB at 0x6C), HyperXPulsefireFPSProController::SendDirect (264-byte report
// 07 0A, RGB at 2..4, 0xA0 at 8), HyperXPulsefireHasteController::SendDirectSetup/SendDirectColor (65-byte reports 04 F2 / 81).
// Direct mode only; profile/save packets are never sent. Not covered: HP-branded VID 0x03F0 variants, Pulsefire Dart/Raid.
const MODEL = {                                      // pid -> [kind, interface, min feature size, zones]
  0x16d3: ['surge', 1, 264, [['ring', 'Ring', 32, 'strip'], ['logo', 'Logo', 1, 'single']]],
  0x16d7: ['fps', 1, 264, [['mouse', 'Mouse', 1, 'single']]],
  0x16de: ['fps', 1, 264, [['mouse', 'Mouse', 1, 'single']]],            // Pulsefire Core
  0x1727: ['haste', 3, 65, [['mouse', 'Mouse', 1, 'single']]],
};

module.exports = {
  type: 'device',
  id: dev => 'pulsefire' + dev.productId.toString(16),
  name: 'HyperX Pulsefire',
  vendorId: 0x0951,
  productIds: Object.keys(MODEL).map(Number),
  minGapMs: 30,

  init(dev) {
    const m = MODEL[dev.productId];
    if (!m || dev.interfaceNumber !== m[1] || dev.featureSize < m[2]) return false;
    this.m = m;
    if (m[0] === 'haste') { const s = new Uint8Array(65); s[1] = 0x04; s[2] = 0xf2; s[8] = 0x02; dev.sendFeature(s); }   // direct-mode setup
    return { zones: m[3].map(z => ({ id: z[0], name: z[1], leds: z[2], kind: z[3], category: 'mouse' })) };
  },

  render(dev, rgb) {
    const kind = this.m[0];
    if (kind === 'surge') {
      const b = new Uint8Array(264);
      b[0] = 0x07; b[1] = 0x14; b[3] = 0xa0;
      for (let i = 0; i < 32; i++) { b[0x08 + i] = rgb[i * 3]; b[0x28 + i] = rgb[i * 3 + 1]; b[0x48 + i] = rgb[i * 3 + 2]; }
      b[0x6c] = rgb[96]; b[0x6d] = rgb[97]; b[0x6e] = rgb[98];
      dev.sendFeature(b);
    } else if (kind === 'fps') {
      const b = new Uint8Array(264);
      b[0] = 0x07; b[1] = 0x0a; b[2] = rgb[0]; b[3] = rgb[1]; b[4] = rgb[2]; b[8] = 0xa0;
      dev.sendFeature(b);
    } else {
      const b = new Uint8Array(65);
      b[1] = 0x81; b[2] = rgb[0]; b[3] = rgb[1]; b[4] = rgb[2]; b[8] = 0x02;
      dev.sendFeature(b);
    }
  },

  tests: [
    {
      name: 'Surge: 07 14 direct report, R/G/B planes of 32 + logo at 0x6C',
      device: { productId: 0x16d3, reportSize: 264, interface: 1 },
      colors: ['#102030'],
      expectZones: 33,
      expectFrame: ['071400A0' + '00000000' + '10'.repeat(32) + '20'.repeat(32) + '30'.repeat(32) + '00000000' + '102030'],
    },
    {
      name: 'FPS Pro: 07 0A report with RGB at 2..4 and 0xA0 at 8',
      device: { productId: 0x16d7, reportSize: 264, interface: 1 },
      colors: ['#102030'],
      expectZones: 1,
      expectFrame: ['070A102030' + '000000' + 'A0'],
    },
    {
      name: 'Haste: direct setup (04 F2 .. 02) at init, then 81 colour report',
      device: { productId: 0x1727, reportSize: 65, interface: 3 },
      colors: ['#102030'],
      expectZones: 1,
      expectInit: ['0004F2' + '00'.repeat(5) + '02'],
      expectFrame: ['0081102030' + '000000' + '02'],
    },
    { name: 'declines the wrong interface', device: { productId: 0x16d3, reportSize: 264, interface: 0 }, expectDeclined: true },
  ],
};
