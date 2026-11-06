// Corsair RGB DRAM over SMBus: Dominator Platinum / Titanium, Vengeance RGB (DDR4 + DDR5).
// Protocol per OpenRGB (Controllers/CorsairDRAMController). Verified on real hardware.
// Needs admin + PawnIO. Glint enforces: only the addresses below, never the SPD EEPROM (0x50-0x57),
// and writes only to a module that answered the identification reads first.
const MODELS = {   // pid high byte → [name, LEDs, reversed]
  0x01: ['Vengeance RGB Pro DDR4', 10, false], 0x02: ['Dominator Platinum RGB DDR4', 12, true],
  0x03: ['Vengeance RGB Pro SL DDR4', 10, false], 0x04: ['Vengeance RGB RS DDR4', 6, false],
  0x06: ['Dominator Platinum RGB DDR5', 12, true], 0x08: ['Dominator Titanium RGB DDR5', 12, true],
  0x07: ['Vengeance RGB DDR5', 10, false], 0x09: ['Vengeance RGB DDR5', 10, false],
  0x0a: ['Vengeance Shugo DDR5', 10, false], 0x0b: ['Vengeance RGB RS DDR5', 6, false],
};

function crc8(bytes, n) {
  let c = 0;
  for (let i = 0; i < n; i++) { c ^= bytes[i]; for (let k = 0; k < 8; k++) c = (c & 0x80) ? ((c << 1) ^ 0x07) & 0xff : (c << 1) & 0xff; }
  return c;
}

module.exports = {
  type: 'smbus',
  id: 'ram',
  name: 'Corsair DRAM',
  addresses: [0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x58, 0x59, 0x5a, 0x5b, 0x5c, 0x5d, 0x5e, 0x5f],
  minGapMs: 90,   // each frame busy-waits the bus ~4.5 ms per stick; ~10 fps keeps CPU low

  init(bus) {
    this.sticks = [];
    for (const a of module.exports.addresses) {
      const id = bus.readByte(a, 0x43);
      if (id !== 0x1a && id !== 0x1b && id !== 0x1c) continue;
      const rev = bus.readByte(a, 0x44);
      if (rev !== 0x01 && rev !== 0x03 && rev !== 0x04) continue;
      // device info buffer
      bus.writeByte(a, 0x61, 0x00);
      bus.writeByte(a, 0x21, 0x00);
      const info = [];
      for (let i = 0; i < 32; i++) info.push(Math.max(0, bus.readByte(a, 0x40)));
      const crc = bus.readByte(a, 0x42);
      if (crc !== crc8(info, 32)) bus.log(`0x${a.toString(16)}: device info CRC mismatch`);
      const pid = info[2] | (info[3] << 8), proto = info[28];
      const [model, leds, reversed] = MODELS[pid >> 8] ?? ['Unknown Corsair DRAM', 10, false];
      bus.log(`0x${a.toString(16)}: ${model} pid 0x${pid.toString(16)} fw ${info[9]}.${info[8]}.${info[10] | (info[11] << 8)} protocol ${proto}`);
      if (proto < 4) { bus.log(`0x${a.toString(16)}: protocol ${proto} has no direct mode, skipped`); continue; }
      this.sticks.push({ addr: a, leds, reversed });
    }
    if (!this.sticks.length) return false;
    return { zones: this.sticks.map((s, i) => ({ id: 'dimm' + (i + 1), name: 'RAM stick ' + (i + 1), leds: s.leds, kind: 'stick' })) };
  },

  render(bus, rgb) {
    let off = 0;
    for (const s of this.sticks) {
      const p = new Uint8Array(s.leds * 3 + 2);
      p[0] = s.leds;
      for (let i = 0; i < s.leds; i++) {
        const src = (off + (s.reversed ? s.leds - 1 - i : i)) * 3;
        p[1 + i * 3] = rgb[src]; p[2 + i * 3] = rgb[src + 1]; p[3 + i * 3] = rgb[src + 2];
      }
      p[p.length - 1] = crc8(p, p.length - 1);
      bus.writeBlock(s.addr, 0x31, p.subarray(0, Math.min(32, p.length)));
      if (p.length > 32) bus.writeBlock(s.addr, 0x32, p.subarray(32));
      off += s.leds;
    }
  },

  // Simulated-device tests: run with `Glint.exe plugincheck` (also part of the selftest).
  tests: [
    {
      name: 'finds two Dominator sticks, never touches the SPD hub',
      bus: { '0x19': { '0x43': 26, '0x44': 4 }, '0x1b': { '0x43': 26, '0x44': 4 }, '0x50': { '0x00': 35 } },
      busInfo: '0000010600000000090100000000000000000000000000000000000004000000',
      colors: ['#0c0000'],
      expectZones: 24,
      expectBusWrites: ['19:31#0C0C0000', '19:32#', '1b:31#0C0C0000'],
    },
  ],
};
