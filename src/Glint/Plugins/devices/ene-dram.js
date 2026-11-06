// ENE / ASUS Aura-compatible DRAM over SMBus: G.Skill Trident Z RGB / Neo, T-Force Delta RGB, Geil Super Luce and
// other ENE DIMM_LED / AUDA0 modules. Protocol per OpenRGB (Controllers/ENESMBusController: ENESMBusController.cpp,
// ENESMBusInterface_i2c_smbus.cpp, ENESMBusControllerDetect.cpp DetectENESMBusDRAMControllers + TestForENESMBusController).
// Needs admin + PawnIO. Glint enforces: only the addresses below, never the SPD EEPROM (0x50-0x57),
// and writes only to an address that answered a read first.
//
// Every ENE register is 16 bit: a word write of the byte-swapped register number to SMBus reg 0x00 sets the pointer,
// then reg 0x81 reads / reg 0x01 writes a byte / reg 0x03 writes a short block (max 3 bytes) at that pointer.
// Identification (all reads, before anything is written): registers 0xA0..0xAF must read 0,1,2..15, the
// "Micron" string at 0x1030 must be absent (that is a Crucial Ballistix), and the 16 byte device name at 0x1000 must be
// one of the known DRAM firmwares. Unknown firmwares are declined (OpenRGB also registers them with 0 LEDs).
//
// Sticks power up all answering at 0x77. Like OpenRGB, the remap step moves them (RAM-only, lost at power-off) to
// 0x70-0x76 by writing slot index + new address (<<1) to 0x80F8/0x80F9 on 0x77, only after 0x77 identified as ENE DRAM.
const ADDRS = [0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77];
const R_NAME = 0x1000, R_MICRON = 0x1030, R_CFG = 0x1C00;
const R_DIRECT_V1 = 0x8000, R_DIRECT_V2 = 0x8100, R_DIRECT_ON = 0x8020, R_APPLY = 0x80A0, R_SLOT = 0x80F8, R_ADDR = 0x80F9;
const CFG_LED_COUNT = 0x02;
const FIRMWARES = {   // name → direct colour register (v1 = 0x8000, v2 = 0x8100) per ENESMBusController ctor
  'DIMM_LED-0102': R_DIRECT_V1,      // first gen (Trident Z RGB)
  'DIMM_LED-0103': R_DIRECT_V1,      // first gen (T-Force Delta RGB)
  'AUDA0-E6K5-0101': R_DIRECT_V2,    // second gen (Geil Super Luce)
};

function ptr(bus, a, reg) { bus.writeWord(a, 0x00, ((reg << 8) & 0xff00) | ((reg >> 8) & 0xff)); }
function rd(bus, a, reg) { ptr(bus, a, reg); return bus.readByte(a, 0x81); }
function wr(bus, a, reg, v) { ptr(bus, a, reg); bus.writeByte(a, 0x01, v); }
function str(bus, a, reg, n) {
  let s = '';
  for (let i = 0; i < n; i++) { const c = rd(bus, a, reg + i); if (c <= 0) break; s += String.fromCharCode(c); }
  return s;
}
// Returns { fw, direct, leds } for an ENE DRAM stick at address a, or null. Writes only after the 0xA0 read answered.
function identify(bus, a) {
  for (let i = 0; i < 16; i++) if (bus.readByte(a, 0xa0 + i) !== i) return null;
  if (str(bus, a, R_MICRON, 6) === 'Micron') return null;
  const fw = str(bus, a, R_NAME, 15);
  const direct = FIRMWARES[fw];
  if (direct === undefined) { bus.log(`0x${a.toString(16)}: ENE-style device "${fw}" is not a known DRAM firmware, skipped`); return null; }
  const leds = rd(bus, a, R_CFG + CFG_LED_COUNT);
  if (leds < 1 || leds > 16) { bus.log(`0x${a.toString(16)}: ${fw} reports ${leds} LEDs, skipped`); return null; }
  return { addr: a, fw, direct, leds };
}

module.exports = {
  type: 'smbus',
  id: 'ram',
  name: 'ENE (ASUS Aura) DRAM',
  addresses: ADDRS,
  minGapMs: 60,

  init(bus) {
    this.sticks = [];
    // Remap step (OpenRGB DetectENESMBusDRAMControllers): only when 0x77 is a verified ENE DRAM stick.
    if (identify(bus, 0x77)) {
      let next = 0;
      for (let slot = 0; slot < 8; slot++) {
        if (bus.readByte(0x77, 0xa0) !== 0) break;            // nothing (left) at 0x77
        while (next < 7 && bus.readByte(ADDRS[next], 0xa0) >= 0) next++;   // first free address in 0x70-0x76
        if (next >= 7) break;
        bus.log(`remapping slot ${slot} to 0x${ADDRS[next].toString(16)}`);
        wr(bus, 0x77, R_SLOT, slot);
        wr(bus, 0x77, R_ADDR, ADDRS[next] << 1);
      }
    }
    for (const a of ADDRS) {
      const s = identify(bus, a);
      if (!s) continue;
      bus.log(`0x${a.toString(16)}: ${s.fw}, ${s.leds} LEDs`);
      wr(bus, a, R_DIRECT_ON, 1); wr(bus, a, R_APPLY, 1);     // SetDirect(true): direct colours, apply
      this.sticks.push(s);
    }
    if (!this.sticks.length) return false;
    return { zones: this.sticks.map((s, i) => ({ id: 'dimm' + (i + 1), name: 'RAM stick ' + (i + 1), leds: s.leds, kind: 'stick' })) };
  },

  render(bus, rgb) {
    let off = 0;
    for (const s of this.sticks) {
      const buf = [];
      for (let i = 0; i < s.leds; i++) { const p = (off + i) * 3; buf.push(rgb[p], rgb[p + 2], rgb[p + 1]); }   // ENE order is R, B, G
      for (let sent = 0; sent < buf.length; sent += 3) {    // SetAllColorsDirect: 3 byte blocks
        ptr(bus, s.addr, s.direct + sent);
        const chunk = buf.slice(sent, sent + 3);
        if (!bus.writeBlock(s.addr, 0x03, chunk)) for (const b of chunk) bus.writeByte(s.addr, 0x01, b);
      }
      off += s.leds;
    }
  },

  shutdown(bus) {   // SetDirect(false): hand the sticks back to their own effect engine
    for (const s of this.sticks ?? []) { wr(bus, s.addr, R_DIRECT_ON, 0); wr(bus, s.addr, R_APPLY, 1); }
  },

  // Simulated-device tests: run with `Glint.exe plugincheck`. busMem plays back the pointer-addressed name string
  // and config table (ENESMBusController ctor); expected writes per SetDirect / SetAllColorsDirect.
  tests: [
    {
      name: 'Trident Z RGB (DIMM_LED-0102, 5 LEDs) at 0x70: direct mode on, then R,B,G 3-byte blocks from 0x8000',
      bus: { '0x70': { '0xa0': 0, '0xa1': 1, '0xa2': 2, '0xa3': 3, '0xa4': 4, '0xa5': 5, '0xa6': 6, '0xa7': 7, '0xa8': 8, '0xa9': 9, '0xaa': 10, '0xab': 11, '0xac': 12, '0xad': 13, '0xae': 14, '0xaf': 15 } },
      busMem: { '0x70': { '0x1000': 'DIMM_LED-0102', '0x1c02': [5] } },
      expectZones: 5,
      expectInit: ['70:00=w2080', '70:01=01', '70:00=wa080'],
      expectFrame: ['70:00=w0080', '70:03#000201', '70:00=w0380', '70:03#030504'],
    },
    { name: 'nothing answers: declined', bus: {}, expectDeclined: true },
    {
      name: 'ENE register signature present but unknown firmware name (flat fake): declined',
      bus: { '0x70': { '0xa0': 0, '0xa1': 1, '0xa2': 2, '0xa3': 3, '0xa4': 4, '0xa5': 5, '0xa6': 6, '0xa7': 7, '0xa8': 8, '0xa9': 9, '0xaa': 10, '0xab': 11, '0xac': 12, '0xad': 13, '0xae': 14, '0xaf': 15, '0x81': 0x41 } },
      expectDeclined: true,
    },
    { name: 'some other device at 0x77 (e.g. an I2C mux) fails the signature: declined', bus: { '0x77': { '0xa0': 0x04 } }, expectDeclined: true },
  ],
};
