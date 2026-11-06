// Razer Chroma (wired) keyboards, mice, mouse mats and Base Station: the 90-byte Razer feature report
// (transaction id + XOR CRC), custom-frame row writes, then "custom" mode to display the frame.
// Protocol per OpenRGB Controllers/RazerController/RazerController.cpp (razer_create_report, razer_calculate_crc,
// razer_create_custom_frame_{extended,standard}_matrix_report / _linear_report, razer_create_mode_custom_*_report,
// razer_set_custom_frame, razer_set_mode_custom, firmware query razer_get_firmware) and the device table
// (name, matrix type, transaction id, rows, cols) from RazerDevices.cpp. Only matrix types STANDARD / EXTENDED /
// LINEAR are covered; wireless dongles, laptops, headsets, addressable controllers, Kraken etc. are not.
// Nothing is saved to the device (storage byte 0 everywhere). Close Razer Synapse first: it fights over the LEDs.
// Table row: [name, category, matrix type (1 standard, 2 extended, 3 linear), transaction id, rows, cols].
const DEVICES = {
  0x0241: ['Razer BlackWidow 2019', 'keyboard', 2, 0x1F, 6, 22],
  0x0203: ['Razer BlackWidow Chroma', 'keyboard', 1, 0x3F, 6, 22],
  0x0211: ['Razer Blackwidow Chroma Overwatch', 'keyboard', 1, 0x3F, 6, 22],
  0x0209: ['Razer BlackWidow Chroma Tournament Edition', 'keyboard', 1, 0x3F, 6, 22],
  0x0221: ['Razer BlackWidow Chroma V2', 'keyboard', 1, 0x3F, 6, 22],
  0x0228: ['Razer BlackWidow Elite', 'keyboard', 2, 0x1F, 6, 22],
  0x024e: ['Razer Blackwidow V3', 'keyboard', 2, 0x3F, 6, 22],
  0x025a: ['Razer BlackWidow V3 Pro (Wired)', 'keyboard', 2, 0x3F, 6, 22],
  0x0a24: ['Razer BlackWidow V3 TKL', 'keyboard', 2, 0x3F, 6, 18],
  0x0258: ['Razer BlackWidow V3 Mini (Wired)', 'keyboard', 2, 0x1F, 5, 16],
  0x0287: ['Razer Blackwidow V4', 'keyboard', 2, 0x1F, 8, 23],
  0x028d: ['Razer Blackwidow V4 Pro', 'keyboard', 2, 0x1F, 8, 23],
  0x02b3: ['Razer Blackwidow V4 Pro 75% (Wired)', 'keyboard', 2, 0x1F, 6, 18],
  0x02a5: ['Razer Blackwidow V4 75% (Wired)', 'keyboard', 2, 0x1F, 6, 18],
  0x0293: ['Razer Blackwidow V4 X', 'keyboard', 2, 0x1F, 6, 22],
  0x02d7: ['Razer Blackwidow V4 TKL (Wired)', 'keyboard', 2, 0x1F, 6, 18],
  0x02d4: ['Razer Blackwidow V4 Low Profile TKL (Wired)', 'keyboard', 2, 0x1F, 6, 18],
  0x0216: ['Razer BlackWidow X Chroma', 'keyboard', 1, 0x3F, 6, 22],
  0x021a: ['Razer BlackWidow X Chroma Tournament Edition', 'keyboard', 1, 0x3F, 6, 22],
  0x022a: ['Razer Cynosa Chroma', 'keyboard', 2, 0x3F, 6, 22],
  0x025e: ['Razer Cynosa v2', 'keyboard', 2, 0x1F, 6, 22],
  0x023f: ['Razer Cynosa Lite', 'keyboard', 2, 0x3F, 1, 1],
  0x021e: ['Razer Ornata Chroma', 'keyboard', 2, 0x3F, 6, 22],
  0x025d: ['Razer Ornata Chroma V2', 'keyboard', 2, 0x1F, 6, 22],
  0x028f: ['Razer Ornata V3', 'keyboard', 2, 0x1F, 1, 10],
  0x02a1: ['Razer Ornata V3 rev2', 'keyboard', 2, 0x1F, 1, 10],
  0x02a3: ['Razer Ornata V3 TKL', 'keyboard', 2, 0x1F, 1, 8],
  0x0294: ['Razer Ornata V3 X', 'keyboard', 2, 0x1F, 1, 1],
  0x02a2: ['Razer Ornata V3 X Rev2', 'keyboard', 2, 0x1F, 1, 1],
  0x0204: ['Razer DeathStalker Chroma', 'keyboard', 3, 0x3F, 1, 12],
  0x0295: ['Razer Deathstalker V2', 'keyboard', 2, 0x1F, 6, 22],
  0x0298: ['Razer DeathStalker V2 Pro TKL (Wired)', 'keyboard', 2, 0x1F, 6, 17],
  0x0292: ['Razer DeathStalker V2 Pro (Wired)', 'keyboard', 2, 0x9F, 6, 22],
  0x0227: ['Razer Huntsman', 'keyboard', 2, 0x3F, 6, 22],
  0x0226: ['Razer Huntsman Elite', 'keyboard', 2, 0x3F, 9, 22],
  0x0266: ['Razer Huntsman V2 Analog', 'keyboard', 2, 0x3F, 9, 22],
  0x0257: ['Razer Huntsman Mini', 'keyboard', 2, 0x3F, 5, 15],
  0x0282: ['Razer Huntsman Mini Analog', 'keyboard', 2, 0x1F, 5, 15],
  0x0243: ['Razer Huntsman Tournament Edition', 'keyboard', 2, 0x3F, 6, 18],
  0x026b: ['Razer Huntsman V2 TKL', 'keyboard', 2, 0x3F, 6, 17],
  0x026c: ['Razer Huntsman V2', 'keyboard', 2, 0x3F, 6, 22],
  0x02a6: ['Razer Huntsman V3 Pro', 'keyboard', 2, 0x3F, 6, 22],
  0x02a7: ['Razer Huntsman V3 Pro TKL White', 'keyboard', 2, 0x3F, 6, 19],
  0x02e4: ['Razer Huntsman V3 HE Magnetic Mini', 'keyboard', 2, 0x3F, 6, 22],
  0x006a: ['Razer Abyssus Elite DVa Edition', 'mouse', 2, 0x3F, 1, 1],
  0x006b: ['Razer Abyssus Essential', 'mouse', 2, 0x3F, 1, 1],
  0x0064: ['Razer Basilisk', 'mouse', 2, 0x3F, 1, 2],
  0x0065: ['Razer Basilisk Essential', 'mouse', 2, 0x3F, 1, 1],
  0x0086: ['Razer Basilisk Ultimate', 'mouse', 2, 0x1F, 1, 14],
  0x0085: ['Razer Basilisk V2', 'mouse', 2, 0x3F, 1, 2],
  0x0099: ['Razer Basilisk V3', 'mouse', 2, 0x1F, 1, 11],
  0x00cb: ['Razer Basilisk V3 35K', 'mouse', 2, 0x1F, 1, 11],
  0x00aa: ['Razer Basilisk V3 Pro (Wired)', 'mouse', 2, 0x1F, 1, 13],
  0x00cc: ['Razer Basilisk V3 Pro 35K (Wired)', 'mouse', 2, 0x1F, 1, 13],
  0x00d6: ['Razer Basilisk V3 Pro 35K Phantom Green Edition (Wired)', 'mouse', 2, 0x1F, 1, 13],
  0x00b9: ['Razer Basilisk V3 X HyperSpeed', 'mouse', 2, 0x1F, 1, 1],
  0x00e2: ['Razer Basilisk V4 Pro (Wired)', 'mouse', 2, 0x1F, 1, 13],
  0x00a3: ['Razer Cobra', 'mouse', 2, 0x1F, 1, 1],
  0x00af: ['Razer Cobra Pro (Wired)', 'mouse', 2, 0x1F, 1, 11],
  0x005c: ['Razer DeathAdder Elite', 'mouse', 2, 0x3F, 1, 2],
  0x006e: ['Razer DeathAdder Essential', 'mouse', 2, 0x3F, 1, 2],
  0x0098: ['Razer DeathAdder Essential V2', 'mouse', 2, 0x1F, 1, 2],
  0x0071: ['Razer DeathAdder Essential (White Edition)', 'mouse', 2, 0x3F, 1, 2],
  0x0084: ['Razer DeathAdder V2', 'mouse', 2, 0x1F, 1, 2],
  0x008c: ['Razer DeathAdder V2 Mini', 'mouse', 2, 0x1F, 1, 1],
  0x007c: ['Razer DeathAdder V2 (Wired)', 'mouse', 2, 0x3F, 1, 1],
  0x004c: ['Razer Diamondback Chroma', 'mouse', 3, 0x3F, 1, 21],
  0x0059: ['Razer Lancehead 2017 (Wired)', 'mouse', 2, 0x3F, 1, 16],
  0x0070: ['Razer Lancehead 2019 (Wired)', 'mouse', 2, 0x3F, 1, 16],
  0x0060: ['Razer Lancehead Tournament Edition', 'mouse', 2, 0x3F, 1, 16],
  0x0044: ['Razer Mamba 2015 (Wired)', 'mouse', 3, 0x3F, 1, 15],
  0x0073: ['Razer Mamba 2018 (Wired)', 'mouse', 2, 0x3F, 1, 2],
  0x006c: ['Razer Mamba Elite', 'mouse', 2, 0x1F, 1, 20],
  0x0046: ['Razer Mamba Tournament Edition', 'mouse', 3, 0x3F, 1, 16],
  0x0069: ['Razer Mamba Hyperflux (Wired)', 'mouse', 2, 0x3F, 1, 2],
  0x0053: ['Razer Naga Chroma', 'mouse', 3, 0x3F, 1, 3],
  0x0093: ['Razer Naga Classic', 'mouse', 2, 0x1F, 1, 3],
  0x0050: ['Razer Naga Hex V2', 'mouse', 1, 0x3F, 1, 3],
  0x008d: ['Razer Naga Left Handed', 'mouse', 2, 0x1F, 1, 3],
  0x0067: ['Razer Naga Trinity', 'mouse', 2, 0x3F, 1, 3],
  0x008f: ['Razer Naga Pro (Wired)', 'mouse', 2, 0x1F, 1, 3],
  0x00a7: ['Razer Naga Pro V2 (Wired)', 'mouse', 2, 0x1F, 1, 2],
  0x00e7: ['Razer Naga Pro V3 (Wired)', 'mouse', 2, 0x1F, 1, 3],
  0x0091: ['Razer Viper 8kHz', 'mouse', 2, 0x1F, 1, 1],
  0x008a: ['Razer Viper Mini', 'mouse', 2, 0x3F, 1, 1],
  0x007a: ['Razer Viper Ultimate (Wired)', 'mouse', 2, 0x3F, 1, 1],
  0x0078: ['Razer Viper', 'mouse', 2, 0x3F, 1, 1],
  0x0244: ['Razer Tartarus Pro', 'keyboard', 2, 0x1F, 1, 21],
  0x022b: ['Razer Tartarus V2', 'keyboard', 2, 0x1F, 4, 6],
  0x0c00: ['Razer Firefly', 'mousepad', 3, 0x3F, 1, 15],
  0x0068: ['Razer Firefly Hyperflux', 'mousepad', 2, 0x3F, 1, 14],
  0x0c04: ['Razer Firefly V2', 'mousepad', 2, 0x3F, 1, 19],
  0x0c08: ['Razer Firefly V2 Pro', 'mousepad', 2, 0x1F, 1, 17],
  0x0c01: ['Razer Goliathus', 'mousepad', 2, 0x3F, 1, 1],
  0x0c06: ['Razer Goliathus Chroma 3XL', 'mousepad', 2, 0x1F, 1, 1],
  0x0c02: ['Razer Goliathus Extended', 'mousepad', 2, 0x3F, 1, 1],
  0x0c05: ['Razer Strider Chroma', 'mousepad', 2, 0x3F, 1, 19],
  0x0f08: ['Razer Base Station Chroma', 'headset', 2, 0x3F, 1, 15],
  0x0f1d: ['Razer Mouse Bungee V3 Chroma', 'mouse', 2, 0x1F, 1, 8],
  0x0f20: ['Razer Base Station V2 Chroma', 'headset', 2, 0x1F, 1, 8],
};
const SIZE = 91;                                      // report id byte + 90-byte Razer report

function report(tid, cls, cmd, args, size) {
  const b = new Uint8Array(SIZE);                     // [0] report id 0, [1] status, [2] transaction id, [3..4] remaining packets,
  b[2] = tid; b[6] = size === undefined ? args.length : size; b[7] = cls; b[8] = cmd;   // [5] protocol, [6] data size, [7] command class, [8] command id
  b.set(args, 9);
  let crc = 0;
  for (let i = 3; i < 89; i++) crc ^= b[i];           // OpenRGB razer_calculate_crc: xor of bytes 3..88
  b[89] = crc;
  return b;
}

module.exports = {
  type: 'device',
  id: dev => 'razer' + dev.productId.toString(16),
  name: 'Razer Chroma',
  vendorId: 0x1532,
  productIds: Object.keys(DEVICES).map(Number),
  featureReports: true,
  minGapMs: 16,

  init(dev) {
    const d = DEVICES[dev.productId];
    if (!d) return false;
    if (dev.featureSize < SIZE) return false;          // only the HID collection that carries the 90-byte Razer report
    const [name, category, , tid, rows, cols] = d;
    // firmware query (class 0x00, command 0x81): a Razer device answers with status 2 and the same class/command
    dev.sendFeature(report(tid, 0x00, 0x81, [0, 0]));
    dev.sleep(5);
    const r = dev.getFeature(0);
    if (r && r.length >= SIZE && r[7] === 0x00 && r[8] === 0x81 && (r[1] === 0x02)) dev.log(`${name}: firmware v${r[9]}.${r[10]}`);
    else if (r && r.length >= SIZE && r[1] >= 0x03) { dev.log('Razer command failed/not supported (status ' + r[1] + ')'); return false; }
    this.d = d;
    const leds = rows * cols;
    const zone = rows > 1
      ? { id: 'keys', name, leds, kind: 'matrix', cols, category }
      : { id: 'leds', name, leds, kind: cols === 1 ? 'single' : 'strip', category };
    return { zones: [zone] };
  },

  render(dev, rgb) {
    const [, , matrix, tid, rows, cols] = this.d;
    const n = cols * 3;
    for (let row = 0; row < rows; row++) {
      const data = rgb.subarray(row * n, row * n + n);
      let a;
      if (matrix === 2) { a = new Uint8Array(5 + n); a[2] = row; a[3] = 0; a[4] = cols - 1; a.set(data, 5); dev.sendFeature(report(tid, 0x0f, 0x03, a)); }
      else if (matrix === 1) { a = new Uint8Array(4 + n); a[0] = 0xff; a[1] = row; a[2] = 0; a[3] = cols - 1; a.set(data, 4); dev.sendFeature(report(tid, 0x03, 0x0b, a)); }
      else { a = new Uint8Array(2 + n); a[0] = 0; a[1] = cols - 1; a.set(data, 2); dev.sendFeature(report(tid, 0x03, 0x0c, a, 0x32)); }   // linear frames always declare 0x32 data bytes
      dev.sleep(1);
    }
    // apply the frame: custom effect
    if (matrix === 2) dev.sendFeature(report(tid, 0x0f, 0x02, [0x00, 0x00, 0x08, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
    else dev.sendFeature(report(tid, 0x03, 0x0a, [0x05, 0x00]));
  },

  tests: [
    {
      // frames per OpenRGB razer_create_custom_frame_* / razer_create_mode_custom_*; CRC = xor of bytes 3..88
      name: "BlackWidow V3 (extended 6x22): firmware query, 6 row writes, custom mode",
      device: { productId: 0x024e, reportSize: 91 },
      colors: ['#010203'],
      expectZones: 132,
      expectInit: ['00003F00000002008100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008300'],
      expectFrame: ['00003F000000470F0300000000150102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030000000000000000005E00', '00003F000000470F0300000100150102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030000000000000000005F00', '00003F000000470F0300000200150102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030000000000000000005C00', '00003F000000470F0300000300150102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030000000000000000005D00', '00003F000000470F0300000400150102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030000000000000000005A00', '00003F000000470F0300000500150102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030102030000000000000000005B00', '00003F0000000C0F0200000800000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000900'],
    },
    {
      // frames per OpenRGB razer_create_custom_frame_* / razer_create_mode_custom_*; CRC = xor of bytes 3..88
      name: "Viper Mini (extended 1x1, one LED)",
      device: { productId: 0x008a, reportSize: 91 },
      colors: ['#010203'],
      expectZones: 1,
      expectInit: ['00003F00000002008100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008300'],
      expectFrame: ['00003F000000080F0300000000000102030000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000400', '00003F0000000C0F0200000800000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000900'],
    },
    {
      // frames per OpenRGB razer_create_custom_frame_* / razer_create_mode_custom_*; CRC = xor of bytes 3..88
      name: "DeathAdder V2 (extended 1x2, tid 0x1F)",
      device: { productId: 0x0084, reportSize: 91 },
      colors: ['#010203'],
      expectZones: 2,
      expectInit: ['00001F00000002008100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008300'],
      expectFrame: ['00001F0000000B0F0300000000010102030102030000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000600', '00001F0000000C0F0200000800000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000900'],
    },
    {
      // frames per OpenRGB razer_create_custom_frame_* / razer_create_mode_custom_*; CRC = xor of bytes 3..88
      name: "Naga Chroma (linear 1x3): 0x03/0x0C frame, declared size 0x32, then 0x03/0x0A custom",
      device: { productId: 0x0053, reportSize: 91 },
      colors: ['#010203'],
      expectZones: 3,
      expectInit: ['00003F00000002008100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008300'],
      expectFrame: ['00003F00000032030C00020102030102030102030000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000003F00', '00003F00000002030A05000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000E00'],
    },
    {
      // frames per OpenRGB razer_create_custom_frame_* / razer_create_mode_custom_*; CRC = xor of bytes 3..88
      name: "Naga Hex V2 (standard 1x3): 0x03/0x0B frame with 0xFF header, then custom",
      device: { productId: 0x0050, reportSize: 91 },
      colors: ['#010203'],
      expectZones: 3,
      expectInit: ['00003F00000002008100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008300'],
      expectFrame: ['00003F0000000D030BFF00000201020301020301020300000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000F800', '00003F00000002030A05000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000E00'],
    },
    { name: 'declines the non-Razer-report collection', device: { productId: 0x024e, reportSize: 65 }, expectDeclined: true },
    { name: 'declines a PID that is not in the table', device: { productId: 0x7777, reportSize: 91 }, expectDeclined: true },
  ],
};
