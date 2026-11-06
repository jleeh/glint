// Corsair Lighting Node Pro / Core: 2 ARGB channels (Core: 1 used). Direct mode, planar R/G/B packets.
// Protocol per OpenRGB (Controllers/CorsairLightingNodeController). Verified on real hardware.
// LED counts can't be read from the device: set them on the Devices page (defaults: 120 per channel).
module.exports = {
  type: 'device',
  id: 'lnp',
  name: 'Corsair Lighting Node',
  vendorId: 0x1b1c,
  productIds: [0x0c0b, 0x0c1a],
  reportSize: 64,

  init(dev) {
    // firmware query (0x02) — also proves we're talking to a Lighting Node
    const r = send(dev, 0x02, [], true);
    if (!r) return false;
    dev.log(`firmware ${r[2]}.${r[3]}.${r[4]}`);
    const zones = [];
    for (let ch = 1; ch <= 2; ch++) {
      const n = dev.ledCount('ch' + ch, 120);
      if (n > 0) zones.push({ id: 'ch' + ch, name: 'Channel ' + ch, leds: n, kind: 'strip' });
    }
    this.zones = zones;
    return { zones };
  },

  render(dev, rgb) {
    let off = 0;
    for (const z of this.zones) {
      const ch = +z.id.slice(2) - 1;
      send(dev, 0x38, [ch, 0x02]);                    // port state: software
      for (let start = 0; start < z.leds; start += 50) {
        const n = Math.min(50, z.leds - start);
        for (let plane = 0; plane < 3; plane++) {
          const p = [ch, start, n, plane];
          for (let i = 0; i < n; i++) p.push(rgb[(off + start + i) * 3 + plane]);
          send(dev, 0x32, p);                          // direct colour data
        }
      }
      off += z.leds;
    }
    send(dev, 0x33, [0xff]);                          // commit
  },

  // Simulated-device tests: run with `Glint.exe plugincheck` (also part of the selftest).
  tests: [
    {
      name: 'firmware, then planar R/G/B packets per channel and a commit',
      device: { reportSize: 65, inputSize: 17 },
      replies: [{ when: '0002', reply: '0002000A04' }],
      expectZones: 240,
      expectInit: ['0002'],
      expectFrame: ['00380002', '003200003200000306090C', '0032000032010104070A'],
    },
  ],
};

// Writes are fire-and-forget (waiting for each ack made frames ~55 ms); unread acks are simply dropped.
function send(dev, id, payload, waitForReply) {
  const b = new Uint8Array(dev.reportSize);
  b[1] = id;
  b.set(payload, 2);
  dev.write(b);
  return waitForReply ? dev.read(300) : null;
}
