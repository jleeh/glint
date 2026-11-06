// Gigabyte RGB Fusion 2 USB (ITE IT8297 / IT5702 / IT8950 / IT5711 motherboard controllers): onboard LEDs + ARGB headers.
// Protocol per OpenRGB Controllers/GigabyteRGBFusion2USBController (GigabyteRGBFusion2USBController.cpp,
// GigabyteFusion2USB_Devices.cpp). 64-byte feature reports with id 0xCC; the OpenRGB "Direct" mode is used:
//  - onboard LEDs: one static-effect packet per LED (SetLEDEffect, EFFECT_STATIC, brightness 255) + apply (CC 28 mask)
//  - ARGB headers: raw colour packets (SetStripColors, 19 LEDs per packet, byte order from the chip's calibration)
// Layouts: OpenRGB picks a per-board layout by DMI motherboard name, which plugins cannot read, so this uses OpenRGB's
// generic per-chip layouts (the "GENERIC ..." entries). Onboard zones therefore have generic names (LED 1 ...).
// ARGB header lengths: set them on the Devices page. Never sends the save-to-flash command (CC 5E) or the calibration
// writer (CC 33). Gigabyte RGB Fusion / SIV must not be running.
module.exports = {
  type: 'device',
  id: 'rgbfusion2',
  name: 'Gigabyte RGB Fusion 2 (motherboard)',
  vendorId: 0x048d,
  productIds: [0x8297, 0x5702, 0x8950, 0x5711],
  usagePage: 0xff89,
  reportSize: 64,
  featureReports: true,
  minGapMs: 25,

  init(dev) {
    if (dev.featureSize < 64) return false;
    const pid = dev.productId;
    // CC 60: read the controller's information report (IT8297Report)
    cc(dev, 0x60, 0x00);
    const r = dev.getFeature(0xcc);
    if (!r || r.length < 64 || (r[0] !== 0xcc && r[0] !== 0x00)) return false;
    const fwVer = (r[4] | (r[5] << 8) | (r[6] << 16) | (r[7] << 24)) >>> 0;
    if (fwVer === 0xffffffff) return false;                      // an erased/failed read
    const deviceNum = r[2], supportFlag = r[11], fwId = r[7], fwRev = r[6];
    let product = '';
    for (let i = 12; i < 40 && r[i]; i++) product += String.fromCharCode(r[i]);
    const hwCounts = [r[8] & 15, r[8] >> 4, r[9] & 15, r[9] >> 4, r[10] & 15, r[10] >> 4].map(countOf);
    const cal = [le32(r, 44), le32(r, 48), 0, 0, 0, 0];          // cal_strip0, cal_strip1
    if (pid === 0x5711) {                                         // CC 61: calibration of strips 2-5 (IT5711 only)
      cc(dev, 0x61, 0x00);
      const c = dev.getFeature(0xcc);
      if (c && c.length >= 20) for (let i = 0; i < 4; i++) cal[2 + i] = le32(c, 4 + i * 4);
    }
    // Controller bring-up, as in the RGBFusion2USBController constructor (no flash save)
    if (supportFlag & 0x02) cc(dev, 0x48, 0x00);                  // lamp-array off
    for (let reg = 0x20; reg <= 0x27; reg++) cc(dev, reg, 0x00);  // clear the effect slots
    if (pid === 0x5711) for (let reg = 0x90; reg <= 0x92; reg++) cc(dev, reg, 0x00);
    cc(dev, 0x28, 0xff, pid === 0x5711 ? 0x07 : 0x00);            // fast apply
    cc(dev, 0x31, 0x00);                                          // beat (audio sync) off

    const layout = layoutFor(pid, deviceNum, fwId, fwRev);
    this.items = [];
    const zones = [];
    const need = [0, 0, 0, 0, 0, 0];
    for (const z of layout) {
      if (z.argb) {
        const max = pid === 0x8297 || pid === 0x5702 ? 1024 : 256;
        let n = z.fixed ? z.fixed : dev.ledCount(z.id, Math.min(hwCounts[z.slot], 128));
        n = Math.max(0, Math.min(max, n));
        if (n === 0) continue;
        need[z.slot] = n;
        this.items.push({ kind: 'argb', hdr: z.hdr, slot: z.slot, n, order: order(cal[z.slot], dev, z.id) });
        zones.push({ id: z.id, name: z.name, leds: n, kind: 'strip', category: 'case' });
      } else {
        this.items.push({ kind: 'led', led: z.led });
        zones.push({ id: z.id, name: z.name, leds: 1, kind: 'single', category: 'case' });
      }
    }
    if (!zones.length) return false;
    // ARGB length "divisions" (CC 34): only ever raised, never lowered, so we don't change what the board already allows
    const want = hwCounts.map((h, i) => Math.max(h, enumOf(need[i])));
    if (want.some((w, i) => w !== hwCounts[i])) {
      const b = new Uint8Array(64);
      b[0] = 0xcc; b[1] = 0x34;
      b[2] = (enumIdx(want[1]) << 4) | enumIdx(want[0]);
      b[3] = (enumIdx(want[3]) << 4) | enumIdx(want[2]);
      b[4] = (enumIdx(want[5]) << 4) | enumIdx(want[4]);
      dev.sendFeature(b);
    }
    // Switch the built-in effect engine off for the headers so raw colour packets are shown (CC 32 mask)
    cc(dev, 0x32, pid === 0x5711 ? 0x7b : 0x03);
    dev.sleep(50);
    dev.log(`${product || 'IT' + pid.toString(16)} fw ${fwVer.toString(16)} dev#${deviceNum}: ${zones.map(z => z.id + '=' + z.leds).join(', ')}`);
    return { zones };
  },

  render(dev, rgb) {
    let off = 0, mask = 0;
    for (const it of this.items) {
      if (it.kind === 'led') {
        const led = it.led;
        const p = new Uint8Array(64);
        p[0] = 0xcc;
        if (led < 8) p[1] = 0x20 + led; else if (led < 11) p[1] = 0x90 + (led - 8); else { off += 1; continue; }
        const bit = (1 << led) >>> 0;
        p[2] = bit & 0xff; p[3] = (bit >> 8) & 0xff; p[4] = (bit >> 16) & 0xff; p[5] = (bit >>> 24) & 0xff;
        p[11] = 0x01;                                            // EFFECT_STATIC
        p[12] = 0xff;                                            // max brightness
        p[14] = rgb[off * 3 + 2]; p[15] = rgb[off * 3 + 1]; p[16] = rgb[off * 3]; // color0 = BGR (RGBToBGRColor)
        dev.sendFeature(p);
        mask |= bit;
        off += 1;
      } else {
        for (let done = 0; done < it.n;) {
          const k = Math.min(19, it.n - done);
          const p = new Uint8Array(64);
          p[0] = 0xcc; p[1] = it.hdr;
          const bo = done * 3;
          p[2] = bo & 0xff; p[3] = (bo >> 8) & 0xff; p[4] = k * 3;
          for (let i = 0; i < k; i++) {
            const s = (off + done + i) * 3, d = 5 + i * 3;
            p[d + it.order[0]] = rgb[s]; p[d + it.order[1]] = rgb[s + 1]; p[d + it.order[2]] = rgb[s + 2];
          }
          dev.sendFeature(p);
          done += k;
        }
        off += it.n;
      }
    }
    // apply the onboard LEDs touched this frame (PktEffectApply: zone_sel0 = mask)
    const a = new Uint8Array(64);
    a[0] = 0xcc; a[1] = 0x28;
    a[2] = mask & 0xff; a[3] = (mask >> 8) & 0xff; a[4] = (mask >> 16) & 0xff; a[5] = (mask >>> 24) & 0xff;
    dev.sendFeature(a);
  },

  // Simulated-device tests (the simulated feature report is all zeros: fw 0, calibration 0 -> RGB order, 32-LED headers).
  tests: [
    {
      // GENERIC IT8297/IT5702 layout (GigabyteFusion2USB_Devices.cpp it8297_device): D_LED1, D_LED2 + LED1-5, LED8
      name: 'IT8297 generic layout: bring-up, ARGB strip packets (19 LEDs each, RGB order)',
      device: { productId: 0x8297, reportSize: 64 },
      colors: ['#ff0000'],
      expectZones: 70,
      expectInit: ['CC6000', 'CC2000', 'CC2700', 'CC28FF00', 'CC3100', 'CC3203'],
      // SetStripColors: header 0x58, byte offset 0 / 57, byte count 57 / 39
      expectFrame: ['CC58000039FF0000FF0000', 'CC58390027FF0000', 'CC59000039FF0000'],
    },
    {
      name: 'IT8297: onboard LED1 = static effect on header 0x20 (BGR colour), LED8 on 0x27, apply mask 0x9F',
      device: { productId: 0x8297, reportSize: 64 },
      colors: ['#102030'],
      expectZones: 70,
      // 4 strip packets first, then LED1..5 and LED8, then the apply report
      expectFrame: ['CC5800', 'CC5839', 'CC5900', 'CC5939', 'CC20' + '01000000' + '00000000' + '00' + '01' + 'FF' + '00' + '302010',
        'CC21' + '02000000', 'CC22' + '04000000', 'CC23' + '08000000', 'CC24' + '10000000', 'CC27' + '80000000', 'CC289F000000'],
    },
    {
      name: 'IT5711: four ARGB headers + onboard LEDs (firmware 0: onboard ARGB 27 + 2), fast apply 0x07',
      device: { productId: 0x5711, reportSize: 64 },
      colors: ['#000001'],
      expectZones: 163,
      expectInit: ['CC9000', 'CC9200', 'CC28FF07', 'CC327B'],
      expectFrame: ['CC580000', 'CC583900', 'CC590000', 'CC593900', 'CC620000', 'CC623900', 'CC630000', 'CC633900'],
    },
    {
      name: 'IT8950: two ARGB headers only',
      device: { productId: 0x8950, reportSize: 64 },
      expectZones: 64,
      expectInit: ['CC3203'],
      expectFrame: ['CC580000', 'CC583900', 'CC590000'],
    },
    { name: 'declines a collection without 64-byte feature reports', device: { productId: 0x8297, reportSize: 8 }, expectDeclined: true },
  ],
};

function cc(dev, a, b, c) { dev.sendFeature(new Uint8Array([0xcc, a, b || 0, c || 0])); }
function le32(r, o) { return (r[o] | (r[o + 1] << 8) | (r[o + 2] << 16) | (r[o + 3] << 24)) >>> 0; }
// LED-count divisions (LEDCount enum): 32, 64, 256, 512, 1024
function countOf(e) { return [32, 64, 256, 512, 1024][e < 5 ? e : 4]; }
function enumIdx(n) { return n <= 32 ? 0 : n <= 64 ? 1 : n <= 256 ? 2 : n <= 512 ? 3 : 4; }
function enumOf(n) { return countOf(enumIdx(n)); }

// Colour byte order on the wire: calibration = bytes [R slot, G slot, B slot]; invalid/OFF falls back to R,G,B.
function order(c, dev, id) {
  const b = c & 255, g = (c >> 8) & 255, r = (c >> 16) & 255;
  if ((c >>> 24) === 0 && r < 3 && g < 3 && b < 3 && r !== g && r !== b && g !== b) return [r, g, b];
  dev.log(`${id}: no valid colour-order calibration (0x${c.toString(16)}), assuming RGB`);
  return [0, 1, 2];
}

// Onboard ARGB counts of IT5711 by firmware (ApplyIT5711ZoneCounts with the generic layout id 0x1DF).
function it5711Counts(fwId, fwRev) {
  switch (fwId) {
    case 0x00: return [27, 2];
    case 0x02: return fwRev === 0x20 ? [0, 0] : [14, 6];
    case 0x03: case 0x08: return [13, 0];
    case 0x05: return [8, 4];
    case 0x06: case 0x09: return [12, 0];
    case 0x0c: return fwRev === 0x06 ? [8, 4] : fwRev === 0x08 ? [10, 4] : [0, 0];
    default: return [0, 0];
  }
}

// Generic layouts. argb zones: hdr = ARGB header byte, slot = calibration / LED-count slot.
function layoutFor(pid, deviceNum, fwId, fwRev) {
  const argb = (id, name, hdr, slot, fixed) => ({ argb: true, id, name, hdr, slot, fixed });
  const led = (n, name) => ({ id: 'led' + n, name: name || 'LED ' + n, led: n - 1 });
  if (pid === 0x8950) return [argb('argb1', 'ARGB_V2_1', 0x58, 0), argb('argb2', 'ARGB_V2_2', 0x59, 1)];
  if (pid === 0x5711) {
    if (deviceNum === 1) return [led(1), led(2), led(3), led(4), led(5), led(9), led(10), led(11)];
    const [c10, c11] = it5711Counts(fwId, fwRev);
    const l = [argb('argb1', 'ARGB_V2_1', 0x58, 0), argb('argb2', 'ARGB_V2_2', 0x59, 1), argb('argb3', 'ARGB_V2_3', 0x62, 2),
      argb('argb4', 'ARGB_V2_4', 0x63, 3), led(1), led(2), led(3), led(4), led(5), led(9)];
    l.push(c10 > 0 ? argb('onboard1', 'Onboard ARGB 1', 0x64, 4, c10) : led(10));
    l.push(c11 > 0 ? argb('onboard2', 'Onboard ARGB 2', 0x65, 5, c11) : led(11));
    return l;
  }
  if (deviceNum === 1) return [led(1), led(2), led(3), led(4), led(5), led(6), led(7), led(8)];
  return [argb('dled1', 'D_LED1', 0x58, 0), argb('dled2', 'D_LED2', 0x59, 1), led(1), led(2), led(3), led(4), led(5), led(8)];
}
