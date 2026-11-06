// Corsair RGB mice that speak the legacy "peripheral" protocol: Glaive RGB / PRO, Harpoon RGB / PRO, Ironclaw RGB, M65 / M65 PRO /
// M65 RGB Elite, Nightsword, Scimitar RGB / PRO / Elite, Sabre RGB / Optical (wired, interface 1, usage page 0xFFC2).
// Protocol per OpenRGB Controllers/CorsairPeripheralController/CorsairPeripheralController.cpp: ReadFirmwareInfo (0E 01, device
// type 0xC1 at byte 0x14), SpecialFunctionControl (07 04 02), LightingControl (07 05 02 00 01), SubmitMouseColors (07 22 <n> 00
// then <zone> R G B per zone; OpenRGB always submits 15 zones), SwitchMode(hardware) = 07 04 01 on exit.
// Which zone slot is which LED comes from RGBController_CorsairPeripheral.cpp's per-model name tables (M65 Elite / Ironclaw: 2 logo,
// 3 DPI, 4 wheel; Sabre RGB: 1 underglow, 2 logo, 3 DPI, 4 wheel; Harpoon PRO: 3 logo); other models are unlabelled in OpenRGB, so
// slots 0-3 are exposed. Only changed colours are written (these mice are slow). Dark Core and the keyboards/mousemats of this protocol
// (K70/K95/K65 - per-key layout tables) are not covered.
const LEDS = [[2, 'Logo'], [3, 'DPI'], [4, 'Scroll wheel']];
const MODELS = {
  0x1b5a: LEDS, 0x1b5d: LEDS,                          // M65 RGB Elite, Ironclaw RGB
  0x1b2f: [[1, 'Underglow'], [2, 'Logo'], [3, 'DPI'], [4, 'Scroll wheel']],   // Sabre RGB
  0x1b75: [[3, 'Logo']],                               // Harpoon RGB PRO
};
const GENERIC = [[0, 'LED 1'], [1, 'LED 2'], [2, 'LED 3'], [3, 'LED 4']];
const PIDS = [0x1b34, 0x1b74, 0x1b3c, 0x1b75, 0x1b5d, 0x1b12, 0x1b2e, 0x1b5a, 0x1b5c, 0x1b1e, 0x1b3e, 0x1b8b, 0x1b2f, 0x1b32];

module.exports = {
  type: 'device',
  id: dev => 'corsairmouse' + dev.productId.toString(16),
  name: 'Corsair mouse',
  vendorId: 0x1b1c,
  productIds: PIDS,
  interface: 1,
  usagePage: 0xffc2,
  reportSize: 65,
  minGapMs: 40,

  init(dev) {
    if (dev.reportSize !== 65) return false;
    // identify: firmware info; a mouse reports device type 0xC1 at 0x14 (input report) or 0x15 (feature-report fallback)
    dev.write([0x00, 0x0e, 0x01]);
    let r = dev.read(300), type = r ? r[0x14] : 0;
    if (type !== 0xc1) {
      dev.sendFeature([0x00, 0x0e, 0x01]);
      r = dev.getFeature(0);
      type = r ? r[0x15] : 0;
    }
    if (type !== 0xc1) { dev.log('not a Corsair mouse (device type 0x' + type.toString(16) + ')'); return false; }
    dev.write([0x00, 0x07, 0x04, 0x02]);               // special function: software lighting control
    dev.write([0x00, 0x07, 0x05, 0x02, 0x00, 0x01]);   // lighting control (1 = mouse)
    this.slots = MODELS[dev.productId] || GENERIC;
    this.last = null;
    return { zones: this.slots.map((s, i) => ({ id: 'z' + s[0], name: s[1], leds: 1, kind: 'single', category: 'mouse' })) };
  },

  render(dev, rgb) {
    const key = Array.from(rgb.subarray(0, this.slots.length * 3)).join(',');
    if (key === this.last) return;
    this.last = key;
    const b = new Uint8Array(65);
    b[1] = 0x07; b[2] = 0x22; b[3] = 15; b[4] = 0x00;
    for (let z = 0; z < 15; z++) b[5 + z * 4] = z;     // 15 zones, <zone> R G B each; unused ones stay black
    this.slots.forEach((s, i) => { const o = 5 + s[0] * 4; b[o + 1] = rgb[i * 3]; b[o + 2] = rgb[i * 3 + 1]; b[o + 3] = rgb[i * 3 + 2]; });
    dev.write(b);
  },

  shutdown(dev) { try { dev.write([0x00, 0x07, 0x04, 0x01]); } catch (e) { } },   // back to hardware lighting

  tests: [
    {
      name: 'Sabre RGB: firmware info says mouse (0xC1), software mode, one 15-zone colour packet',
      device: { productId: 0x1b2f, reportSize: 65, interface: 1 },
      replies: [{ when: '000E01', reply: '00'.repeat(0x14) + 'C1' }],
      colors: ['#102030'],
      expectZones: 4,
      expectInit: ['000E01', '00070402', '000705020001'],
      expectFrame: ['0007220F00' + '00000000' + '01102030' + '02102030' + '03102030' + '04102030' + '05000000'],
    },
    {
      name: 'Harpoon PRO: one logo zone in slot 3',
      device: { productId: 0x1b75, reportSize: 65, interface: 1 },
      replies: [{ when: '000E01', reply: '00'.repeat(0x14) + 'C1' }],
      colors: ['#ff0000'],
      expectZones: 1,
      expectFrame: ['0007220F00' + '00000000' + '01000000' + '02000000' + '03ff0000' + '04000000'],
    },
    {
      name: 'declines a keyboard (device type 0xC0)',
      device: { productId: 0x1b2f, reportSize: 65, interface: 1 },
      replies: [{ when: '000E01', reply: '00'.repeat(0x14) + 'C0' }],
      expectDeclined: true,
    },
    { name: 'declines a collection of the wrong size', device: { productId: 0x1b2f, reportSize: 32 }, expectDeclined: true },
  ],
};
