// Corsair Hydro Platinum / Pro XT / iCUE Elite RGB AIOs (1b1c:0c17-0c22, 0c29, 0c35-0c37, 0c40, 0c41): liquid temperature,
// pump mode, 2-3 radiator fans (duty + RPM) and the pump-head RGB (16 LEDs; Platinum models also have fan LEDs).
// Protocol per liquidctl liquidctl/driver/hydro_platinum.py (_send_command, _send_set_cooling / _generate_cooling_payload,
// get_status, set_color). 64-byte reports: [0x3f, seq<<3 | feature, command, data..., CRC-8].
//
// Pump: the hardware only has three firmware modes (quiet / balanced / extreme). Glint's pump "duty" maps to them:
// < 40 % = quiet, < 75 % = balanced, otherwise extreme. Fans: fixed duty. shutdown() puts the fans back on a firmware
// temperature curve (CUSTOM_PROFILE, liquid temperature) and the pump on balanced. Nothing is saved to flash.

const MODELS = {
  0x0c18: { name: 'H100i Platinum', fans: 2, fanLeds: 4 },
  0x0c19: { name: 'H100i Platinum SE', fans: 2, fanLeds: 16 },
  0x0c17: { name: 'H115i Platinum', fans: 2, fanLeds: 4 },
  0x0c29: { name: 'H60i Pro XT', fans: 2, fanLeds: 0 },
  0x0c20: { name: 'H100i Pro XT', fans: 2, fanLeds: 0 },
  0x0c21: { name: 'H115i Pro XT', fans: 2, fanLeds: 0 },
  0x0c22: { name: 'H150i Pro XT', fans: 3, fanLeds: 0 },
  0x0c35: { name: 'H100i Elite RGB', fans: 2, fanLeds: 0 },
  0x0c36: { name: 'H115i Elite RGB', fans: 2, fanLeds: 0 },
  0x0c37: { name: 'H150i Elite RGB', fans: 3, fanLeds: 0 },
  0x0c40: { name: 'H100i Elite RGB (White)', fans: 2, fanLeds: 0 },
  0x0c41: { name: 'H150i Elite RGB (White)', fans: 3, fanLeds: 0 },
};
const PREFIX = 0x3f;
const FEATURE_COOLING = 0, FEATURE_COOLING2 = 3, CMD_GET_STATUS = 0xff, CMD_SET_COOLING = 0x14;
const CMD_LIGHTING = [0b100, 0b101, 0b110];
const DATA_LEN = 60;                                         // report length 64 - 4
const COOLING_PREFIX = [0x00, 0xff, 0x05, 0xff, 0xff, 0xff, 0xff, 0xff];
const FAN_MODE_OFF = [0x0b - 3, 0x11 - 3], FAN_DUTY_OFF = [0x0b - 3 + 5, 0x11 - 3 + 5], FAN_PROFILE_OFF = [0x1e - 3, 0x2c - 3];
const PUMP_MODE_OFF = 0x17 - 3, PROFILE_LEN_OFF = 0x1d - 3, PROFILE_LEN = 7;
const MODE_PROFILE = 0x0, MODE_FIXED = 0x2;
const STATUS_BASE = [14, 21, 42], PUMP_BASE = 28;            // offsets of duty / rpm in the status reply (liquidctl numbering)
// firmware curve used when Glint exits: (deg C, duty %) x7, last point 60 C = 100 %
const SAFE_CURVE = [[25, 30], [30, 40], [35, 50], [40, 65], [45, 80], [50, 90], [60, 100]];

let model = null, seq = 0, fanDuty = [], pumpMode = 1, mode = [], ledCount = 16;

function crc8(b, from, to) {                                  // CRC-8, poly 0x07, init 0 (crcmod 'crc-8')
  let c = 0;
  for (let i = from; i < to; i++) {
    c ^= b[i];
    for (let k = 0; k < 8; k++) c = (c & 0x80) ? ((c << 1) ^ 0x07) & 255 : (c << 1) & 255;
  }
  return c;
}

// command + reply; reply[0] is the report id, so liquidctl's res[k] = r[k + 1]. Returns null on timeout / bad checksum.
function command(dev, feature, cmd, data) {
  const b = new Uint8Array(65);
  seq = seq % 31 + 1;
  b[1] = PREFIX;
  b[2] = seq << 3;
  let at;
  if (feature !== null) { b[2] |= feature; b[3] = cmd; at = 4; } else { b[2] |= cmd; at = 3; }
  if (data) b.set(data.slice(0, 64 - at), at);
  b[64] = crc8(b, 2, 64);
  for (let i = 0; i < 16; i++) if (!dev.read(1)) break;        // clear enqueued reports
  dev.write(b);
  const r = dev.read(500);
  if (!r || r.length < 64 || crc8(r, 2, 65) !== 0) return null;  // liquidctl: crc8(res[1:]) must be 0
  return r;
}

function coolingPayload(fanIdx, pumpByte) {
  const d = new Uint8Array(DATA_LEN);
  d.set(COOLING_PREFIX, 0);
  d[PROFILE_LEN_OFF] = PROFILE_LEN;
  fanIdx.forEach((f, k) => {
    d[FAN_MODE_OFF[k]] = mode[f];
    if (mode[f] === MODE_FIXED) d[FAN_DUTY_OFF[k]] = Math.round(Math.max(0, Math.min(100, fanDuty[f])) * 255 / 100);
    else SAFE_CURVE.forEach(([t, p], i) => { d[FAN_PROFILE_OFF[k] + i * 2] = t; d[FAN_PROFILE_OFF[k] + i * 2 + 1] = Math.round(p * 255 / 100); });
  });
  d[PUMP_MODE_OFF] = pumpByte;
  return d;
}

function sendCooling(dev) {
  let r = null;
  if (model.fans === 3) command(dev, FEATURE_COOLING2, CMD_SET_COOLING, coolingPayload([2], 0xff));
  r = command(dev, FEATURE_COOLING, CMD_SET_COOLING, coolingPayload([0, 1], pumpMode));
  return r;
}

module.exports = {
  type: 'device',
  id: 'hydro',
  name: 'Corsair Hydro Platinum / Pro XT / Elite RGB',
  vendorId: 0x1b1c,
  productIds: Object.keys(MODELS).map(Number),
  reportSize: 64,

  init(dev) {
    model = MODELS[dev.productId];
    if (!model) return false;
    seq = 0;
    // identify: a status request must come back with a valid CRC-8
    const st = command(dev, FEATURE_COOLING, CMD_GET_STATUS);
    if (!st) return false;
    // adopt the duties currently running so nothing jumps when we take over
    fanDuty = []; mode = [];
    for (let i = 0; i < model.fans; i++) { fanDuty.push(Math.round(st[STATUS_BASE[i] + 1] / 255 * 100)); mode.push(MODE_FIXED); }
    pumpMode = 1;                                              // balanced
    const fw = sendCooling(dev);
    if (fw) dev.log(`firmware ${fw[3] >> 4}.${fw[3] & 15}.${fw[4]}`);

    // enable per-LED colours (liquidctl "magic" setup packets, once per power-up)
    const hex = s => Uint8Array.from(s.match(/../g).map(x => parseInt(x, 16)));
    command(dev, null, 0b001, hex('0101ffffffffffffffffffffffffff7f7f7f7fff00ffffffff00ffffffff00ffffffff00ffffffff00ffffffff00ffffffffffffffffffffffffffffff'));
    command(dev, null, 0b010, hex('000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f2021222324252627ffffffffffffffffffffffffffffffffffffffffff'));
    command(dev, null, 0b011, hex('28292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f404142434445464748494a4b4c4d4e4fffffffffffffffffffffffffffffffffffffffffff'));

    const zones = [{ id: 'pump', name: 'Pump head', leds: 16, kind: 'ring', category: 'pump' }];
    if (model.fanLeds > 0) for (let i = 0; i < model.fans; i++) zones.push({ id: 'fan' + (i + 1), name: 'Fan ' + (i + 1), leds: model.fanLeds, kind: 'ring', category: 'fan' });
    ledCount = zones.reduce((a, z) => a + z.leds, 0);
    const fans = [{ id: 'pump', name: 'Pump', pump: true }];
    for (let i = 0; i < model.fans; i++) fans.push({ id: 'fan' + (i + 1), name: 'Fan ' + (i + 1) });
    dev.log(`${model.name}: ${ledCount} LEDs, ${model.fans} fans`);
    return { zones, fans };
  },

  // LEDs in the device's order (pump head, then fans), BGR, 20 LEDs per packet
  render(dev, rgb) {
    for (let p = 0; p * 20 < ledCount; p++) {
      const n = Math.min(20, ledCount - p * 20);
      const d = new Uint8Array(n * 3);
      for (let i = 0; i < n; i++) {
        const o = (p * 20 + i) * 3;
        d[i * 3] = rgb[o + 2]; d[i * 3 + 1] = rgb[o + 1]; d[i * 3 + 2] = rgb[o];
      }
      command(dev, null, CMD_LIGHTING[p], d);
    }
  },

  poll(dev) {
    const r = command(dev, FEATURE_COOLING, CMD_GET_STATUS);
    if (!r) return {};
    const rpm = { pump: r[PUMP_BASE + 2] | (r[PUMP_BASE + 3] << 8) };
    for (let i = 0; i < model.fans; i++) rpm['fan' + (i + 1)] = r[STATUS_BASE[i] + 2] | (r[STATUS_BASE[i] + 3] << 8);
    return { temps: { liquid: r[9] + r[8] / 255 }, rpm };
  },

  setDuty(dev, duty) {
    let changed = false;
    if (duty.pump !== undefined) {
      const m = duty.pump < 40 ? 0 : duty.pump < 75 ? 1 : 2;
      if (m !== pumpMode) { pumpMode = m; changed = true; }
    }
    for (let i = 0; i < model.fans; i++) {
      const v = duty['fan' + (i + 1)];
      if (v === undefined) continue;
      const nv = Math.max(0, Math.min(100, Math.round(v)));
      if (nv !== fanDuty[i] || mode[i] !== MODE_FIXED) { fanDuty[i] = nv; mode[i] = MODE_FIXED; changed = true; }
    }
    if (changed) sendCooling(dev);
  },

  // fans back on a firmware temperature curve, pump balanced
  shutdown(dev) {
    try {
      for (let i = 0; i < model.fans; i++) mode[i] = MODE_PROFILE;
      pumpMode = 1;
      sendCooling(dev);
    } catch (e) { }
  },

  // Simulated-device tests (Glint.exe plugincheck). Expected bytes derived from hydro_platinum.py (see the packet layout
  // comments above); sequence numbers run 1, 2, 3... and checksums are CRC-8 (poly 0x07) over bytes 2..63.
  tests: (function () {
    const crc = (b, from, to) => { let c = 0; for (let i = from; i < to; i++) { c ^= b[i]; for (let k = 0; k < 8; k++) c = (c & 0x80) ? ((c << 1) ^ 7) & 255 : (c << 1) & 255; } return c; };
    const hx = a => Array.from(a).map(v => v.toString(16).padStart(2, '0')).join('');
    // a valid device reply: res[0] = 0, res[1..] = data, last byte CRC over res[1:63]; as reported it is preceded by the report id
    const reply = (fill) => { const r = new Uint8Array(65); fill(r); r[64] = crc(r, 2, 64); return hx(r); };
    const status = (pid) => reply(r => { r[9] = 0x00; r[8] = 0; r[9] = 30; r[8] = 128;            // res[7]=128 -> 30.5 C
      r[STATUS_BASE_T[0] + 1] = 255; r[STATUS_BASE_T[1] + 1] = 128; r[STATUS_BASE_T[2] + 1] = 255; r[STATUS_BASE_T[3] + 1] = 255; r[STATUS_BASE_T[3] + 2] = 0xA0; r[STATUS_BASE_T[3] + 3] = 0x0F; });
    const STATUS_BASE_T = [14, 21, 42, 28];
    const bad = new Uint8Array(65).fill(1); const badHex = hx(bad);
    // expected set-cooling packet: seq 1 (after the status request used seq 1 -> set cooling is seq 2)
    const cooling = (seqn, feature, fanIdx, duties, pump) => {
      const b = new Uint8Array(65); b[1] = 0x3f; b[2] = (seqn << 3) | feature; b[3] = 0x14;
      const d = new Uint8Array(60); [0, 0xff, 5, 0xff, 0xff, 0xff, 0xff, 0xff].forEach((v, i) => d[i] = v); d[0x1d - 3] = 7;
      fanIdx.forEach((f, k) => { d[[8, 14][k]] = 2; d[[13, 19][k]] = Math.round(duties[f] * 255 / 100); }); d[0x17 - 3] = pump;
      b.set(d, 4); b[64] = crc(b, 2, 64); return hx(b);
    };
    const led = (seqn, cmd, bgr) => { const b = new Uint8Array(65); b[1] = 0x3f; b[2] = (seqn << 3) | cmd; b.set(bgr, 3); b[64] = crc(b, 2, 64); return hx(b); };
    // ramp frame: LED i = (3i, 3i+1, 3i+2) -> BGR on the wire
    const ramp = (from, n) => { const o = []; for (let i = from; i < from + n; i++) o.push((3 * i + 2) & 255, (3 * i + 1) & 255, (3 * i) & 255); return o; };
    return [
      {
        name: 'H100i Elite RGB: status, set cooling (fans keep their duty, pump balanced), LED enable packets, 16-LED frame',
        device: { productId: 0x0c35, reportSize: 65, inputSize: 65 },
        // every command is answered with the status-shaped reply (valid CRC)
        replies: [{ when: '003f', reply: status(0x0c35) }],
        expectZones: 16,
        // seq1 status, seq2 set cooling (fan1 100 %, fan2 50 % = 128/255), seq3-5 LED magic
        expectInit: [reply0(1), cooling(2, 0, [0, 1], [100, 50], 1), '003f19', '003f22', '003f2b'],
        expectFrame: [led(6, 0b100, ramp(0, 16))],
      },
      {
        name: 'H150i Elite RGB: third fan goes in its own cooling2 packet (pump byte 0xff)',
        device: { productId: 0x0c37, reportSize: 65, inputSize: 65 },
        replies: [{ when: '003f', reply: status(0x0c37) }],
        expectZones: 16,
        expectInit: [cooling(2, 3, [2], [100, 50, 100], 0xff)],
      },
      {
        name: 'H115i Platinum: 24 LEDs in two packets of 20 + 4',
        device: { productId: 0x0c17, reportSize: 65, inputSize: 65 },
        replies: [{ when: '003f', reply: status(0x0c17) }],
        expectZones: 24,
        expectFrame: [led(6, 0b100, ramp(0, 20)), led(7, 0b101, ramp(20, 4))],
      },
      { name: 'declined when the reply has a bad checksum', device: { productId: 0x0c35, reportSize: 65, inputSize: 65 }, replies: [{ when: '003f', reply: badHex }], expectDeclined: true },
      { name: 'declined when the device does not answer', device: { productId: 0x0c35, reportSize: 65 }, expectDeclined: true },
    ];
    function reply0(n) { const b = new Uint8Array(65); b[1] = 0x3f; b[2] = (n << 3); b[3] = 0xff; b[64] = crc(b, 2, 64); return hx(b); }
  })(),
};
