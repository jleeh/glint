// Crucial Ballistix RGB DRAM (DDR4, 8 LEDs per module) over SMBus.
// Protocol per OpenRGB (Controllers/CrucialController: CrucialController.cpp SetMode / SendEffectMode / SendDirectColors,
// CrucialControllerDetect.cpp TestForCrucialController + the remap loop in DetectCrucialControllers).
// Needs admin + PawnIO. Glint enforces: only the addresses below, never the SPD EEPROM (0x50-0x57),
// and writes only to an address that answered a read first.
//
// Registers are 16 bit: a word write of the byte-swapped register number to SMBus reg 0x00 sets the pointer, then
// reg 0x81 reads / reg 0x01 writes a byte / reg 0x03 writes a block at that pointer.
// Identification (reads only): registers 0xA0..0xAF must read 0..15 and the string "Micron" must be at 0x1025 or 0x1030.
// New sticks answer at 0x27; like OpenRGB the remap step moves them (RAM-only) to the addresses below, but only after
// 0x27 itself passed that identification.
const ADDRS = [0x39, 0x3a, 0x3b, 0x3c, 0x20, 0x21, 0x22, 0x23];   // remap targets, in OpenRGB's order
const ALL = [0x27, ...ADDRS];
const LEDS = 8;

function ptr(bus, a, reg) { bus.writeWord(a, 0x00, ((reg << 8) & 0xff00) | ((reg >> 8) & 0xff)); }
function rd(bus, a, reg) { ptr(bus, a, reg); return bus.readByte(a, 0x81); }
function wr(bus, a, reg, v) { ptr(bus, a, reg); bus.writeByte(a, 0x01, v); }
function str(bus, a, reg, n) {
  let s = '';
  for (let i = 0; i < n; i++) { const c = rd(bus, a, reg + i); if (c <= 0) break; s += String.fromCharCode(c); }
  return s;
}
function identify(bus, a) {
  for (let i = 0; i < 16; i++) if (bus.readByte(a, 0xa0 + i) !== i) return false;
  return str(bus, a, 0x1025, 16) === 'Micron' || str(bus, a, 0x1030, 16) === 'Micron';
}

module.exports = {
  type: 'smbus',
  id: 'ram',
  name: 'Crucial Ballistix RGB',
  addresses: ALL,
  minGapMs: 60,

  init(bus) {
    this.sticks = [];
    if (identify(bus, 0x27)) {
      let next = -1;
      for (let slot = 0; slot < 4; slot++) {
        if (bus.readByte(0x27, 0xa0) < 0) break;
        do { next++; } while (next < ADDRS.length && bus.readByte(ADDRS[next], 0xa0) >= 0);
        if (next >= ADDRS.length) break;
        bus.log(`remapping slot ${slot} to 0x${ADDRS[next].toString(16)}`);
        wr(bus, 0x27, 0x82ee, slot);
        wr(bus, 0x27, 0x82ef, ADDRS[next] << 1);
        wr(bus, 0x27, 0x82f0, 0xf0);
        bus.sleep(1);
      }
    }
    for (const a of ALL) {
      if (!identify(bus, a)) continue;
      bus.log(`0x${a.toString(16)}: Crucial DRAM, version "${str(bus, a, 0x1000, 16)}"`);
      // SetMode(STATIC) = SendEffectMode(0xCF, 0x10): the controller then takes direct colours
      wr(bus, a, 0x820f, 0xcf); wr(bus, a, 0x82ee, 0x00); wr(bus, a, 0x82ef, 0x10); wr(bus, a, 0x82f0, 0x84);
      this.sticks.push(a);
    }
    if (!this.sticks.length) return false;
    return { zones: this.sticks.map((a, i) => ({ id: 'dimm' + (i + 1), name: 'RAM stick ' + (i + 1), leds: LEDS, kind: 'stick' })) };
  },

  render(bus, rgb) {
    let off = 0;
    for (const a of this.sticks) {
      // SendDirectColors: red block at 0x8300, green at 0x8340, blue at 0x8380, 8 LEDs each
      for (let c = 0; c < 3; c++) {
        const blk = [];
        for (let i = 0; i < LEDS; i++) blk.push(rgb[(off + i) * 3 + c]);
        ptr(bus, a, 0x8300 + c * 0x40);
        if (!bus.writeBlock(a, 0x03, blk)) for (const b of blk) bus.writeByte(a, 0x01, b);
      }
      off += LEDS;
    }
  },

  // Simulated-device tests: run with `Glint.exe plugincheck`. busMem plays back the pointer-addressed "Micron"
  // string; expected writes per CrucialController SendEffectMode / SendDirectColors.
  tests: [
    {
      name: 'Ballistix at 0x39 ("Micron" at 0x1025): static mode, then R/G/B 8-byte blocks at 0x8300/0x8340/0x8380',
      bus: { '0x39': { '0xa0': 0, '0xa1': 1, '0xa2': 2, '0xa3': 3, '0xa4': 4, '0xa5': 5, '0xa6': 6, '0xa7': 7, '0xa8': 8, '0xa9': 9, '0xaa': 10, '0xab': 11, '0xac': 12, '0xad': 13, '0xae': 14, '0xaf': 15 } },
      busMem: { '0x39': { '0x1025': 'Micron' } },
      expectZones: 8,
      expectInit: ['39:00=w0f82', '39:01=cf', '39:00=wf082', '39:01=84'],
      expectFrame: ['39:00=w0083', '39:03#000306090C0F1215', '39:00=w4083', '39:03#0104070A0D101316', '39:00=w8083', '39:03#0205080B0E111417'],
    },
    { name: 'nothing answers: declined', bus: {}, expectDeclined: true },
    {
      name: 'ENE-style signature without the Micron string (an ENE stick, not a Ballistix): declined',
      bus: { '0x3a': { '0xa0': 0, '0xa1': 1, '0xa2': 2, '0xa3': 3, '0xa4': 4, '0xa5': 5, '0xa6': 6, '0xa7': 7, '0xa8': 8, '0xa9': 9, '0xaa': 10, '0xab': 11, '0xac': 12, '0xad': 13, '0xae': 14, '0xaf': 15, '0x81': 0x41 } },
      expectDeclined: true,
    },
    { name: 'unrelated device at 0x27 (wrong signature): declined', bus: { '0x27': { '0xa0': 0x7f } }, expectDeclined: true },
  ],
};
