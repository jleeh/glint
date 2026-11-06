// ASUS Aura USB mainboard controller (AULA3 etc.): onboard LEDs + addressable (ARGB) headers, direct mode.
// Protocol per OpenRGB (Controllers/AsusAuraUSBController). Verified on real hardware.
// ARGB header lengths can't be read from the board: set them on the Devices page (default 60, 0 = unused).
module.exports = {
  type: 'device',
  id: 'aura',
  name: 'ASUS Aura (motherboard)',
  vendorId: 0x0b05,
  productIds: [0x1867, 0x1872, 0x18a3, 0x18a5, 0x19af, 0x1aa6, 0x1bed],
  reportSize: 65,

  init(dev) {
    let r = request(dev, 0x82);
    if (!r || r[1] !== 0x02) return false;
    const fw = String.fromCharCode(...r.subarray(2, 18)).replace(/\0+$/, '');
    r = request(dev, 0xb0);
    if (!r || r[1] !== 0x30) return false;              // no config table: not a mainboard controller
    const table = r.subarray(4, 64);
    const boardLeds = table[0x1b], argbHeaders = table[0x02];
    const fromZero = fw.startsWith('AULA3-AR32');
    let effect = 0;
    this.ch = [];
    if (boardLeds > 0) {
      this.ch.push({ direct: 0x04, effect: fromZero ? argbHeaders : effect, zone: { id: 'board', name: 'Onboard LEDs', leds: boardLeds, kind: 'single' } });
      if (!fromZero) effect++;
    }
    for (let i = 0; i < argbHeaders; i++) {
      const n = dev.ledCount('argb' + (i + 1), 60);
      if (n > 0) this.ch.push({ direct: i, effect, zone: { id: 'argb' + (i + 1), name: 'ARGB header ' + (i + 1), leds: n, kind: 'strip' } });
      effect++;
    }
    raw(dev, [0x52, 0x53, 0x00, 0x01]);                 // gen1
    for (const c of this.ch) raw(dev, [0x35, c.effect, 0x00, 0x00, 0xff]); // direct mode per channel
    dev.log(`${fw}: ${this.ch.map(c => c.zone.id + '=' + c.zone.leds).join(', ')}`);
    return { zones: this.ch.map(c => c.zone) };
  },

  render(dev, rgb) {
    let off = 0;
    const b = new Uint8Array(65);
    for (const c of this.ch) {
      const total = c.zone.leds;
      for (let done = 0; done < total;) {
        const n = Math.min(20, total - done);
        b.fill(0);
        b[0] = 0xec; b[1] = 0x40;
        let cmd = c.direct;
        if (done + n >= total) cmd |= 0x80;             // apply after the last packet
        if (done > 255) cmd |= 0x10;
        b[2] = cmd; b[3] = done & 0xff; b[4] = n;
        b.set(rgb.subarray((off + done) * 3, (off + done + n) * 3), 5);
        dev.write(b);
        done += n;
      }
      off += total;
    }
  },

  // Simulated-device tests: run with `Glint.exe plugincheck` (also part of the selftest).
  tests: [
    {
      name: 'AULA3-AR32: board + 3 ARGB headers, direct mode',
      device: { reportSize: 65 },
      replies: [{ when: 'EC82', reply: 'EC0241554C41332D415233322D30333039' }, { when: 'ECB0', reply: 'EC300000000003000000000000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000' }],
      expectZones: 182,
      expectInit: ['EC52530001', 'EC35030000FF', 'EC35000000FF'],
      expectFrame: ['EC40840002000102030405', 'EC40000014060708'],
    },
  ],
};

function raw(dev, payload) {
  const b = new Uint8Array(65);
  b[0] = 0xec;
  b.set(payload, 1);
  dev.write(b);
}

function request(dev, cmd) {
  raw(dev, [cmd]);
  for (let i = 0; i < 4; i++) {
    const r = dev.read(300);
    if (!r) return null;
    if (r[0] === 0xec) return r;
  }
  return null;
}
