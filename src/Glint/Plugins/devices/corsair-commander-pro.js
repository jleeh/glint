// Corsair Commander Pro (1b1c:0c10) and Obsidian 1000D case hub (1b1c:1d00): 6 fans (duty + RPM), 4 temperature
// probes, 2 ARGB channels. One HID interface, 64-byte commands, 16-byte replies (reply[1] = status, 0 = ok).
// Fan / temperature protocol: liquidctl commander_pro.py (_CMD_GET_FIRMWARE 0x02, _CMD_GET_TEMP_CONFIG 0x10,
// _CMD_GET_TEMP 0x11, _CMD_GET_FAN_MODES 0x20, _CMD_GET_FAN_RPM 0x21, _CMD_SET_FAN_DUTY 0x23, _CMD_SET_FAN_PROFILE 0x25).
// LED protocol (direct mode): OpenRGB Controllers/CorsairLightingNodeController (port state 0x38, direct 0x32, commit 0x33),
// identical to the Lighting Node Pro which corsair-lighting-node.js already drives.
//
// LED counts cannot be read: set them on the Devices page (default 16 = one LL/QL-style ring per channel).
// Safety: only direct LED mode and fan duty (0x23) are used; nothing is saved to flash. shutdown() hands the fans
// back to the controller's own temperature curve (set-fan-profile 0x25 on probe 1) or, with no probe, to 70 %.

const CMD = {
  firmware: 0x02, tempConfig: 0x10, getTemp: 0x11, fanModes: 0x20, getRpm: 0x21, setDuty: 0x23, setProfile: 0x25,
  portState: 0x38, direct: 0x32, commit: 0x33,
};
const FAN_COUNT = 6, PROBES = 4;
// hardware curve used when Glint exits: (deg C, rpm) x6, same layout/limits as liquidctl set_speed_profile
const SAFE_CURVE = [[30, 800], [40, 1200], [50, 1800], [55, 2600], [60, 5000], [60, 5000]];

let fanOn = [], probeOn = [], channels = [];

function send(dev, cmd, payload) {
  const b = new Uint8Array(Math.max(dev.reportSize, 65));
  b[1] = cmd;
  if (payload) b.set(payload, 2);
  dev.write(b);
}
function drain(dev) { for (let i = 0; i < 32; i++) if (!dev.read(1)) break; }
// command + reply (stale acks from fire-and-forget colour packets are drained first, like liquidctl's clear_enqueued_reports)
function query(dev, cmd, payload) {
  drain(dev);
  send(dev, cmd, payload);
  const r = dev.read(300);
  return r && r[1] === 0 ? r : null;
}
const be16 = (r, o) => (r[o] << 8) | r[o + 1];

module.exports = {
  type: 'device',
  id: 'cpro',
  name: 'Corsair Commander Pro',
  vendorId: 0x1b1c,
  productIds: [0x0c10, 0x1d00],
  reportSize: 64,

  init(dev) {
    const fw = query(dev, CMD.firmware);
    if (!fw) return false;                                        // not a Commander Pro (or wrong collection)
    dev.log(`firmware ${fw[2]}.${fw[3]}.${fw[4]}`);
    const tc = query(dev, CMD.tempConfig);
    probeOn = []; for (let i = 0; i < PROBES; i++) probeOn.push(!!(tc && tc[2 + i]));
    const fm = query(dev, CMD.fanModes);                          // 0 = none, 1 = DC (3-pin), 2 = PWM (4-pin)
    fanOn = []; for (let i = 0; i < FAN_COUNT; i++) fanOn.push(!!(fm && (fm[2 + i] === 1 || fm[2 + i] === 2)));
    dev.log(`fans ${fanOn.map(x => +x).join('')} probes ${probeOn.map(x => +x).join('')}`);

    const zones = [];
    channels = [];
    for (let ch = 0; ch < 2; ch++) {
      const n = Math.min(204, dev.ledCount('ch' + (ch + 1), 16));
      if (n > 0) { zones.push({ id: 'ch' + (ch + 1), name: 'ARGB ' + (ch + 1), leds: n, kind: 'strip', category: 'strip' }); channels.push({ ch, n }); }
    }
    const fans = [];
    fanOn.forEach((on, i) => { if (on) fans.push({ id: 'fan' + (i + 1), name: 'Fan ' + (i + 1) }); });
    return { zones, fans };
  },

  // per channel: port state = software, then planar R/G/B packets of up to 50 LEDs, then one commit
  render(dev, rgb) {
    let off = 0;
    for (const { ch, n } of channels) {
      send(dev, CMD.portState, [ch, 0x02]);
      for (let start = 0; start < n; start += 50) {
        const cnt = Math.min(50, n - start);
        for (let plane = 0; plane < 3; plane++) {
          const p = [ch, start, cnt, plane];
          for (let i = 0; i < cnt; i++) p.push(rgb[(off + start + i) * 3 + plane]);
          send(dev, CMD.direct, p);
        }
      }
      off += n;
    }
    send(dev, CMD.commit, [0xff]);
  },

  poll(dev) {
    const temps = {}, rpm = {};
    probeOn.forEach((on, i) => {
      if (!on) return;
      const r = query(dev, CMD.getTemp, [i]);
      if (!r) return;
      const v = be16(r, 2) / 100;
      if (v > 0 && v < 120) temps['probe' + (i + 1)] = v;
    });
    fanOn.forEach((on, i) => {
      if (!on) return;
      const r = query(dev, CMD.getRpm, [i]);
      if (r) rpm['fan' + (i + 1)] = be16(r, 2);
    });
    return { temps, rpm };
  },

  setDuty(dev, duty) {
    for (let i = 0; i < FAN_COUNT; i++) {
      const d = duty['fan' + (i + 1)];
      if (d === undefined || !fanOn[i]) continue;
      send(dev, CMD.setDuty, [i, Math.max(0, Math.min(100, Math.round(d)))]);
    }
  },

  // fans back to the controller's own curve (probe 1 if fitted), else a quiet-but-safe fixed duty
  shutdown(dev) {
    try {
      const probe = probeOn.indexOf(true);
      fanOn.forEach((on, i) => {
        if (!on) return;
        if (probe >= 0) {
          const p = new Array(25).fill(0);                         // fan, probe, 6x temp*100 BE, 6x rpm BE (liquidctl)
          p[0] = i; p[1] = probe;
          SAFE_CURVE.forEach(([t, r], k) => { p[2 + k * 2] = (t * 100) >> 8; p[3 + k * 2] = (t * 100) & 255; p[14 + k * 2] = r >> 8; p[15 + k * 2] = r & 255; });
          send(dev, CMD.setProfile, p);
        } else send(dev, CMD.setDuty, [i, 70]);
      });
    } catch (e) { }
  },

  // Simulated-device tests (Glint.exe plugincheck). Expected bytes derived from liquidctl commander_pro.py + OpenRGB CorsairLightingNodeController.
  tests: [
    {
      name: 'Commander Pro: firmware/probe/fan-mode queries, 2x16 ARGB planar frame',
      device: { productId: 0x0c10, reportSize: 65, inputSize: 17 },
      replies: [
        { when: '0002', reply: '0000000A04' },                      // status 0, fw 0.10.4
        { when: '0010', reply: '000001010000' },                    // probes 1+2 connected
        { when: '0020', reply: '00000201000000' },                  // fan1 PWM, fan2 DC, rest not connected
      ],
      expectZones: 32,
      expectInit: ['0002', '0010', '0020'],
      // port state software (0x38, ch0, 2); R plane: LEDs 0..15 -> 0,3,6..; G plane 1,4,..; B plane 2,5,..
      expectFrame: ['00380002', '00320000100000030609 0C0F1215181B1E2124272A2D', '003200001001010407', '003200001002020508'],
    },
    {
      name: 'Obsidian 1000D uses the same protocol',
      device: { productId: 0x1d00, reportSize: 65, inputSize: 17 },
      replies: [{ when: '0002', reply: '0000010203' }, { when: '0010', reply: '000000000000' }, { when: '0020', reply: '0000000000' }],
      expectZones: 32,
      expectInit: ['0002'],
    },
    { name: 'declined when the firmware query gets no valid answer', device: { productId: 0x0c10, reportSize: 65 }, expectDeclined: true },
    { name: 'declined when the device reports an error status', device: { productId: 0x0c10, reportSize: 65, inputSize: 17 }, replies: [{ when: '0002', reply: '00FF000000' }], expectDeclined: true },
  ],
};
