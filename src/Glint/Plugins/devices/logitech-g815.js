// Logitech G813 / G815 / G915 (wired) / G915 TKL (wired): per-key RGB keyboards, HID++ 2.0 feature 0x8081.
// Protocol per OpenRGB Controllers/LogitechController/LogitechG815Controller (LogitechG815Controller.cpp:
// InitializeDirect, SendSingleLed, SendDirectFrame, SendCommit; RGBController_LogitechG815.cpp: matrix_map,
// led_names, DeviceUpdateLEDs). OpenRGB hard-codes the feature indices (0x08, 0x0F, 0x10); here they are looked
// up through IRoot (0x4522 DisableKeysByUsage, 0x8071 RGBEffects, 0x8081 PerKeyLighting v2) so firmware differences
// don't matter. One deliberate difference: the per-cluster "effect off" writes use persist = 0 (OpenRGB sends 1),
// so nothing is ever saved to the keyboard. Wireless (receiver) G915 is not supported. Stop Logitech G HUB first.
const SW = 0x0f;
const MAT = [                                         // OpenRGB matrix_map (7 x 27), -1 = no LED
  [110, -1, -1, -1, -1, -1, -1, -1, -1, -1, 111, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1],
  [-1, -1, 37, -1, 54, 55, 56, 57, -1, 58, 59, 60, 61, -1, 62, 63, 64, 65, -1, 66, 67, 68, -1, 106, 107, 108, 109],
  [112, -1, 49, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, -1, 41, 42, 38, -1, -1, 69, 70, 71, -1, 79, 80, 81, 82],
  [113, -1, 39, -1, 16, 22, 4, 17, -1, 19, 24, 20, 8, 14, 15, 43, 44, 45, -1, 72, 73, 74, -1, 91, 92, 93, 83],
  [114, -1, 53, -1, 0, 18, 3, 5, -1, 6, 7, 9, 10, 11, 47, 48, 46, 36, -1, -1, -1, -1, -1, 88, 89, 90, -1],
  [115, -1, 99, 96, 25, 23, 2, 21, -1, 1, -1, 13, 12, 50, 51, 52, 103, -1, -1, -1, 78, -1, -1, 85, 86, 87, 84],
  [116, -1, 98, 101, 100, -1, -1, -1, -1, 40, -1, -1, -1, -1, 104, 105, 97, 102, -1, 76, 77, 75, -1, 94, -1, 95, -1],
];
// LED index -> wire key id (OpenRGB led_names order; DeviceUpdateLEDs applies usage-3 / usage-0x78 / +0xd1 / +0xb3)
const KEY = (() => {
  const k = [];
  for (let u = 0x04; u <= 0x65; u++) k.push(u - 3);                // 0..97: HID usages 0x04..0x65 (A..Z,1..0,Enter..Menu)
  for (let u = 0xe0; u <= 0xe7; u++) k.push(u - 0x78);             // 98..105 modifiers
  k.push(0x9e, 0x9b, 0x9d, 0x9c);                                  // 106..109 media prev / play / next / mute
  k.push(0x01 + 0xd1, 0x99);                                       // 110 logo, 111 lighting indicator
  for (let g = 1; g <= 5; g++) k.push(g + 0xb3);                   // 112..116 G1..G5
  return k;
})();
const COLS = 27, ROWS = 7;

function request(dev, idx, fn, params) {
  const b = new Uint8Array(20);
  b.set([0x11, 0xff, idx, fn | SW]);
  if (params) b.set(params, 4);
  dev.write(b);
  for (let i = 0; i < 3; i++) {
    const r = dev.read(120);
    if (!r) return null;
    if (r[0] === 0x11 && r[2] === 0xff && r[3] === idx) return null;           // HID++ error
    if (r[0] === 0x11 && r[2] === idx && r[3] === (fn | SW)) return r;
  }
  return null;
}
function lookup(dev, feat) {
  const r = request(dev, 0x00, 0x00, [feat >> 8, feat & 0xff]);
  return r && r[4] ? r[4] : 0;
}

module.exports = {
  type: 'device',
  id: 'g815',
  name: 'Logitech G815/G915 keyboard',
  vendorId: 0x046d,
  productIds: [0xc232, 0xc33f, 0xc33e, 0xc343],        // G813, G815, G915 (wired), G915 TKL (wired)
  reportSize: 20,
  minGapMs: 25,

  init(dev) {
    if (dev.reportSize !== 20) return false;
    const perKey = lookup(dev, 0x8081);
    if (!perKey) { dev.log('no per-key lighting (0x8081)'); return false; }
    const fx = lookup(dev, 0x8071), dk = lookup(dev, 0x4522);
    this.perKey = perKey;
    // OpenRGB InitializeDirect: DisableKeysByUsage handshake (fn3, fn1), turn the firmware effect of each cluster off
    if (dk) { request(dev, dk, 0x30); request(dev, dk, 0x10); }
    if (fx) {
      const info = request(dev, fx, 0x00, [0xff, 0xff, 0x00]);
      const clusters = Math.min(info ? info[6] : 0, 4);
      for (let c = 0; c < clusters; c++) request(dev, fx, 0x10, [c, 0x00]);    // effect 0 = off, persist byte [12] stays 0
    }
    this.frame(dev, [[0x29, 0, 0, 0]]);                // primer key, then commit: engages direct mode
    this.last = null;
    return { zones: [{ id: 'keys', name: 'Keyboard', leds: ROWS * COLS, kind: 'matrix', cols: COLS, category: 'keyboard' }] };
  },

  // send [[keyId, r, g, b]...]: little (fn1, 4 keys/packet) frames, then a commit (fn7)
  frame(dev, keys) {
    for (let i = 0; i < keys.length; i += 4) {
      const p = new Uint8Array(16);
      const n = Math.min(4, keys.length - i);
      for (let j = 0; j < n; j++) p.set(keys[i + j], j * 4);
      if (n < 4) p[n * 4] = 0xff;                      // end-of-data marker
      request(dev, this.perKey, 0x10, p);
    }
    request(dev, this.perKey, 0x70);
  },

  render(dev, rgb) {
    const groups = new Map();                          // colour -> key ids that changed to it
    if (!this.last) this.last = new Int32Array(MAT.length * COLS).fill(-1);
    for (let r = 0; r < ROWS; r++) for (let c = 0; c < COLS; c++) {
      const led = MAT[r][c];
      if (led < 0) continue;
      const cell = r * COLS + c, o = cell * 3;
      const col = (rgb[o] << 16) | (rgb[o + 1] << 8) | rgb[o + 2];
      if (this.last[cell] === col) continue;
      this.last[cell] = col;
      if (!groups.has(col)) groups.set(col, []);
      groups.get(col).push(KEY[led]);
    }
    if (!groups.size) return;
    const little = [];
    for (const [col, ids] of groups) {
      const r = col >> 16, g = (col >> 8) & 255, b = col & 255;
      if (ids.length > 4) {                            // big frame (fn6): R G B + up to 13 key ids
        for (let i = 0; i < ids.length; i += 13) {
          const p = new Uint8Array(16);
          const part = ids.slice(i, i + 13);
          p[0] = r; p[1] = g; p[2] = b; p.set(part, 3);
          if (3 + part.length < 16) p[3 + part.length] = 0xff;
          request(dev, this.perKey, 0x60, p);
        }
      } else for (const id of ids) little.push([id, r, g, b]);
    }
    for (let i = 0; i < little.length; i += 4) {
      const p = new Uint8Array(16);
      const n = Math.min(4, little.length - i);
      for (let j = 0; j < n; j++) p.set(little[i + j], j * 4);
      if (n < 4) p[n * 4] = 0xff;
      request(dev, this.perKey, 0x10, p);
    }
    request(dev, this.perKey, 0x70);                   // commit
  },

  tests: [
    {
      name: 'finds 0x8081/0x8071/0x4522, init like InitializeDirect, one big frame + commit',
      device: { productId: 0xc33f, reportSize: 20 },
      replies: [
        { when: '11FF00' + '0F' + '8081', reply: '11FF000F10' },
        { when: '11FF00' + '0F' + '8071', reply: '11FF000F0F' },
        { when: '11FF00' + '0F' + '4522', reply: '11FF000F08' },
        { when: '11FF080F', reply: '11FF080F' },
        { when: '11FF083F', reply: '11FF083F' },
        { when: '11FF0F0FFFFF00', reply: '11FF0F0F00000200' },
        { when: '11FF0F1F', reply: '11FF0F1F' },
        { when: '11FF101F', reply: '11FF101F' },
        { when: '11FF106F', reply: '11FF106F' },
        { when: '11FF107F', reply: '11FF107F' },
      ],
      colors: ['#102030'],
      expectZones: 189,
      // OpenRGB InitializeDirect: 4522 fn3/fn1, 8071 fn1 per cluster (off), SendSingleLed(0x29 black) + commit
      expectInit: ['11FF083F', '11FF081F', '11FF0F1F0000', '11FF0F1F0100', '11FF101F29000000FF', '11FF107F'],
      // 117 identical keys -> OpenRGB DeviceUpdateLEDs sends 6F packets of 13 key ids (R G B, ids...), matrix scan order
      expectFrame: ['11FF106F102030D2992637' + '38393A3B3C3D3E3F40', '11FF106F10203041424344459E9B9D9CB4321B1C'],
    },
    { name: 'declines other collections', device: { reportSize: 64 }, expectDeclined: true },
  ],
};
