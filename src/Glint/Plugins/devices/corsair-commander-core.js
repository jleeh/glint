// Corsair iCUE Commander Core / Core XT / Commander ST (AIO hub): fans, pump, temperature probes, RGB.
// Protocol per OpenLinkHub (cc.go / ccxt.go). Verified on real hardware.
//
// The hub runs in "software mode" while Glint talks to it. If Glint stops (exit or crash) it falls back to
// its own firmware fan curves + lighting, which is the fail-safe.
// Measured quirk: the Core XT freezes fan duty / RPM reads if colour frames arrive < ~70 ms apart.

const PUMP_MODELS = [0x0c1c, 0x0c32, 0x0c1d, 0x0c3c, 0x0c3d, 0x0c3e];
const XT = 0x0c2a;

const CMD = {
  open: [0x0d, 0x01], openColor: [0x0d, 0x00], close: [0x05, 0x01, 0x01],
  firmware: [0x02, 0x13], softwareMode: [0x01, 0x03, 0x00, 0x02], hardwareMode: [0x01, 0x03, 0x00, 0x01],
  write: [0x06, 0x01], writeColor: [0x06, 0x00], writeColorNext: [0x07, 0x00], read: [0x08, 0x01],
};
const EP = { leds: 0x20, speeds: 0x17, setSpeed: 0x18, temps: 0x21, fans: 0x1a, color: 0x22, ledPorts: 0x1e };

// LED device type code per LED count (what kind of fan / pump head is on the port)
function ledCommand(n) {
  return ({ 4: 0x03, 8: 0x05, 10: 0x01, 12: 0x04, 16: 0x02, 21: 0x08, 24: 0x08, 29: 0x08, 34: 0x06 })[n] ?? 0x01;
}

let pump = true, size = 65, buf = null, ledCounts = [], fanStatus = [], chIndex = {};

// Send 0x08 + command + payload; return the reply (report id stripped) whose echo matches the command.
function transfer(dev, cmd, payload) {
  buf.fill(0);
  buf[1] = 0x08;
  buf.set(cmd, 2);
  if (payload) buf.set(payload, 2 + cmd.length);
  dev.write(buf);
  for (let tries = 0; tries < 8; tries++) {
    const r = dev.read(500);
    if (!r) break;
    if (r.length > 2 && r[1] === 0x00 && r[2] === cmd[0]) return r.subarray(1);
  }
  throw new Error('no matching response to 0x' + cmd[0].toString(16));
}

function readEndpoint(dev, ep) {
  transfer(dev, CMD.close, [ep]);
  transfer(dev, CMD.open, [ep]);
  const r = transfer(dev, CMD.read, [ep]).slice();
  transfer(dev, CMD.close, [ep]);
  return r;
}

function writeEndpoint(dev, ep, dataType, data, extra) {
  const dt = dataType ? dataType.length : 0;
  const b = new Uint8Array(4 + dt + data.length);
  const len = extra ? data.length + 2 : data.length;
  b[0] = len & 0xff; b[1] = len >> 8;
  if (dataType) b.set(dataType, 4);
  b.set(data, 4 + dt);
  transfer(dev, CMD.close, [ep]);
  transfer(dev, CMD.open, [ep]);
  const r = transfer(dev, CMD.write, b);
  transfer(dev, CMD.close, [ep]);
  return r;
}

const fanId = ch => pump ? (ch === 0 ? 'pump' : 'fan' + ch) : 'fan' + (ch + 1);

module.exports = {
  type: 'device',
  name: 'Corsair Commander Core',
  vendorId: 0x1b1c,
  productIds: [...PUMP_MODELS, XT],
  reportSize: 64,                                     // vendor interface only (64/96/384-byte reports)
  id: dev => dev.productId === 0x0c32 ? 'st' : dev.productId === XT ? 'xt' : 'cc',
  minGapMs: dev => dev.productId === XT ? 75 : 8,

  init(dev) {
    pump = dev.productId !== XT;
    size = dev.reportSize;
    buf = new Uint8Array(size);

    const fw = transfer(dev, CMD.firmware);
    dev.log(`firmware ${fw[3]}.${fw[4]}.${fw[5]}`);
    transfer(dev, CMD.softwareMode);
    for (let i = pump ? 0 : 1; i <= 6; i++) transfer(dev, [0x14, i, 0x01]);   // init LED ports (XT: 1-6, per ccxt.go initLedPorts)
    dev.sleep(500);

    // fan channels: status 0x07 = spinning, 0x02 = connected (0 rpm)
    const f = readEndpoint(dev, EP.fans);
    fanStatus = Array.from(f.subarray(6, 6 + f[5]));
    // LED ports: 4 bytes each (status 2 = connected, then u16 LED count). XT has an external port first.
    const l = readEndpoint(dev, EP.leds);
    const start = pump ? 6 : 10, ports = pump ? 7 : 6;
    ledCounts = [];
    for (let i = 0; i < ports && start + i * 4 + 3 < l.length; i++) {
      const o = start + i * 4;
      ledCounts.push(l[o] === 2 ? l[o + 2] | (l[o + 3] << 8) : 0);
    }

    // colour setup: per-port LED device types, then hold the colour endpoint open on handle 0.
    // The XT's list starts with its external RGB port (0x00 = nothing configured there), then the 6 fan
    // ports, and the LED power must be reset afterwards (ccxt.go resetLEDPorts). Without the leading byte
    // every fan port's type shifts by one slot and the XT shows none of Glint's colours.
    const ports2 = [0x0d, 0x00, 0x07];
    if (!pump) ports2.push(0x00);
    for (const n of ledCounts) { if (n > 0) ports2.push(0x01, ledCommand(n)); else ports2.push(0x00); }
    if (pump) while (ports2.length < 3 + 7 * 2) ports2.push(0x00);
    writeEndpoint(dev, EP.ledPorts, null, Uint8Array.from(ports2), false);
    if (!pump) transfer(dev, [0x15, 0x01]);             // reset LED power so the new port types apply
    transfer(dev, CMD.close, [EP.color]);
    transfer(dev, CMD.openColor, [EP.color]);

    const zones = [];
    ledCounts.forEach((n, i) => {
      if (!n) return;
      const isPump = pump && i === 0, num = pump ? i : i + 1;
      zones.push({ id: isPump ? 'pump' : 'fan' + num, name: isPump ? 'Pump' : 'Fan ' + num, leds: n, kind: 'ring' });
    });
    const fans = [];
    chIndex = {};
    fanStatus.forEach((st, ch) => {
      if (st !== 0x07 && st !== 0x02) return;
      const id = fanId(ch);
      chIndex[id] = ch;
      fans.push({ id, name: id, pump: pump && ch === 0 });
    });
    dev.log(`fans ${fanStatus.map(x => x.toString(16)).join(',')} leds ${ledCounts.join(',')}`);
    return { zones, fans };
  },

  // rgb: all connected ports' LEDs, contiguous
  render(dev, rgb) {
    const b = new Uint8Array(6 + rgb.length);
    const len = rgb.length + 2;
    b[0] = len & 0xff; b[1] = len >> 8; b[4] = 0x12; b[5] = 0x00;
    b.set(rgb, 6);
    const chunk = size - 1 - 3;                         // room after 0x08 + 2 command bytes
    for (let off = 0, i = 0; off < b.length; off += chunk, i++)
      transfer(dev, i === 0 ? CMD.writeColor : CMD.writeColorNext, b.subarray(off, Math.min(off + chunk, b.length)));
  },

  poll(dev) {
    const temps = {}, rpm = {};
    const t = readEndpoint(dev, EP.temps);
    for (let i = 0; i < t[5] && 6 + i * 3 + 2 < t.length; i++) {
      const o = 6 + i * 3;
      if (t[o] !== 0) continue;                         // probe not connected
      let v = t[o + 1] | (t[o + 2] << 8); if (v > 32767) v -= 65536;
      v /= 10;
      if (v < 5 || v > 100) continue;
      if (pump && i === 0) temps.liquid = v; else temps['probe' + (pump ? i : i + 1)] = v;
    }
    const s = readEndpoint(dev, EP.speeds);
    for (let ch = 0; ch < s[5] && 6 + ch * 2 + 1 < s.length; ch++) rpm[fanId(ch)] = s[6 + ch * 2] | (s[7 + ch * 2] << 8);
    return { temps, rpm };
  },

  setDuty(dev, duty) {
    const chans = Object.keys(duty).filter(k => k in chIndex).map(k => [chIndex[k], duty[k]]).sort((a, b) => a[0] - b[0]);
    if (!chans.length) return;
    const b = new Uint8Array(1 + chans.length * 4);
    b[0] = chans.length;
    chans.forEach(([ch, d], i) => { b[1 + i * 4] = ch; b[2 + i * 4] = 0; b[3 + i * 4] = Math.max(0, Math.min(100, d)); });
    writeEndpoint(dev, EP.setSpeed, [0x07, 0x00], b, true);
  },

  // back to hardware mode: firmware fan curves + lighting take over
  shutdown(dev) { try { transfer(dev, CMD.hardwareMode); } catch (e) { } },

  // Simulated-device tests: run with `Glint.exe plugincheck` (also part of the selftest).
  tests: [
    {
      name: 'Commander ST: pump + 4 fans, colour frame in 61-byte chunks',
      device: { productId: 0x0c32, reportSize: 65 },
      replies: [
        { when: '00080213', reply: '000002000200 13' },
        { when: '000808011A', reply: '0000080000000707070707070101' },
        { when: '0008080120', reply: '000008000000 07 0200 1D00 0200 0800 0200 0800 0200 0800 0200 0800 0300 0000 0300 0000' },
        { when: '000805', reply: '000005' }, { when: '00080D', reply: '00000D' }, { when: '000806', reply: '000006' },
        { when: '000807', reply: '000007' }, { when: '000801', reply: '000001' }, { when: '000814', reply: '000014' },
        { when: '000808', reply: '000008' },
      ],
      expectZones: 61,
      expectInit: ['000801030002', '0008140001', '0008140601'],
      expectFrame: ['00080600B90000001200000102', '00080700'],
    },
    {
      name: 'Core XT: external-port byte leads the LED port list, then LED power reset (ccxt.go resetLEDPorts)',
      device: { productId: 0x0c2a, reportSize: 385 },
      replies: [
        { when: '00080213', reply: '000002000200 13' },
        { when: '000808011A', reply: '00000800000006070707070707' },
        { when: '0008080120', reply: '000008000000 07 00000000 0200 0800 0200 0800 0200 0800 0200 0800 0200 0800 0200 0800' },
        { when: '000805', reply: '000005' }, { when: '00080D', reply: '00000D' }, { when: '000806', reply: '000006' },
        { when: '000807', reply: '000007' }, { when: '000801', reply: '000001' }, { when: '000814', reply: '000014' },
        { when: '000815', reply: '000015' }, { when: '000808', reply: '000008' },
      ],
      expectZones: 48,
      expectInit: ['000801030002', '0008140101', '0008140601', '0008060110000000 0D0007 00 0105 0105 0105 0105 0105 0105', '00081501'],
      expectFrame: ['00080600920000001200000102'],
    },
  ],
};
