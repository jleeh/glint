// ASRock Polychrome RGB (USB controller on AMD/Intel ASRock boards, 26ce:01a2; also the Deskmini ARGB strip 26ce:01a6).
// Protocol per OpenRGB Controllers/ASRockPolychromeUSBController (ASRockPolychromeUSBController.cpp):
//   config tables are read with 0x14 (cfg 2 = LED counts per zone, cfg 3 = RGSWAP bits, cfg 1 = zone-present bits),
//   a zone is put in direct mode with 0x10 (mode 0x0F), and colours are streamed in 0xE3/0xE4 chunks (WriteAllZones).
// Zones: RGB header 1/2, ARGB header 1/2, PCH, IO cover, PCB, audio/ARGB header 3 - whichever the board reports.
// ARGB header lengths come from the board; if you change one on the Devices page the board's LED-count table is
// rewritten (cfg 2), exactly as OpenRGB does. On exit each zone is returned to the mode it had and the RGSWAP bits are
// restored. Never sends the commit command (0x12). ASRock Polychrome RGB / A-Tuning must not be running.
const NAMES = ['RGB LED 1 Header', 'RGB LED 2 Header', 'Addressable Header 1', 'Addressable Header 2', 'PCH', 'IO Cover', 'PCB', 'Addressable Header 3 / Audio'];
const IDS = ['rgb1', 'rgb2', 'argb1', 'argb2', 'pch', 'io', 'pcb', 'argb3'];
const ADDRESSABLE = [2, 3, 7];
const ARGB_MAX = 100;               // ASROCK_ADDRESSABLE_MAX_LEDS
const NOT_PRESENT = 0x1e;           // POLYCHROME_USB_ZONE_UNAVAILABLE

module.exports = {
  type: 'device',
  id: 'polychrome',
  name: 'ASRock Polychrome (motherboard)',
  vendorId: 0x26ce,
  productIds: [0x01a2, 0x01a6],
  reportSize: 65,
  minGapMs: 30,

  init(dev) {
    if (dev.reportSize !== 65) return false;
    // Read the three config tables. dev.read() includes the report-id byte, so OpenRGB's usb_buf[n] is r[n + 1].
    const counts = table(dev, 0x02), swapRow = table(dev, 0x03), present = table(dev, 0x01);
    if (!counts || !swapRow || !present) { dev.log('no answer to the config-table reads'); return false; }
    const cfg = [];
    for (let i = 0; i < 8; i++) cfg.push(counts[5 + i]);
    const rgswapBits = swapRow[5], presentBits = present[5];
    for (const c of cfg) if (c !== NOT_PRESENT && c > ARGB_MAX) { dev.log('implausible LED count table'); return false; }
    this.cfg = cfg; this.rgswapBits = rgswapBits;
    this.zones = [];
    const zones = [];
    for (let z = 0; z < 8; z++) {
      if (cfg[z] === NOT_PRESENT || !((presentBits >> z) & 1)) continue;
      const addressable = ADDRESSABLE.includes(z);
      let n = cfg[z];
      if (addressable) n = Math.max(0, Math.min(ARGB_MAX, dev.ledCount(IDS[z], n)));
      if (n === 0) continue;
      // current zone state, so it can be restored on exit
      const prev = zoneConfig(dev, z, ((rgswapBits >> z) & 1) === 1);
      this.zones.push({ z, n, max: addressable ? ARGB_MAX : cfg[z], swap: ((rgswapBits >> z) & 1) === 1, prev });
      zones.push({ id: IDS[z], name: NAMES[z], leds: n, kind: n === 1 ? 'single' : 'strip', category: 'case' });
    }
    if (!zones.length) return false;
    // user changed an ARGB header length: the board's LED-count table has to match the stream layout
    let resized = false;
    for (const e of this.zones) if (e.n !== cfg[e.z] && ADDRESSABLE.includes(e.z)) { cfg[e.z] = e.n; resized = true; }
    if (resized) writeHeader(dev, 0x02, cfg);
    // Direct mode: RGSWAP off in the board's table (OpenRGB: "RGSwap causes flashing on each update in direct mode"),
    // then every zone to mode 0x0F
    writeHeader(dev, 0x03, [0]);
    for (const e of this.zones) writeZone(dev, e.z, 0x0f, e.prev.speed, [0, 0, 0], e.swap);
    dev.log(this.zones.map(e => IDS[e.z] + '=' + e.n).join(', '));
    return { zones };
  },

  render(dev, rgb) {
    // WriteAllZones: [each zone's LEDs, G R B (R G B when the zone's swap bit is set)] padded to (sum of max LEDs + 8) * 3
    let total = 8;
    for (const e of this.zones) total += e.max;
    const data = new Uint8Array(total * 3);
    let p = 0, off = 0;
    for (const e of this.zones) {
      for (let i = 0; i < e.n; i++) {
        const r = rgb[(off + i) * 3], g = rgb[(off + i) * 3 + 1], b = rgb[(off + i) * 3 + 2];
        if (e.swap) { data[p++] = r; data[p++] = g; } else { data[p++] = g; data[p++] = r; }
        data[p++] = b;
      }
      off += e.n;
    }
    // Zones that are present but have fewer LEDs than their max leave a gap in OpenRGB's stream too: it only pads at the end.
    let idx = 0, first = true;
    while (idx < data.length) {
      const buf = new Uint8Array(65);
      buf[1] = 0x10; buf[3] = 0xff; buf[4] = first ? 0xe3 : 0xe4; buf[0x40] = 0x65;
      let o, cap;
      if (first) { buf[7] = 0xff; o = 9; cap = 54; first = false; } else { o = 5; cap = 57; }
      for (let k = 0; k < cap && idx < data.length; k++) buf[o + k] = data[idx++];
      dev.write(buf);
      dev.read(30);
    }
  },

  shutdown(dev) {
    // put the board back the way we found it: zone modes first, then the RGSWAP bits
    for (const e of this.zones || []) if (e.prev.mode < 0x0f) writeZone(dev, e.z, e.prev.mode, e.prev.speed, e.prev.color, e.swap);
    if (this.rgswapBits !== undefined) writeHeader(dev, 0x03, [this.rgswapBits]);
  },

  // Simulated-device tests. Replies are laid out like the reference parses them: OpenRGB's usb_buf[n] is the
  // n-th payload byte, and Glint's read() prepends the (zero) report id.
  tests: [
    {
      // counts: RGB1=1 RGB2=1 ARGB1=20 ARGB2=n/a PCH=1 IO=1 PCB=n/a AUDIO=n/a; present bits 0b00110111; rgswap bits PCH+IO
      name: 'reads the config tables, switches 5 zones to direct mode, streams 0xE3/0xE4 chunks',
      device: { reportSize: 65 },
      replies: [
        { when: '00140002', reply: '00' + '00140002' + '0101141E01011E1E' },
        { when: '00140003', reply: '00' + '00140003' + '30' },
        { when: '00140001', reply: '00' + '00140001' + '37' },
        { when: '0011', reply: '00' + '00110000' + '01' + '000000' + 'E0' },
      ],
      expectZones: 24,
      // header 0x15 cfg 3 = 0; zone 0 -> mode 0x0F, speed 0xE0 from its config
      expectInit: ['00150003' + '00', '00100000' + '0F' + '000000' + 'E0' + 'FF'],
      // ramp: LED0 = (0,1,2) -> G R B = 01 00 02 (zone 0 not swapped); LED1 = (3,4,5) -> 04 03 05
      // (LED 2 = (6,7,8) -> 07 06 08, first ARGB1 LED; the PCH LED lands in a later chunk)
      expectFrame: ['0010' + '00' + 'FF' + 'E3' + '0000' + 'FF' + '00' + '010002' + '040305' + '070608', '001000FFE4'],
    },
    {
      name: 'PCH only, RGSWAP bit set: colour bytes go out as R G B, one 27-byte chunk',
      device: { reportSize: 65 },
      replies: [
        { when: '00140002', reply: '00' + '00140002' + '1E1E1E1E011E1E1E' },
        { when: '00140003', reply: '00' + '00140003' + '10' },
        { when: '00140001', reply: '00' + '00140001' + '10' },
        { when: '0011', reply: '00' + '00110004' + '02' + '000000' + 'E0' },
      ],
      expectZones: 1,
      expectInit: ['00150003' + '00', '00100004' + '0F'],
      expectFrame: ['001000FFE3' + '0000' + 'FF' + '00' + '000102'],
    },
    {
      name: 'declines when the config tables say there are no zones',
      device: { reportSize: 65 },
      replies: [
        { when: '00140002', reply: '00' + '00140002' + '0000000000000000' },
        { when: '00140003', reply: '00' + '00140003' + '00' },
        { when: '00140001', reply: '00' + '00140001' + '00' },
      ],
      expectDeclined: true,
    },
    { name: 'declines a collection without 65-byte reports', device: { reportSize: 8 }, expectDeclined: true },
  ],
};

// 0x14 = read header table <cfg>; returns the reply (with the report id byte) or null.
function table(dev, cfg) {
  const b = new Uint8Array(65);
  b[1] = 0x14; b[3] = cfg;
  dev.write(b);
  for (let i = 0; i < 4; i++) { const r = dev.read(100); if (r) return r; }
  return null;
}

// 0x15 = write header table <cfg>
function writeHeader(dev, cfg, bytes) {
  const b = new Uint8Array(65);
  b[1] = 0x15; b[3] = cfg;
  for (let i = 0; i < bytes.length && i < 61; i++) b[4 + i] = bytes[i];
  dev.write(b);
  dev.read(50);
}

// 0x10 = set zone (WriteZone)
function writeZone(dev, zone, mode, speed, color, swap) {
  const b = new Uint8Array(65);
  b[1] = 0x10; b[3] = zone; b[4] = mode;
  if (swap) { b[5] = color[0]; b[6] = color[1]; } else { b[5] = color[1]; b[6] = color[0]; }
  b[7] = color[2]; b[8] = speed; b[9] = 0xff; b[0x10] = 0;
  dev.write(b);
  dev.read(50);
}

// 0x11 = read zone config (GetZoneConfig): mode, colour, speed. Chroma/per-LED modes (0xE2/0xE3) count as direct.
function zoneConfig(dev, zone, swap) {
  const b = new Uint8Array(65);
  b[1] = 0x11; b[3] = zone;
  dev.write(b);
  const r = dev.read(100);
  if (!r) return { mode: 0x0f, speed: 0xe0, color: [0, 0, 0] };
  const m = r[5];
  const mode = m !== 0xe2 && m !== 0xe3 && m < 0x0f ? m : 0x0f;
  // G and R are swapped when the zone's RGSWAP bit is off
  const color = swap ? [r[6], r[7], r[8]] : [r[7], r[6], r[8]];
  return { mode, speed: r[9], color };
}
