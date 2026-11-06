// SteelSeries Apex keyboards.
//  - Apex 3 (0x161A, 10 zones) and Apex 3 TKL (0x1622, 8 zones): zone colour packets.
//  - Apex 5 / Apex 7 / Apex 7 TKL / Apex Pro / Apex Pro TKL (first-generation protocol): per-key direct mode.
// Protocol per OpenRGB Controllers/SteelSeriesController: SteelSeriesApexTZoneController (SetColor), SteelSeriesApex8ZoneController
// (SetColor/SetBrightness), SteelSeriesApexController (keys[], SetLEDsDirect with APEX_GEN1_PACKET_ID_DIRECT 0x3A, 643-byte feature
// report, SendDeinitialization = 0x3B, firmware check 0x90 in SteelSeriesApexBaseController::GetVersion). Later generations
// (Apex 9, Apex Pro TKL 2023 / Gen 3, wireless) use another packet id and are NOT handled; a Pro TKL with firmware >= 1.19.7 is declined.
// Never sends the "save" packets (0x06/0x09). The per-key matrix is the standard ANSI full-size grid (the device has no matrix of its own):
// 6 rows x 22 columns, keys placed by HID usage; keys the grid has no cell for (extra ISO/Fn/international keys) are sent black.
const KEYS = [                                         // OpenRGB `keys[]`: the key (HID usage) order of the direct packet
  0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
  0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B,
  0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40,
  0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F, 0x50, 0x51, 0x52, 0x64, 0xE0,
  0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xF0, 0x31, 0x87, 0x88, 0x89, 0x8A, 0x8B, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
  0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F, 0x60, 0x61, 0x62, 0x63, 0xFB,
];
const ROWS = 6, COLS = 22;
const GRID = [                                         // HID usage per cell, 0 = no key
  [0x29, 0, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0, 0, 0, 0, 0],
  [0x35, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x2D, 0x2E, 0x2A, 0x49, 0x4A, 0x4B, 0, 0x53, 0x54, 0x55, 0x56],
  [0x2B, 0x14, 0x1A, 0x08, 0x15, 0x17, 0x1C, 0x18, 0x0C, 0x12, 0x13, 0x2F, 0x30, 0x31, 0x4C, 0x4D, 0x4E, 0, 0x5F, 0x60, 0x61, 0x57],
  [0x39, 0x04, 0x16, 0x07, 0x09, 0x0A, 0x0B, 0x0D, 0x0E, 0x0F, 0x33, 0x34, 0x32, 0x28, 0, 0, 0, 0, 0x5C, 0x5D, 0x5E, 0],
  [0xE1, 0x64, 0x1D, 0x1B, 0x06, 0x19, 0x05, 0x11, 0x10, 0x36, 0x37, 0x38, 0, 0xE5, 0, 0x52, 0, 0, 0x59, 0x5A, 0x5B, 0x58],
  [0xE0, 0xE3, 0xE2, 0, 0, 0x2C, 0, 0, 0xE6, 0xE7, 0, 0, 0, 0xE4, 0x50, 0x51, 0x4F, 0, 0x62, 0, 0x63, 0],
];
const CELL = new Map();                                // usage -> cell index
GRID.forEach((row, r) => row.forEach((u, c) => { if (u) CELL.set(u, r * COLS + c); }));
const PACKET = 643;                                    // APEX_PACKET_LENGTH (feature report incl. report id)
const hexs = [0x161c, 0x1612, 0x1618, 0x1610, 0x1614];

module.exports = {
  type: 'device',
  id: dev => 'apex' + dev.productId.toString(16),
  name: 'SteelSeries Apex',
  vendorId: 0x1038,
  productIds: [0x161a, 0x1622, ...hexs],
  minGapMs: 20,

  init(dev) {
    const pid = dev.productId, iface = dev.interfaceNumber;
    if (pid === 0x161a) {                                // Apex 3: interface 3, 33-byte reports
      if (iface !== 3) return false;
      this.kind = 'tz'; this.n = 10;
      dev.write([0x00, 0x0a, 0x00, 0x64]);               // brightness 100 (0x0A packet)
      return { zones: [{ id: 'keys', name: 'Apex 3', leds: 10, kind: 'strip', category: 'keyboard' }] };
    }
    if (pid === 0x1622) {                                // Apex 3 TKL: interface 1, 65-byte reports
      if (iface !== 1) return false;
      this.kind = '8z'; this.n = 8;
      dev.write([0x00, 0x23, 0x10]);                     // brightness max (0x10)
      return { zones: [{ id: 'keys', name: 'Apex 3 TKL', leds: 8, kind: 'strip', category: 'keyboard' }] };
    }
    if (iface !== 1) return false;                       // Apex 5/7/Pro: interface 1
    // firmware query (0x90): a first-generation Apex answers with its version text
    dev.write([0x00, 0x90]);
    const r = dev.read(100);
    if (!r) { dev.log('no firmware reply'); return false; }
    const txt = Array.from(r).map(x => (x >= 0x20 && x < 0x7f) ? String.fromCharCode(x) : ' ').join('');
    const m = /(\d+)\.(\d+)\.(\d+)/.exec(txt);
    if (pid === 0x1614 && m) {                           // Pro TKL: >= 1.19.7 speaks the Gen 3 protocol instead (not supported)
      const v = (+m[1]) * 1e6 + (+m[2]) * 1e3 + (+m[3]);
      if (v >= 1019007) { dev.log('firmware ' + m[0] + ' uses the Gen 3 protocol'); return false; }
    }
    this.kind = 'perkey';
    return { zones: [{ id: 'keys', name: 'Keyboard', leds: ROWS * COLS, kind: 'matrix', cols: COLS, category: 'keyboard' }] };
  },

  render(dev, rgb) {
    if (this.kind === 'tz') {
      const b = new Uint8Array(33); b[1] = 0x0b;
      for (let i = 0; i < this.n * 3; i++) b[3 + i] = rgb[i];
      dev.write(b);
    } else if (this.kind === '8z') {
      const b = new Uint8Array(65); b[1] = 0x21; b[2] = 0xff;
      for (let i = 0; i < this.n * 3; i++) b[3 + i] = rgb[i];
      dev.write(b);
    } else {
      const b = new Uint8Array(PACKET);
      b[1] = 0x3a; b[2] = KEYS.length;                   // report id 0, packet id, key count, then [usage, r, g, b] per key
      KEYS.forEach((u, i) => {
        const cell = CELL.get(u);
        b[3 + i * 4] = u;
        if (cell !== undefined) { b[4 + i * 4] = rgb[cell * 3]; b[5 + i * 4] = rgb[cell * 3 + 1]; b[6 + i * 4] = rgb[cell * 3 + 2]; }
      });
      dev.sendFeature(b);
    }
  },

  // give the keyboard back to its on-board lighting (SendDeinitialization: packet 0x3B)
  shutdown(dev) {
    if (this.kind === 'perkey') { try { dev.write([0x00, 0x3b]); } catch (e) { } }
  },

  tests: [
    {
      name: 'Apex 3: brightness packet, then 10-zone colour packet (OpenRGB SetColor)',
      device: { productId: 0x161a, reportSize: 33, interface: 3 },
      colors: ['#102030'],
      expectZones: 10,
      expectInit: ['000A0064'],
      expectFrame: ['000B00' + '102030'.repeat(10)],
    },
    {
      name: 'Apex 3 TKL: brightness 0x10, then 8-zone packet 0x21/0xFF',
      device: { productId: 0x1622, reportSize: 65, interface: 1 },
      colors: ['#102030'],
      expectZones: 8,
      expectInit: ['002310'],
      expectFrame: ['0021FF' + '102030'.repeat(8)],
    },
    {
      name: 'Apex 7: firmware query then per-key direct packet 0x3A (112 keys)',
      device: { productId: 0x1612, reportSize: 65, interface: 1 },
      replies: [{ when: '0090', reply: '312E322E33' }],
      colors: ['#102030'],
      expectZones: 132,
      expectInit: ['0090'],
      // report id 0, 0x3A, 0x70 keys, then [usage r g b]: A, B, C ...; the 0x31 backslash etc. are grid cells too
      expectFrame: ['003A70' + '04102030' + '05102030' + '06102030'],
    },
    {
      name: 'Apex Pro TKL with Gen 3 firmware 1.19.7 is declined',
      device: { productId: 0x1614, reportSize: 65, interface: 1 },
      replies: [{ when: '0090', reply: '312E31392E37' }],
      expectDeclined: true,
    },
    { name: 'declines the wrong interface', device: { productId: 0x1612, reportSize: 65, interface: 0 }, expectDeclined: true },
  ],
};
