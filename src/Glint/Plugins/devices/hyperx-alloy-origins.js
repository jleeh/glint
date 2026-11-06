// HyperX Alloy Origins (full-size, wired; VID 0x0951 PID 0x16E5): per-key direct mode, 107 keys.
// Protocol per OpenRGB Controllers/HyperXKeyboardController/HyperXAlloyOriginsController (SetLEDsDirect,
// SendDirectInitialization, SendDirectColorPacket): every frame is one 65-byte feature report "04 F2 .. 09" followed by nine
// 65-byte feature reports of up to 16 x [0x81, R, G, B] key slots; 19 slots (skip_idx) are unused and sent black. LED order is
// OpenRGB's led_names order (column-wise); the matrix below places each LED by key name on a standard 6 x 23 grid
// (esc/F-row, main block, nav cluster, numpad). Not covered: HP-branded VID 0x03F0 variants, Origins Core / 60 / 65 /
// Elite 2 / FPS RGB (different packet formats and layout variants). Nothing is saved to the keyboard.
const SKIP = new Set([23, 29, 41, 47, 59, 70, 71, 87, 88, 93, 99, 100, 102, 108, 113, 114, 120, 123, 124]);
const ROWS = 6, COLS = 23, LEDS = 107;
const CELL = [                                       // grid cell -> LED index (-1 = no key); rows of 23
  0, -1, 12, 18, 23, 28, 34, 39, 44, 50, 55, 61, 65, 71, -1, 77, 81, 86, -1, -1, -1, -1, -1,
  1, 7, 13, 19, 24, 29, 35, 40, 45, 51, 56, 62, 66, 72, -1, 78, 82, 87, -1, 90, 95, 99, 104,
  2, 8, 14, 20, 25, 30, 36, 41, 46, 52, 57, 63, 67, 73, -1, 79, 83, 88, -1, 91, 96, 100, 105,
  3, 9, 15, 21, 26, 31, 37, 42, 47, 53, 58, 64, 68, 74, -1, -1, -1, -1, -1, 92, 97, 101, -1,
  4, 6, 10, 16, 22, 27, 32, 38, 43, 48, 54, 59, 69, 75, -1, -1, 84, -1, -1, 93, 98, 102, 106,
  5, 11, 17, -1, -1, 33, -1, -1, 49, 60, 70, -1, -1, 76, -1, 80, 85, 89, -1, 94, -1, 103, -1,
];
const SLOT = [];                                     // LED index -> slot in the 126-slot stream
for (let i = 0, s = 0; i < LEDS; i++) { while (SKIP.has(s)) s++; SLOT.push(s++); }
const SLOTS = SLOT[LEDS - 1] + 1;

module.exports = {
  type: 'device',
  id: 'alloyorigins',
  name: 'HyperX Alloy Origins',
  vendorId: 0x0951,
  productIds: [0x16e5],
  interface: 3,
  minGapMs: 30,

  init(dev) {
    if (dev.featureSize < 65) return false;           // the keyboard's 65-byte feature-report interface (MI_03)
    this.led = new Int32Array(SLOTS).fill(-1);       // slot -> cell
    CELL.forEach((l, c) => { if (l >= 0) this.led[SLOT[l]] = c; });
    return { zones: [{ id: 'keys', name: 'Keyboard', leds: ROWS * COLS, kind: 'matrix', cols: COLS, category: 'keyboard' }] };
  },

  render(dev, rgb) {
    const init = new Uint8Array(65);
    init[1] = 0x04; init[2] = 0xf2; init[9] = 0x09;   // direct-mode initialisation, sent before every colour frame
    dev.sendFeature(init);
    let sent = 0;
    for (let pkt = 0; pkt < 9; pkt++) {
      const n = sent < SLOTS ? Math.min(16, SLOTS - sent) : 1;
      const b = new Uint8Array(65);
      for (let k = 0; k < n; k++) {
        const cell = sent < SLOTS ? this.led[sent + k] : -1;
        b[1 + k * 4] = 0x81;
        if (cell >= 0) { b[2 + k * 4] = rgb[cell * 3]; b[3 + k * 4] = rgb[cell * 3 + 1]; b[4 + k * 4] = rgb[cell * 3 + 2]; }
      }
      sent += n;
      dev.sendFeature(b);
    }
  },

  tests: [
    {
      name: 'direct frame: init report, then 9 packets of [81 R G B] slots with the skipped slots black',
      device: { productId: 0x16e5, reportSize: 65, interface: 3 },
      colors: ['#102030'],
      expectZones: 138,
      // OpenRGB: skip slots 23 and 29 stay 81 00 00 00; slots 126..143 are not sent (last packet is one black entry)
      expectFrame: [
        '0004F2' + '00'.repeat(6) + '09',
        '00' + '81102030'.repeat(16),
        '00' + '81102030'.repeat(7) + '81000000' + '81102030'.repeat(5) + '81000000' + '81102030'.repeat(2),
      ],
    },
    { name: 'declines a short collection', device: { productId: 0x16e5, reportSize: 33, interface: 3 }, expectDeclined: true },
  ],
};
