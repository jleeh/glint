// Corsair iCUE LINK System Hub (1b1c:0c3f): LINK fans / pumps (duty + RPM), coolant / probe temperatures, and the RGB of
// every LINK device in the chain. The chain is discovered at startup (device list endpoint 0x36); replug = restart Glint.
// Protocol per OpenLinkHub src/devices/lsh/lsh.go (getDevices, getLedDevices, getDeviceData, setSpeed, writeColor,
// setDeviceProtection, transfer) and database/external/lsh.json (device table). Same endpoint protocol as the Commander
// Core (corsair-commander-core.js) but 512-byte reports with an extra 0x01 header byte: [0, 0x00, 0x01, cmd..., payload].
//
// Software mode + keepalive: the hub runs in software mode while Glint talks to it; Glint re-sends a frame every <= 4 s and
// polls once a second. shutdown() puts the hub back in hardware mode (its own fan curves and lighting).
// Safety: no firmware / save commands. Port power protection (OpenLinkHub setDeviceProtection) scales brightness when more
// than 238 / 340 / 442 LEDs hang on one hub port. Not supported (skipped): iCUE LINK adapters / external LED strips, PSUs, LCD images.

// [deviceType, model, name, leds, flags]  p=pump s=speed a=AIO t=temp probe/sensor d=adapter c=commander duo u=PSU r=VRM cooler v=v2
const TABLE = [
  [1, 0, 'QX RGB', 34, 'st'], [2, 0, 'LX RGB', 18, 's'], [3, 0, 'RX RGB MAX', 8, 's'], [19, 0, 'RX', 0, 's'],
  [15, 0, 'RX RGB', 8, 's'], [4, 0, 'RX MAX', 0, 's'],
  [7, 2, 'H150i', 20, 'psa'], [7, 5, 'H150i', 20, 'psa'], [7, 1, 'H115i', 20, 'psa'], [7, 3, 'H170i', 20, 'psa'],
  [7, 0, 'H100i', 20, 'psa'], [7, 4, 'H100i', 20, 'psa'],
  [9, 0, 'XC7 ELITE', 24, 't'], [9, 1, 'XC7 ELITE', 24, 't'], [10, 0, 'XG3 HYBRID', 22, 's'], [13, 0, 'XG7 RGB', 16, ''],
  [12, 0, 'XD5 ELITE', 22, 'pst'], [25, 0, 'XD6 ELITE', 22, 'pst'], [16, 0, 'VRM Cooler', 0, 'sr'],
  [17, 0, 'TITAN 240', 20, 'psa'], [17, 4, 'TITAN 240', 20, 'psa'], [17, 2, 'TITAN 360', 20, 'psa'],
  [17, 5, 'TITAN 360', 20, 'psa'], [17, 1, 'TITAN 280', 20, 'psa'], [17, 3, 'TITAN 420', 20, 'psa'],
  [5, 0, 'ADAPTER', 0, 'd'], [5, 1, 'ADAPTER', 0, 'd'], [5, 2, 'ADAPTER', 0, 'd'],
  [27, 0, 'COMMANDER DUO', 0, 'stc'],
  [11, 0, 'HX1000i', 0, 'stu'], [11, 1, 'HX1200i', 0, 'stu'], [11, 2, 'HX1500i', 0, 'stu'],
  [26, 8, 'LCD SCREEN MODULE', 32, 'sv'], [26, 1, 'GENERIC AIO PUMP', 20, 'psav'], [26, 0, 'FANS', 8, 'sv'],
];

const CMD = {
  close: [0x05, 0x01, 0x01], open: [0x0d, 0x01], openColor: [0x0d, 0x00], read: [0x08, 0x01],
  write: [0x06, 0x01], writeColor: [0x06, 0x00], writeColorNext: [0x07, 0x00],
  firmware: [0x02, 0x13], softwareMode: [0x01, 0x03, 0x00, 0x02], hardwareMode: [0x01, 0x03, 0x00, 0x01],
};
const EP = { devices: 0x36, leds: 0x20, speeds: 0x17, setSpeed: 0x18, temps: 0x21, color: 0x22 };
const TYPE = { devices: [0x21, 0x00], speeds: [0x25, 0x00], temps: [0x10, 0x00], setSpeed: [0x07, 0x00], color: [0x12, 0x00] };
const CHUNK = 508;                                   // maxBufferSizePerRequest
const DEFAULT_DUTY = 70;

let size = 513, buf = null, chain = [], duty = {}, scale = 1, lcd = false, ledOrder = [];

// one request/response; reply = raw input report (r[0] = report id, r[1] = 0, r[2] = command echo)
function transfer(dev, cmd, payload) {
  for (let i = 0; i < 32; i++) if (!dev.read(1)) break;           // drop stale replies
  buf.fill(0);
  buf[2] = 0x01;
  buf.set(cmd, 3);
  if (payload) buf.set(payload, 3 + cmd.length);
  dev.write(buf);
  for (let tries = 0; tries < 8; tries++) {
    const r = dev.read(500);
    if (!r) break;
    if (r.length > 3 && r[1] === 0x00 && r[2] === cmd[0]) return r;
  }
  throw new Error('no matching response to 0x' + cmd[0].toString(16));
}

// close / open / read / close an endpoint; a reply whose data type echoes the request continues in a second read
function readEndpoint(dev, ep, type) {
  transfer(dev, CMD.close, [ep]);
  transfer(dev, CMD.open, [ep]);
  let r = Array.from(transfer(dev, CMD.read, [ep]));
  if (type && r[4] === type[0] && r[5] === type[1]) {
    try { r = r.concat(Array.from(transfer(dev, CMD.read, [ep]).subarray(4))); } catch (e) { }
  }
  transfer(dev, CMD.close, [ep]);
  return r;
}

function writeEndpoint(dev, ep, type, data) {
  const b = new Uint8Array(4 + type.length + data.length);
  const len = data.length + 2;
  b[0] = len & 0xff; b[1] = len >> 8;
  b.set(type, 4); b.set(data, 4 + type.length);
  transfer(dev, CMD.close, [ep]);
  transfer(dev, CMD.open, [ep]);
  const r = transfer(dev, CMD.write, b);
  transfer(dev, CMD.close, [ep]);
  return r;
}

const meta = (t, m) => TABLE.find(x => x[0] === t && x[1] === m);
const le16 = (r, o) => r[o] | (r[o + 1] << 8);

function sendSpeeds(dev) {
  const chans = chain.filter(d => d.fan).map(d => d.ch).sort((a, b) => a - b);
  if (!chans.length) return;
  const p = new Uint8Array(1 + chans.length * 4);
  p[0] = chans.length;
  chans.forEach((ch, i) => { p[1 + i * 4] = ch; p[2 + i * 4] = 0; p[3 + i * 4] = duty[ch]; });
  for (let n = 0; n < 5; n++) {                                  // the hub NAKs while busy: repeat (OpenLinkHub setSpeed)
    const r = writeEndpoint(dev, EP.setSpeed, TYPE.setSpeed, p);
    if (r[3] === 0x00) break;
    dev.sleep(100);
  }
}

module.exports = {
  type: 'device',
  id: 'lsh',
  name: 'Corsair iCUE LINK System Hub',
  vendorId: 0x1b1c,
  productIds: [0x0c3f],
  reportSize: 512,                                    // vendor interface only (512-byte reports)
  minGapMs: 25,

  init(dev) {
    size = dev.reportSize;
    buf = new Uint8Array(size);

    // identify: firmware query (major.minor.build LE16) must be answered by the hub
    let fw;
    try { fw = transfer(dev, CMD.firmware); } catch (e) { return false; }
    if (!fw || fw[4] > 20) return false;
    dev.log(`firmware ${fw[4]}.${fw[5]}.${le16(fw, 6)}`);
    const fwMajor = fw[4];
    transfer(dev, CMD.softwareMode);
    dev.sleep(500);

    // device list: channels 1..N, each an 8-byte header (type, model at +2/+3, id length at +7) + id string
    const r = readEndpoint(dev, EP.devices, TYPE.devices);
    const count = r[6], data = r.slice(7);
    chain = []; lcd = false;
    let pos = 0;
    for (let ch = 1; ch <= count && pos + 8 <= data.length; ch++) {
      const idLen = data[pos + 7];
      if (idLen === 0) { pos += 8; continue; }
      const type = data[pos + 2], model = data[pos + 3];
      if (type === 6 || type === 14) lcd = true;                 // LINK pump-cap LCD / XD5 Elite LCD
      const m = meta(type, model);
      pos += 8 + idLen;
      if (!m) { dev.log(`unknown LINK device type ${type}/${model} on channel ${ch}`); continue; }
      const f = m[4];
      if (f.includes('u') || f.includes('d')) continue;          // PSU / external-LED adapter: not supported
      chain.push({
        ch, name: m[2], leds: m[3], pump: f.includes('p'), aio: f.includes('a'), fan: f.includes('s'),
        temp: f.includes('t') || f.includes('p'), duo: f.includes('c'), port: (ch >= 13 || (fwMajor < 2 && ch >= 7)) ? 1 : 0,
      });
    }

    // Commander DUO: the LED count is whatever is plugged in (endpoint 0x20: 4 bytes per channel, status 2 = connected)
    if (chain.some(d => d.duo)) {
      const l = readEndpoint(dev, EP.leds), n = l[6], ld = l.slice(7);
      for (let i = 1; i <= n && i * 4 + 3 < ld.length; i++) {
        const d = chain.find(x => x.ch === i);
        if (d && d.duo && le16(ld, i * 4) === 2) d.leds = Math.min(50, le16(ld, i * 4 + 2));
      }
    }
    // pump cap LCD: AIOs gain 24 cap LEDs (the inner LEDs 16..19 of the AIO are kept dark, like OpenLinkHub)
    if (lcd) chain.forEach(d => { if (d.aio) { d.leds += 24; d.lcd = true; } });

    // port power protection (OpenLinkHub setDeviceProtection)
    const ports = [0, 0];
    chain.forEach(d => { ports[d.port] += d.leds; });
    scale = 1;
    for (const p of ports) { if (p > 238) scale = 0.66; if (p > 340) scale = 0.33; if (p > 442) scale = 0.1; }
    if (scale !== 1) dev.log(`port protection: brightness x${scale}`);

    // fan / pump default duty (OpenLinkHub setDefaults), then colour endpoint held open
    duty = {};
    chain.forEach(d => { if (d.fan) duty[d.ch] = DEFAULT_DUTY; });
    sendSpeeds(dev);
    transfer(dev, CMD.close, [EP.color]);
    transfer(dev, CMD.openColor, [EP.color]);

    const zones = [], fans = [];
    ledOrder = [];
    chain.forEach(d => {
      const n = d.leds > 0 ? dev.ledCount('ch' + d.ch, d.leds) : 0;
      d.leds = n;
      if (n > 0) {
        zones.push({ id: 'ch' + d.ch, name: `${d.name} (${d.ch})`, leds: n, kind: d.duo ? 'strip' : 'ring', category: d.pump ? 'pump' : d.duo ? 'strip' : 'fan' });
        ledOrder.push(d);
      }
      if (d.fan) fans.push({ id: 'ch' + d.ch, name: `${d.name} (${d.ch})`, pump: d.pump });
    });
    dev.log(`chain: ${chain.map(d => d.ch + ':' + d.name).join(', ')}`);
    return { zones, fans };
  },

  // all LEDs in channel order, as one colour packet (chunked at 508 bytes): [len LE16, 0, 0, 0x12, 0x00, rgb...]
  render(dev, rgb) {
    const total = rgb.length;
    const b = new Uint8Array(6 + total);
    const len = total + 2;
    b[0] = len & 0xff; b[1] = len >> 8; b[4] = TYPE.color[0]; b[5] = TYPE.color[1];
    let o = 0;
    for (const d of ledOrder) {
      for (let i = 0; i < d.leds; i++, o += 3) {
        const dark = d.lcd && i > 15 && i < 20;
        for (let c = 0; c < 3; c++) b[6 + o + c] = dark ? 0 : Math.floor(rgb[o + c] * scale);
      }
    }
    for (let off = 0, i = 0; off < b.length; off += CHUNK, i++)
      transfer(dev, i === 0 ? CMD.writeColor : CMD.writeColorNext, b.subarray(off, Math.min(off + CHUNK, b.length)));
  },

  // entry i of the speed / temperature endpoints belongs to channel i (entry 0 is the hub itself)
  poll(dev) {
    const temps = {}, rpm = {};
    const s = readEndpoint(dev, EP.speeds, TYPE.speeds);
    if (s[7] === 0x01) {
      for (let i = 0; i < s[6]; i++) {
        const o = 7 + i * 3, d = chain.find(x => x.ch === i);
        if (!d || !d.fan || s[o] !== 0x00) continue;
        const v = le16(s, o + 1);
        rpm['ch' + i] = v > 1 ? v : 0;
      }
    }
    const t = readEndpoint(dev, EP.temps, TYPE.temps);
    if (t[3] === 0x00 && (t[7] === 0x00 || t[7] === 0x01)) {
      let liquidSet = false;
      for (let i = 0; i < t[6]; i++) {
        const o = 7 + i * 3, d = chain.find(x => x.ch === i);
        if (!d || !d.temp || t[o] !== 0x00) continue;
        let v = le16(t, o + 1); if (v > 32767) v -= 65536;
        v /= 10;
        if (v <= 1 || v > 120) continue;
        if (d.pump && !liquidSet) { temps.liquid = v; liquidSet = true; } else temps['probe' + i] = v;
      }
    }
    return { temps, rpm };
  },

  setDuty(dev, req) {
    let changed = false;
    for (const d of chain) {
      const v = req['ch' + d.ch];
      if (v === undefined || !d.fan) continue;
      const min = d.pump ? (d.aio ? 50 : 20) : 0;                // pumps must never stall
      const nv = Math.max(min, Math.min(100, Math.round(v)));
      if (duty[d.ch] !== nv) { duty[d.ch] = nv; changed = true; }
    }
    if (changed) sendSpeeds(dev);
  },

  // hand the hub back to its own firmware (fan curves + lighting)
  shutdown(dev) { try { transfer(dev, CMD.hardwareMode); } catch (e) { } },

  // Simulated-device tests (Glint.exe plugincheck). Packet layouts derived from OpenLinkHub lsh.go
  // (transfer / read / write / writeColor / getDevices / setSpeed); LED counts from database/external/lsh.json.
  tests: (function () {
    const H = s => s.replace(/ /g, '');
    // device list reply: 00 00 08 00 | 21 00 | count | entries {00 00 type model 00 00 00 idLen, id}
    const entry = (type, model, id) => '00 00 ' + type.toString(16).padStart(2, '0') + ' ' + model.toString(16).padStart(2, '0') + ' 00 00 00 03 ' + id;
    const replies = list => [
      { when: '0000010213', reply: '00 00 02 00 02 04 B6 01' },                                   // fw 2.4.438
      { when: '000001010300', reply: '00 00 01 00' },                                            // software mode
      { when: '000001080136', reply: '00 00 08 00 21 00 ' + list.length.toString(16).padStart(2, '0') + ' ' + list.join(' ') },
      { when: '000001080120', reply: '00 00 08 00 00 00 00' },
      { when: '00000108', reply: '00 00 08 00' },
      { when: '00000105', reply: '00 00 05 00' }, { when: '0000010d', reply: '00 00 0d 00' },
      { when: '00000106', reply: '00 00 06 00' }, { when: '00000107', reply: '00 00 07 00' },
    ];
    const dev = { productId: 0x0c3f, reportSize: 513, inputSize: 513 };
    const ramp = n => { let s = ''; for (let i = 0; i < n; i++) s += [i * 3, i * 3 + 1, i * 3 + 2].map(v => (v & 255).toString(16).padStart(2, '0')).join(''); return s; };
    const hdr = leds => '00 00 01 06 00 ' + ((leds * 3 + 2) & 255).toString(16).padStart(2, '0') + ' ' + ((leds * 3 + 2) >> 8).toString(16).padStart(2, '0') + ' 00 00 12 00 ';
    const qx8 = []; for (let i = 0; i < 8; i++) qx8.push(entry(1, 0, '41 41 41'));
    return [
      {
        name: 'QX fan + H150i pump: 54 LEDs, software mode, default duty, colour endpoint, one colour packet',
        device: dev,
        replies: replies([entry(1, 0, '41 41 41'), entry(7, 2, '42 42 42')]),
        expectZones: 54,
        // fw query, software mode, close+open device list, speed write (count 2: ch1 duty 70=0x46, ch2 70), colour endpoint open
        expectInit: ['0000010213', '000001010300', '00000105010136', '0000010d0136',
          H('00 00 01 06 01 0B 00 00 00 07 00 02 01 00 46 00 02 00 46 00'), '0000010d0022'],
        expectFrame: [H(hdr(54) + ramp(54).slice(0, 24))],
      },
      {
        name: 'H150i with a pump-cap LCD: 44 LEDs, inner LEDs 16-19 dark',
        device: dev,
        replies: replies([entry(6, 0, '4C 43 44'), entry(7, 2, '42 42 42')]),
        expectZones: 44,
        expectFrame: [H(hdr(44) + ramp(16) + '000000000000000000000000')],
      },
      {
        name: '8 QX fans on one port: brightness reduced to 66 %, colour split in 508-byte chunks',
        device: dev,
        replies: replies(qx8),
        colors: ['#ffffff'],
        expectZones: 272,
        expectFrame: [H(hdr(272) + 'A8A8A8A8A8A8'), '0000010700'],
      },
      { name: 'declined when the firmware query gets no answer', device: dev, expectDeclined: true },
    ];
  })(),
};
