// Kingston Fury Beast / Renegade DDR4 + DDR5 RGB over SMBus.
// Protocol per OpenRGB (Controllers/KingstonFuryDRAMController: KingstonFuryDRAMController.cpp SendBegin / SetMode /
// SetLEDColors / SendApply, KingstonFuryDRAMControllerDetect.cpp TestForFurySignature, RGBController_KingstonFuryDRAM.cpp
// DeviceUpdateLEDs for the Direct mode). Needs admin + PawnIO.
// Glint enforces: only the addresses below, never the SPD EEPROM (0x50-0x57), writes only after a read answered.
//
// DDR4 sticks live at 0x58 + slot, DDR5 sticks at 0x60 + slot. Identification WITHOUT the SPD (Glint blocks 0x50-0x57):
// OpenRGB first reads the SPD manufacturer data to recognise some DDR5 kits and skips the signature check for those.
// Here every module must show the controller's own signature instead: registers 1..4 (high byte) read "FURY" and
// register 6 (high byte) holds a known model code. DDR5 kits that OpenRGB only recognises through the SPD, i.e. that do
// not show the signature, are therefore NOT supported (skipped, never written to).
// Order of operations per candidate: read (does anything answer?), rule out a Corsair stick (reg 0x43), read the signature;
// only when the signature is not readable yet, send the "begin transfer" write OpenRGB sends before reading, then read again.
const DDR4_BASE = 0x58, DDR5_BASE = 0x60, SLOTS = 8;
const DDR4_MODELS = [0x21, 0x23];
const DDR5_MODELS = [0x10, 0x11, 0x12, 0x15];
const LEDS_DDR4 = 10, LEDS_DDR5 = 12;
const R_MODEL = 0x06, R_APPLY = 0x08, R_MODE = 0x09, R_BRIGHTNESS = 0x20, R_NUM_SLOTS = 0x27, R_RED = 0x50;
const BEGIN = 0x53, END = 0x44, MODE_DIRECT = 0x10, DELAY = 10;
const SIG = [0x46, 0x55, 0x52, 0x59];   // "FURY"

function hi(w) { return w < 0 || w === 0xffff ? -1 : (w >> 8) & 0xff; }
function signature(bus, a) {
  for (let i = 0; i < 4; i++) {
    let c = -1;
    for (let t = 0; t < 3 && c < 0; t++) { c = hi(bus.readWord(a, i + 1)); if (c < 0) bus.sleep(DELAY); }
    if (c !== SIG[i]) return false;
  }
  return true;
}
// 'ddr4' | 'ddr5' | null
function identify(bus, a) {
  if (bus.readWord(a, 1) < 0 || bus.readWord(a, 1) === 0xffff) return null;       // nothing answers
  const id = bus.readByte(a, 0x43);                                                 // Corsair Vengeance/Dominator: not ours
  if (id === 0x1a || id === 0x1b || id === 0x1c) return null;
  // Only sticks whose "FURY" signature is readable straight away. OpenRGB may send a begin-transfer write first,
  // but without the SPD (permanently blocked in Glint) that would be a write to an unidentified device.
  const ok = signature(bus, a);
  let kind = null;
  if (ok) {
    const model = hi(bus.readWord(a, R_MODEL));
    if (a < DDR5_BASE) { if (DDR4_MODELS.includes(model)) kind = 'ddr4'; }
    else if (DDR5_MODELS.includes(model)) kind = 'ddr5';
    if (!kind) bus.log(`0x${a.toString(16)}: Fury signature but unknown model code 0x${model.toString(16)}, skipped`);
  }
  return kind;
}

module.exports = {
  type: 'smbus',
  id: 'ram',
  name: 'Kingston Fury DRAM',
  addresses: Array.from({ length: SLOTS * 2 }, (_, i) => DDR4_BASE + i),   // 0x58-0x67
  minGapMs: 100,

  init(bus) {
    this.sticks = []; this.cache = {};
    for (const a of module.exports.addresses) {
      const kind = identify(bus, a);
      if (!kind) continue;
      bus.log(`0x${a.toString(16)}: Kingston Fury ${kind.toUpperCase()}`);
      this.sticks.push({ addr: a, leds: kind === 'ddr4' ? LEDS_DDR4 : LEDS_DDR5 });
    }
    if (!this.sticks.length) return false;
    return { zones: this.sticks.map((s, i) => ({ id: 'dimm' + (i + 1), name: 'RAM stick ' + (i + 1), leds: s.leds, kind: 'stick' })) };
  },

  render(bus, rgb) {
    const S = this.sticks, cache = this.cache;
    // SetRegister: one register on every stick, cached; the controller is slow so wait once after a changed batch
    const setAll = (reg, vals) => {
      let wrote = false;
      S.forEach((s, i) => {
        const c = cache[s.addr] ?? (cache[s.addr] = {});
        if (c[reg] !== vals[i] && bus.writeByte(s.addr, reg, vals[i])) { c[reg] = vals[i]; wrote = true; }
      });
      if (wrote) bus.sleep(DELAY);
    };
    const same = v => S.map(() => v);
    for (const s of S) bus.writeByte(s.addr, R_APPLY, BEGIN);          // SendBegin
    bus.sleep(DELAY);
    setAll(R_MODE, same(MODE_DIRECT));                                 // SetMode(direct)
    const per = S[0].leds;
    let offs = []; { let o = 0; for (const s of S) { offs.push(o); o += s.leds; } }
    for (let led = 0; led < per; led++) {                              // SetLEDColors: red, blue, green register per LED
      const col = c => S.map((s, i) => rgb[(offs[i] + led) * 3 + c]);
      setAll(R_RED + 3 * led, col(0));
      setAll(R_RED + 3 * led + 2, col(2));
      setAll(R_RED + 3 * led + 1, col(1));
    }
    setAll(R_BRIGHTNESS, same(100));                                   // full brightness (Glint scales colours itself)
    setAll(R_NUM_SLOTS, same(Math.min(S.length, 4)));
    for (const s of S) bus.writeByte(s.addr, R_APPLY, END);            // SendApply
    bus.sleep(DELAY);
  },

  // Simulated-device tests: run with `Glint.exe plugincheck`. (plugincheck flags every write whose text starts with "5" as
  // an SPD write, which would trip on DDR4 addresses 0x58-0x5F, so the happy-path tests use the DDR5 range 0x60+.)
  tests: [
    {
      name: 'two DDR5 Fury sticks: signature + model identified, Direct frame as OpenRGB DeviceUpdateLEDs',
      bus: {
        '0x60': { '0x01': 0x4600, '0x02': 0x5500, '0x03': 0x5200, '0x04': 0x5900, '0x06': 0x1000 },
        '0x61': { '0x01': 0x4600, '0x02': 0x5500, '0x03': 0x5200, '0x04': 0x5900, '0x06': 0x1500 },
        '0x50': { '0x00': 35 },
      },
      colors: ['#102030'],
      expectZones: 24,
      expectFrame: ['60:08=53', '61:08=53', '60:09=10', '61:09=10', '60:50=10', '61:50=10', '60:52=30', '61:52=30', '60:51=20', '61:51=20'],
      expectBusWrites: ['60:20=64', '61:20=64', '60:27=02', '61:27=02', '60:6d=30', '60:08=44', '61:08=44'],
    },
    {
      name: 'one DDR5 Fury at 0x60 (12 LEDs)',
      bus: { '0x60': { '0x01': 0x4600, '0x02': 0x5500, '0x03': 0x5200, '0x04': 0x5900, '0x06': 0x1100 } },
      colors: ['#ff0000'],
      expectZones: 12,
      expectBusWrites: ['60:08=53', '60:09=10', '60:6b=ff', '60:6c=00', '60:27=01', '60:08=44'],
    },
    { name: 'nothing answers: declined', bus: {}, expectDeclined: true },
    { name: 'a Corsair stick in the Fury range is left alone', bus: { '0x60': { '0x43': 0x1a, '0x01': 0x1234 } }, expectDeclined: true },
    { name: 'answers but no FURY signature (some other device): declined', bus: { '0x62': { '0x01': 0x1234 } }, expectDeclined: true },
    { name: 'FURY signature with an unknown model code: declined', bus: { '0x61': { '0x01': 0x4600, '0x02': 0x5500, '0x03': 0x5200, '0x04': 0x5900, '0x06': 0x7700 } }, expectDeclined: true },
  ],
};
