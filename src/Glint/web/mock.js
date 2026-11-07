// Dev-only mock bridge: lets the dashboard run in a normal browser (headless tests and README screenshots).
// The rig below is a FICTIONAL demo PC built from the bundled plugins' device / zone ids. It is not anyone's machine.
(() => {
  if (window.chrome?.webview) return;

  /* ------------------------------------------------------------ demo rig */
  const FX0 = { speed: 0.2, perZone: false, reverse: false, scale: 1, size: 0.3, offset: 0, density: 0.5 };
  const fx = o => ({ ...FX0, ...o });
  const rule = o => ({ combine: 'max', minDuty: 20, maxDuty: 100, rampUpPerSec: 25, rampDownPerSec: 4, ...o });
  const inp = (sensor, curve, smoothingSec = 0) => ({ sensor, curve, weight: 1, hysteresis: 2, smoothingSec });

  const PRESETS = {"Warm static":{"type":"static","colors":["#ff5a1f"]},"White":{"type":"static","colors":["#ffffff"]},"Rainbow wave":{"type":"wave","colors":["#ffffff"],"speed":0.25},"Rainbow spin (per fan)":{"type":"wave","colors":["#ffffff"],"speed":0.5,"perZone":true},"Ocean wave":{"type":"colorWave","colors":["#001a66","#0077ff","#00e5ff","#0044aa"],"speed":0.15},"Sunset gradient":{"type":"gradient","colors":["#ff2a00","#ff8a00","#b000ff"]},"Spinners":{"type":"spin","colors":["#00ffd0"],"speed":0.8,"size":0.5,"offset":0.15},"Comet":{"type":"comet","colors":["#ff0040","#ff8000","#ffff00"],"speed":0.35,"size":0.35},"Meteor rain":{"type":"meteor","colors":["#a0d8ff","#ffffff"],"speed":1.2,"density":0.4},"Starfield":{"type":"twinkle","colors":["#ffffff","#9fc8ff","#ffe0a0"],"density":0.6,"background":"#020210"},"Fire":{"type":"fire","colors":["#ffffff"],"speed":0.6},"Plasma":{"type":"plasma","colors":["#ffffff"],"speed":0.4},"Rain":{"type":"rain","colors":["#40a0ff"],"density":0.6},"Marquee":{"type":"marquee","colors":["#ff0000","#ffffff"],"speed":0.6,"size":2},"Purple + sparkles":{"type":"static","colors":["#3a0070"],"layers":[{"type":"twinkle","colors":["#ffffff"],"density":0.4}]},"Audio spectrum":{"type":"audio","colors":["#ff0040","#ffb000","#00ff80","#00a0ff"]},"Audio beat pulse":{"type":"audioPulse","colors":["#ff0060","#00d0ff","#ffe000"]},"Spectrum cycle":{"type":"spectrum","colors":["#ffffff"],"speed":0.05},"Breathing":{"type":"breathing","colors":["#2060ff"]},"CPU temperature":{"type":"temperature","colors":["#0060ff","#00ff60","#ff2000"],"sensor":"cpu","range":[40,85]},"Max(CPU,GPU) temperature":{"type":"temperature","colors":["#0060ff","#00ff60","#ff2000"],"sensor":"max(cpu,gpu)","range":[40,85]},"Off":{"type":"off","colors":["#ffffff"]}};
  for (const k in PRESETS) { PRESETS[k] = fx(PRESETS[k]); PRESETS[k].layers?.forEach((l, i, a) => a[i] = fx(l)); }

  // [device id, device name, [[zone id, zone name, leds, kind, cols, category, x, y, w, h], ...]]
  const RIG = [
    ['cc', 'Corsair Commander Core', [
      ['pump', 'Pump', 16, 'ring', 0, '', 0.43, 0.33, 0.075, 0.12],
      ['fan1', 'Fan 1', 8, 'ring', 0, '', 0.22, 0.075, 0.085, 0.136],
      ['fan2', 'Fan 2', 8, 'ring', 0, '', 0.335, 0.075, 0.085, 0.136],
      ['fan3', 'Fan 3', 8, 'ring', 0, '', 0.45, 0.075, 0.085, 0.136]]],
    ['lnp', 'Corsair Lighting Node Pro', [
      ['ch1', 'Channel 1', 40, 'strip', 0, '', 0.725, 0.11, 0.012, 0.6],
      ['ch2', 'Channel 2', 40, 'strip', 0, '', 0.13, 0.035, 0.5, 0.012]]],
    ['mystic', 'MSI Mystic Light (motherboard)', [
      ['onboard', 'Onboard LEDs', 6, 'strip', 0, 'case', 0.33, 0.6, 0.09, 0.02],
      ['jrainbow1', 'JRAINBOW1', 30, 'strip', 0, 'case', 0.045, 0.12, 0.012, 0.6],
      ['jrainbow2', 'JRAINBOW2', 30, 'strip', 0, 'case', 0.13, 0.775, 0.5, 0.012]]],
    ['ram', 'G.Skill Trident Z RGB (ENE)', [
      ['dimm1', 'RAM stick 1', 8, 'stick', 0, '', 0.575, 0.28, 0.012, 0.24],
      ['dimm2', 'RAM stick 2', 8, 'stick', 0, '', 0.605, 0.28, 0.012, 0.24]]],
    ['razer25a', 'Razer BlackWidow V3 Pro', [
      ['keys', 'Keyboard', 132, 'matrix', 22, 'keyboard', 0.07, 0.86, 0.42, 0.105]]],
    ['rival1720', 'SteelSeries Rival 310', [
      ['z1', 'Scroll wheel', 1, 'single', 0, 'mouse', 0.54, 0.875, 0.03, 0.045],
      ['z0', 'Logo', 1, 'single', 0, 'mouse', 0.64, 0.875, 0.03, 0.045]]],
  ];
  const zones = [], devices = [], placements = {}, deviceNames = {};
  let start = 0;
  for (const [dev, name, zs] of RIG) {
    deviceNames[dev] = name;
    devices.push({ id: dev, name, zones: zs.map(z => ({ id: `${dev}:${z[0]}`, name: `${dev}:${z[0]}`, leds: z[2] })) });
    for (const [zid, zname, leds, kind, cols, category, x, y, w, h] of zs) {
      const id = `${dev}:${zid}`;
      zones.push({ id, dev, start, count: leds, ring: kind === 'ring', kind, cols, category, name: zname });
      placements[id] = { x, y, w, h, rotation: 0 };
      start += leds;
    }
  }
  const fans = [
    ...['pump', 'fan1', 'fan2', 'fan3'].map(f => ({ id: 'cc:' + f, name: 'cc:' + f, device: 'cc' })),
    ...[1, 2, 3, 4].map(n => ({ id: `mb:Fan #${n}`, name: `Fan #${n}`, device: 'Motherboard' })),
    ...[1, 2].map(n => ({ id: `gpu:Fan ${n}`, name: `GPU Fan ${n}`, device: 'Demo Graphics 9000' })),
  ];
  const hubs = [{ id: 'cc', name: 'Corsair Commander Core', firmware: '', channels: [
    { id: 'pump', name: 'pump', isPump: true }, { id: 'fan1', name: 'fan1', isPump: false }, { id: 'fan2', name: 'fan2', isPump: false }, { id: 'fan3', name: 'fan3', isPump: false }] }];

  const curves = {
    'cpu-balanced': [[40, 25], [60, 35], [75, 60], [85, 100]], 'gpu-balanced': [[45, 25], [65, 40], [78, 70], [85, 100]],
    'liquid-balanced': [[30, 25], [35, 40], [40, 70], [45, 100]], 'cpu-quiet': [[50, 20], [70, 30], [80, 55], [90, 100]],
    'gpu-quiet': [[55, 20], [72, 35], [82, 65], [88, 100]], 'liquid-quiet': [[32, 20], [38, 35], [43, 70], [47, 100]],
    pump: [[30, 70], [40, 85], [45, 100]],
  };
  const RAD = ['cc:fan1', 'cc:fan2', 'cc:fan3'], CASE = ['mb:Fan #2', 'mb:Fan #3', 'mb:Fan #4'];
  const profile = mode => [
    rule({ name: 'AIO pump', fans: ['cc:pump'], inputs: [inp('liquid', 'pump')], minDuty: 60 }),
    rule({ name: 'Radiator fans', fans: RAD, inputs: [inp('liquid', `liquid-${mode}`), inp('cpu', `cpu-${mode}`, 4)] }),
    rule({ name: 'Case fans', fans: CASE, inputs: [inp('gpu', `gpu-${mode}`), inp('cpu', `cpu-${mode}`, 4)] }),
  ];
  const config = {
    pollMs: 1000, failsafeDuty: 100, fanControl: true, curves, activeProfile: 'Balanced',
    profiles: { Balanced: profile('balanced'), Quiet: profile('quiet'), 'Full speed': [rule({ name: 'Everything', fans: ['cc:pump', ...RAD, ...CASE], inputs: [], fixed: 100 })] },
    fanNames: { 'cc:fan1': 'Radiator 1', 'cc:fan2': 'Radiator 2', 'cc:fan3': 'Radiator 3', 'mb:Fan #1': 'CPU fan', 'mb:Fan #2': 'Front intake 1', 'mb:Fan #3': 'Front intake 2', 'mb:Fan #4': 'Rear exhaust' },
    lighting: {
      enabled: true, brightness: 100, fps: 30,
      effect: fx({ type: 'plugin', plugin: 'Aurora', colors: ['#ffffff'], params: {} }),
      overrides: {}, presets: PRESETS,
      ledCounts: { 'lnp:ch1': 40, 'lnp:ch2': 40, 'mystic:jrainbow1': 30, 'mystic:jrainbow2': 30 },
      layout: {},
    },
    sensorNames: { 'mb:Temperature #1': 'Motherboard', 'mb:Temperature #2': 'Chipset' },
    homeAssistant: { enabled: false, host: '', port: 1883, tls: false, discoveryPrefix: 'homeassistant', baseTopic: 'glint' },
    overlay: { enabled: true, hotkey: 'Ctrl+Shift+O', position: 'top-left', offsetX: 16, offsetY: 16, scale: 1, opacity: 1, backgroundOpacity: 0.72, accent: null, layout: 'detailed', onlyWhenGame: false,
      items: [{ source: 'fps' }, { source: 'frametime' }, { source: 'low1' }, { source: 'cpu' }, { source: 'gpu' }, { source: 'gpu.hotspot' }, { source: 'liquid' }] },
  };

  /* ------------------------------------------------------------ plugins (the bundled set + a few user ones) */
  const cut = s => s.length > 58 ? s.slice(0, 57) + '…' : s;
  const BUNDLED = [
    ['asrock-polychrome.js', 'device', 'ASRock Polychrome (motherboard)', '26ce:01a2,01a6'],
    ['asus-aura-usb.js', 'device', 'ASUS Aura (motherboard)', '0b05:1867,1872,18a3,18a5,19af,1aa6,1bed'],
    ['corsair-commander-core.js', 'device', 'Corsair Commander Core', '1b1c:0c1c,0c32,0c1d,0c3c,0c3d,0c3e,0c2a'],
    ['corsair-commander-pro.js', 'device', 'Corsair Commander Pro', '1b1c:0c10,1d00'],
    ['corsair-dram.js', 'smbus', 'Corsair DRAM', 'SMBus 0x18 0x19 0x1a 0x1b 0x1c 0x1d 0x1e 0x1f 0x58 0x59 0x5a 0x5b'],
    ['corsair-hydro-platinum.js', 'device', 'Corsair Hydro Platinum / Pro XT / Elite RGB', '1b1c:0c17,0c18,0c19,0c20,0c21,0c22,0c29,0c35,0c36,0c37'],
    ['corsair-icue-link.js', 'device', 'Corsair iCUE LINK System Hub', '1b1c:0c3f'],
    ['corsair-lighting-node.js', 'device', 'Corsair Lighting Node', '1b1c:0c0b,0c1a'],
    ['corsair-mouse.js', 'device', 'Corsair mouse', '1b1c:1b34,1b74,1b3c,1b75,1b5d,1b12,1b2e,1b5a,1b5c'],
    ['crucial-ballistix.js', 'smbus', 'Crucial Ballistix RGB', 'SMBus 0x27 0x39 0x3a 0x3b 0x3c 0x20 0x21 0x22 0x23'],
    ['ene-dram.js', 'smbus', 'ENE (ASUS Aura) DRAM', 'SMBus 0x70 0x71 0x72 0x73 0x74 0x75 0x76 0x77'],
    ['gigabyte-rgb-fusion2.js', 'device', 'Gigabyte RGB Fusion 2 (motherboard)', '048d:8297,5702,8950,5711'],
    ['hyperx-alloy-origins.js', 'device', 'HyperX Alloy Origins', '0951:16e5'],
    ['hyperx-pulsefire.js', 'device', 'HyperX Pulsefire', '0951:16d3,16d7,16de,1727'],
    ['kingston-fury.js', 'smbus', 'Kingston Fury DRAM', 'SMBus 0x58 0x59 0x5a 0x5b 0x5c 0x5d 0x5e 0x5f 0x60 0x61'],
    ['lianli-unihub.js', 'device', 'Lian Li Uni Hub (SL Infinity / SL V2 / AL V2 / AL)', '0cf2:a101,a102,a103,a104,a105'],
    ['logitech-g213.js', 'device', 'Logitech G213', '046d:c336'],
    ['logitech-g402.js', 'device', 'Logitech G402', '046d:c07e'],
    ['logitech-g815.js', 'device', 'Logitech G815/G915 keyboard', '046d:c232,c33f,c33e,c343'],
    ['logitech-hidpp-zones.js', 'device', 'Logitech mouse (HID++ zones)', '046d:c092,c084,c080,c083,c08f,c332,c08b,c08d'],
    ['msi-mystic-light.js', 'device', 'MSI Mystic Light (motherboard)', '1462:1720,7b12,7b16,7b17,7b18,7b50,7b85,7b92,7b93,7c02'],
    ['nzxt-hue2.js', 'device', 'NZXT HUE 2 / Smart Device V2 / RGB & Fan Controller', '1e71:2001,2002,2005,2006,2009,200b,200d'],
    ['nzxt-kraken.js', 'device', 'NZXT Kraken X3 / Z3 (RGB)', '1e71:2007,2014,3008'],
    ['razer-chroma.js', 'device', 'Razer Chroma', '1532:0044,0046,004c,0050,0053,0059,005c,0060,0064,0065'],
    ['steelseries-apex.js', 'device', 'SteelSeries Apex', '1038:161a,1622,161c,1612,1618,1610,1614'],
    ['steelseries-rival.js', 'device', 'SteelSeries Rival', '1038:171e,1720,1722,1724,172e,1736,1824,1832,1834,184c'],
    ['aurora.js', 'effect', 'Aurora', 'Flowing curtains of colour'],
    ['heartbeat.js', 'effect', 'Heartbeat', 'Pulse rate follows a temperature'],
    ['lava.js', 'effect', 'Lava lamp', 'Warm blobs drifting slowly'],
  ];
  // user effects: [file, name, description, preview, enabled]
  const USER_FX = [
    ['sunset.js', 'Sunset', 'Warm fade from gold to violet', ['#ffb000', '#ff3d6e', '#5a1aa0'], true],
    ['thunderstorm.js', 'Thunderstorm', 'Dark sky with lightning flashes', ['#0a1030', '#4060c0', '#e0e8ff'], true],
    ['synthwave.js', 'Synthwave', 'Neon pink and cyan scanlines', ['#ff2fb0', '#7a2cff', '#00e5ff'], true],
    ['forest-canopy.js', 'Forest canopy', 'Dappled green light', ['#0b3d1a', '#3ddc5a', '#c8ff70'], true],
    ['ember-glow.js', 'Ember glow', 'Slow smouldering embers', ['#2a0500', '#ff4a00', '#ffb347'], true],
    ['ice-crystals.js', 'Ice crystals', 'Cold glints on deep blue', ['#04143a', '#3fa8ff', '#e8f6ff'], true],
    ['galaxy-swirl.js', 'Galaxy swirl', 'Spiral arms of stars', ['#12002e', '#8a3dff', '#ff7ad9'], true],
    ['neon-tube.js', 'Neon tube', 'Buzzing neon with flicker', ['#ff1f6b', '#ff8ab0'], true],
    ['candle-flicker.js', 'Candle flicker', 'Soft candlelight', ['#ff9a2e', '#5a1e00'], true],
    ['police-lights.js', 'Police lights', 'Alternating red and blue', ['#ff0030', '#0040ff'], false],
  ];
  const row = (file, kind, name, message, source, extra = {}) => ({ file, path: '%APPDATA%\\Glint\\plugins\\' + file, source, kind, name, enabled: true, ok: true, message: cut(message), needsApproval: false, ...extra });
  const list = [
    ...BUNDLED.map(([f, k, n, m]) => row(f, k, n, m, 'bundled')),
    row('desk-lamp-usb.js', 'device', 'Desk lamp (USB)', '16c0:05dc', 'user'),
    ...USER_FX.map(([f, n, d, , en]) => row(f, 'effect', n, d, 'user', { enabled: en })),
    row('patriot-viper.js', 'smbus', 'Patriot Viper RGB DRAM', 'Needs your approval: this plugin wants SMBus access', 'user', { ok: false, needsApproval: true }),
    row('my-effect.js', '?', 'my-effect', "SyntaxError: Unexpected token '}' (line 14)", 'user', { ok: false }),
  ];
  const effects = [
    { name: 'Aurora', description: 'Flowing curtains of colour', preview: ['#00ffa0', '#00a0ff', '#a040ff'], params: { colors: { type: 'colors', label: 'Colours', default: ['#00ffa0', '#00a0ff', '#a040ff'] }, speed: { type: 'number', label: 'Speed', min: 0.05, max: 2, step: 0.05, default: 0.4 }, depth: { type: 'number', label: 'Contrast', min: 0, max: 1, step: 0.05, default: 0.6 } } },
    { name: 'Heartbeat', description: 'Pulse rate follows a temperature', preview: ['#ff1040', '#300008'], params: { color: { type: 'color', label: 'Colour', default: '#ff1040' }, sensor: { type: 'sensor', label: 'Sensor', default: 'cpu' }, calm: { type: 'number', label: 'Calm at (°C)', min: 20, max: 80, step: 1, default: 40 }, racing: { type: 'number', label: 'Racing at (°C)', min: 40, max: 100, step: 1, default: 85 } } },
    { name: 'Lava lamp', description: 'Warm blobs drifting slowly', preview: ['#ff3000', '#ffa000', '#600010'], params: { colors: { type: 'colors', label: 'Colours', default: ['#ff3000', '#ffa000', '#ff0060'] }, background: { type: 'color', label: 'Background', default: '#200004' }, speed: { type: 'number', label: 'Speed', min: 0.05, max: 1.5, step: 0.05, default: 0.25 }, blobs: { type: 'number', label: 'Blob size', min: 0.1, max: 1, step: 0.05, default: 0.45 } } },
    ...USER_FX.filter(x => x[4]).map(([, name, description, preview]) => ({ name, description, preview, params: {} })),
  ];
  list.unshift({ file: 'builtin-gpu-fans', path: '', source: 'built-in', kind: 'device', name: 'Graphics card fans', enabled: true, ok: true, message: 'built in · 2 fans on Demo Graphics 9000', needsApproval: false });
  const plugins = {
    dir: '%APPDATA%\\Glint\\plugins', exe: 'C:\\Program Files\\Glint\\Glint.exe', list, effects,
    status: list.filter(x => !x.ok).map(x => ({ file: x.file, kind: x.kind, name: x.name, ok: false, message: x.message })),
  };

  const INIT = {
    type: 'init', config, fans, zones, devices, hubs, deviceNames, placements, aspect: 1.6, plugins,
    log: ['Demo rig (mock.js): no hardware is accessed'], effects: [], elevated: true, version: '0.2.0', haHasPassword: false,
    releasesUrl: 'https://github.com/jleeh/glint/releases/latest', cannotInstall: null,
  };
  config.updates = { check: true, autoInstall: false };
  config.openRgb = { enabled: false, host: '127.0.0.1', port: 6742, exclude: [] };

  /* ------------------------------------------------------------ setup check (a fictional, mostly healthy PC) */
  const UPDATE = { current: '0.2.0', latest: '0.3.0', url: 'https://github.com/jleeh/glint/releases/tag/v0.3.0', available: true, checkedAt: new Date().toISOString(), note: null };
  const setupReport = () => {
    const cfg = window.__lastConfig ?? INIT.config;
    const it = (id, title, status, detail, fixLabel, fix, fixArg, important = false) => ({ id, title, status, detail, ...(fix ? { fixLabel, fix, fixArg } : {}), important });
    const items = [
      it('elevated', 'Running as administrator', 'ok', 'Glint can read CPU temperatures, control motherboard fan headers and reach RAM lighting.'),
      it('pawnio', 'PawnIO working', 'ok', 'Sensors and the SMBus (RAM lighting) are available.'),
      it('webview2', 'WebView2 runtime 142.0.3595.53', 'ok', 'The dashboard runtime is installed.'),
      it('cpuTemp', 'CPU temperature readable (52.4 °C)', 'ok', 'Fan rules that follow the CPU work.'),
      it('conflict:signalrgb', 'SignalRGB starts with Windows', 'warn', 'SignalRGB also controls RGB devices. Two apps writing to the same device makes colours flicker and fan speeds jump. Quit it and remove it from startup (or switch off its lighting/fan control). Startup entry: "SignalRGB".', 'Open startup apps', 'startupApps'),
      it('plugins:approval', '1 plugin waiting for your approval', 'warn', 'Patriot Viper RGB DRAM: SMBus plugins only run after you approve them on the Plugins page.', 'Review on Plugins page', 'page', 'plugins'),
      it('plugins:failed', '1 plugin failed to load', 'fail', "my-effect.js: SyntaxError: Unexpected token '}' (line 14)", 'Open Plugins page', 'page', 'plugins'),
      it('devices', 'No devices declined', 'ok', 'Every device a plugin claimed identified itself as expected.'),
      it('startupTask', 'Starts with Windows', 'ok', 'The "Glint" logon task is registered (runs Glint as admin without a UAC prompt).'),
      cfg.updates?.check === false ? it('version', 'Glint 0.2.0', 'ok', 'Update check is off (Settings).')
        : it('version', 'Update available: v0.3.0', 'warn', "You have Glint 0.2.0. Install downloads the setup from Glint's GitHub releases, checks it against the release's SHA-256 list and GitHub's build attestation, runs it and restarts Glint.", 'Install update', 'installUpdate'),
      ...(window.__setupExtra ?? []),
    ];
    const n = st => items.filter(i => i.status === st).length;
    return { time: new Date().toISOString(), ok: n('ok'), warn: n('warn'), fail: n('fail'), importantFail: items.some(i => i.important && i.status === 'fail'), items };
  };
  let testPattern = null; // { zone, count }
  // one-click update: walks through the real states; window.__mockUpdateFail = 'reason' makes verification fail
  let install = { state: 'idle', version: null, message: null, bytes: 0, total: 0, at: new Date().toISOString() };
  const setInstall = (state, message, bytes = 0, total = 0) => { install = { state, version: '0.3.0', message, bytes, total, at: new Date().toISOString() }; emit({ type: 'updater', install }); };
  const runInstall = () => {
    if (['downloading', 'verifying', 'installing'].includes(install.state)) return;
    const T = 62914560;
    setInstall('downloading', 'Downloading Glint-Setup-0.3.0.exe…', 0, T);
    setTimeout(() => setInstall('downloading', 'Downloading Glint-Setup-0.3.0.exe…', T / 2, T), 150);
    setTimeout(() => setInstall('verifying', 'Verifying checksum and build attestation…'), 300);
    setTimeout(() => window.__mockUpdateFail ? setInstall('failed', 'Update not installed: ' + window.__mockUpdateFail + '.')
      : setInstall('installing', 'Starting the Glint 0.3.0 setup. Glint closes and restarts by itself.'), 450);
  };

  /* ------------------------------------------------------------ live state */
  const BASE = {
    sensors: { cpu: 52.4, gpu: 46.0, 'gpu.hotspot': 55.1, liquid: 31.4, 'cc:liquid': 31.4, storage: 41.0, ram: 38.5, 'mb:Temperature #1': 36.0, 'mb:Temperature #2': 44.0 },
    rpm: { 'cc:pump': 2380, 'cc:fan1': 1040, 'cc:fan2': 1052, 'cc:fan3': 1031, 'mb:Fan #1': 1150, 'mb:Fan #2': 812, 'mb:Fan #3': 806, 'mb:Fan #4': 790, 'gpu:Fan 1': 1310, 'gpu:Fan 2': 1305 }
  };
  const et = new EventTarget();
  let t0 = performance.now(), cpu = 0, previewOn = false;
  // tests: window.__mockTamper(msg) may rewrite every host message (hostile plugin / config data), __mockEmit sends one
  const emit = d => et.dispatchEvent(Object.assign(new Event('message'), { data: window.__mockTamper ? window.__mockTamper(structuredClone(d)) : d }));
  window.__mockEmit = emit; window.__mockInit = () => structuredClone(INIT);
  const sensorsAt = t => {
    const s = { ...BASE.sensors };
    s.cpu = +(s.cpu + 7 * Math.sin(t / 7) + 2.5 * Math.sin(t * 1.3)).toFixed(1);
    s.gpu = +(s.gpu + 12 * Math.max(0, Math.sin(t / 11))).toFixed(1);
    s['gpu.hotspot'] = +(s.gpu + 9.1).toFixed(1);
    s.liquid = s['cc:liquid'] = +(s.liquid + 1.2 * Math.sin(t / 20)).toFixed(1);
    return s;
  };
  // fan duty follows the active profile's curves (hottest input wins), so the numbers on screen agree with each other
  const MAX_RPM = { 'cc:pump': 3000, 'cc:fan1': 2400, 'cc:fan2': 2400, 'cc:fan3': 2400 };
  const lerp = (pts, t) => { if (t <= pts[0][0]) return pts[0][1]; for (let i = 1; i < pts.length; i++) if (t <= pts[i][0]) { const [a, b] = [pts[i - 1], pts[i]]; return a[1] + (b[1] - a[1]) * (t - a[0]) / (b[0] - a[0]); } return pts.at(-1)[1]; };
  const fanState = s => {
    const duty = {}, rpm = { ...BASE.rpm };
    for (const r of config.profiles[config.activeProfile] ?? []) {
      let d = r.fixed ?? Math.max(0, ...r.inputs.map(i => lerp(config.curves[i.curve] ?? [[0, 100]], s[i.sensor] ?? 100)));
      d = Math.round(Math.min(r.maxDuty, Math.max(r.minDuty, d)));
      for (const f of r.fans) { duty[f] = d; rpm[f] = Math.round((MAX_RPM[f] ?? 1900) * d / 100 * (0.98 + 0.04 * ((f.charCodeAt(f.length - 1) % 5) / 5))); }
    }
    return { duty, rpm };
  };
  const state = (back = 0) => {
    cpu += 0.0012 * 32;
    const sensors = sensorsAt((performance.now() - t0) / 1000 - back), { duty, rpm } = fanState(sensors);
    const ha = (window.__lastConfig ?? INIT.config).homeAssistant ?? {};
    const haStatus = { enabled: !!ha.enabled, connected: !!(ha.enabled && ha.host), broker: ha.host ? `${ha.tls ? 'mqtts' : 'mqtt'}://${ha.host}:${ha.port}` : '',
      lastError: ha.enabled && !ha.host ? 'no broker host set' : '', entities: ha.enabled && ha.host ? 27 : 0, reconnects: 0 };
    const cfgNow = window.__lastConfig ?? INIT.config;
    return { type: 'state', sensors, rpm, duty, explain: {}, profile: INIT.config.activeProfile, error: '', ha: haStatus,
      update: cfgNow.updates?.check === false ? { ...UPDATE, latest: null, available: false, checkedAt: null } : UPDATE, install, openRgb: null, testPattern: testPattern?.zone ?? null,
      overrides: Object.fromEntries(Object.entries((window.__lastConfig ?? INIT.config).profiles).map(([k, rs]) => [k, rs.map(r => r.override ?? null)])),
      perf: { cpuSeconds: cpu, privateMB: 58, fps: 30, devices: { cc: 30, lnp: 18.9, mystic: 21.7, ram: 10.1, razer25a: 30, rival1720: 30 }, cores: 32 } };
  };

  /* ------------------------------------------------------------ full sensor catalog (Sensors page) */
  // [name, type, unit, value(t, s, rpm) | number, aliases?]  — a fictional Ryzen / GeForce desktop, values loosely follow the live state
  const W = (b, a = 0, p = 9) => t => +(b + a * Math.sin(t / p + b)).toFixed(3);
  const cores = (n, f) => Array.from({ length: n }, (_, i) => f(i + 1));
  const HW = (t, s, rpm) => [
    { id: '/amdcpu/0', name: 'AMD Ryzen 7 7800X3D', type: 'Cpu', sensors: [
      ['Core (Tctl/Tdie)', 'Temperature', '°C', s.cpu, ['cpu', '/amdcpu/0/temperature/2']],
      ['CCD1 (Tdie)', 'Temperature', '°C', +(s.cpu - 3.2).toFixed(1), ['/amdcpu/0/temperature/3']],
      ['Core (SVI3 TFN)', 'Voltage', 'V', W(1.12, 0.04)(t)], ['SoC (SVI3 TFN)', 'Voltage', 'V', 1.25],
      ['CPU Total', 'Load', '%', W(14, 9, 3)(t)], ...cores(8, i => [`CPU Core #${i}`, 'Load', '%', W(6 + i * 2.3, 8, 2 + i / 3)(t)]),
      ...cores(8, i => [`Core #${i}`, 'Clock', 'MHz', W(4850 - i * 12, 120, 3 + i / 5)(t)]), ['Bus Speed', 'Clock', 'MHz', 100],
      ['Package', 'Power', 'W', W(58, 14, 3)(t)], ['Cores', 'Power', 'W', W(31, 10, 3)(t)], ['SoC', 'Power', 'W', 9.4],
    ], sub: [] },
    { id: '/motherboard', name: 'MSI MAG B650 TOMAHAWK WIFI', type: 'Motherboard', sensors: [], sub: [
      { id: '/lpc/nct6687d/0', name: 'Nuvoton NCT6687D', type: 'SuperIO', sensors: [
        ['+12V', 'Voltage', 'V', W(12.096, 0.012, 2)(t)], ['+5V', 'Voltage', 'V', W(5.03, 0.01, 2)(t)], ['Vcore', 'Voltage', 'V', W(1.13, 0.04)(t)],
        ['+3.3V', 'Voltage', 'V', 3.336], ['DRAM', 'Voltage', 'V', 1.352], ['CPU SA', 'Voltage', 'V', 1.248], ['VSB', 'Voltage', 'V', 3.392],
        ['Temperature #1', 'Temperature', '°C', s['mb:Temperature #1'], ['mb:Temperature #1']], ['Temperature #2', 'Temperature', '°C', s['mb:Temperature #2'], ['mb:Temperature #2']],
        ['VRM MOS', 'Temperature', '°C', W(47, 3)(t), ['mb:VRM MOS']], ['PCH', 'Temperature', '°C', W(44, 1)(t), ['mb:PCH']],
        ...cores(4, i => [`Fan #${i}`, 'Fan', 'RPM', rpm[`mb:Fan #${i}`], [`mb:Fan #${i}`]]),
        ...cores(4, i => [`Fan Control #${i}`, 'Control', '%', Math.round((rpm[`mb:Fan #${i}`] ?? 0) / 19), [`mb:Fan #${i}`]]),
      ] }] },
    { id: '/gpu-nvidia/0', name: 'NVIDIA GeForce RTX 4070 SUPER', type: 'GpuNvidia', sensors: [
      ['GPU Core', 'Temperature', '°C', s.gpu, ['gpu', '/gpu-nvidia/0/temperature/0']], ['GPU Hot Spot', 'Temperature', '°C', s['gpu.hotspot'], ['gpu.hotspot', '/gpu-nvidia/0/temperature/2']],
      ['GPU Memory Junction', 'Temperature', '°C', +(s.gpu + 6).toFixed(1), ['/gpu-nvidia/0/temperature/3']],
      ['GPU Core', 'Clock', 'MHz', W(2475, 140, 4)(t)], ['GPU Memory', 'Clock', 'MHz', 10501], ['GPU Video', 'Clock', 'MHz', W(1950, 90)(t)],
      ['GPU Core', 'Load', '%', W(31, 28, 5)(t)], ['GPU Memory Controller', 'Load', '%', W(18, 12, 5)(t)], ['GPU Video Engine', 'Load', '%', 0], ['GPU Bus', 'Load', '%', W(4, 3)(t)],
      ['GPU Fan 1', 'Fan', 'RPM', Math.round(W(1180, 160, 6)(t))], ['GPU Fan 2', 'Fan', 'RPM', Math.round(W(1172, 160, 6)(t))],
      ['GPU Fan 1', 'Control', '%', Math.round(W(36, 5, 6)(t))], ['GPU Fan 2', 'Control', '%', Math.round(W(36, 5, 6)(t))],
      ['GPU Package', 'Power', 'W', W(96, 60, 5)(t)], ['GPU Memory Free', 'SmallData', 'MB', 9012], ['GPU Memory Used', 'SmallData', 'MB', 3276], ['GPU Memory Total', 'SmallData', 'MB', 12288],
      ['GPU PCIe Rx', 'Throughput', 'B/s', W(41e6, 30e6, 2)(t)], ['GPU PCIe Tx', 'Throughput', 'B/s', W(9e6, 6e6, 2)(t)],
    ], sub: [] },
    { id: '/ram', name: 'Total Memory', type: 'Memory', sensors: [
      ['Memory Used', 'Data', 'GB', W(14.2, 0.3)(t)], ['Memory Available', 'Data', 'GB', W(17.6, 0.3)(t)], ['Memory', 'Load', '%', W(44.6, 1)(t)],
      ['Virtual Memory Used', 'Data', 'GB', W(19.8, 0.4)(t)], ['Virtual Memory', 'Load', '%', W(41, 1)(t)],
    ], sub: [] },
    ...[1, 2].map(n => ({ id: `/memory/dimm/${n}`, name: `DIMM #${n} (16 GB DDR5-6000)`, type: 'Memory', sensors: [
      ['DIMM Temperature', 'Temperature', '°C', +(s.ram - (n - 1) * 0.75).toFixed(2), ['ram', `/memory/dimm/${n}/temperature/0`]],
      ['Thermal Sensor Low Limit', 'Temperature', '°C', 0], ['Thermal Sensor High Limit', 'Temperature', '°C', 55],
    ], sub: [] })),
    { id: '/nvme/0', name: 'Samsung SSD 990 PRO 2TB', type: 'Storage', sensors: [
      ['Composite Temperature', 'Temperature', '°C', s.storage, ['storage', 'disk:samsung-ssd-990-pro-2tb', '/nvme/0/temperature/0']], ['Temperature #2', 'Temperature', '°C', +(s.storage + 7).toFixed(1), ['/nvme/0/temperature/1']],
      ['Used Space', 'Load', '%', 61.4], ['Read Activity', 'Load', '%', W(2, 2, 1)(t)], ['Write Activity', 'Load', '%', W(1, 1, 1)(t)],
      ['Available Spare', 'Level', '%', 100], ['Percentage Used', 'Level', '%', 2],
      ['Data Read', 'Data', 'GB', 21893], ['Data Written', 'Data', 'GB', 16420],
      ['Read Rate', 'Throughput', 'B/s', W(3.4e6, 3e6, 1)(t)], ['Write Rate', 'Throughput', 'B/s', W(1.1e6, 1e6, 1)(t)],
    ], sub: [] },
    { id: '/nvme/1', name: 'WD Blue SN580 1TB', type: 'Storage', sensors: [
      ['Composite Temperature', 'Temperature', '°C', 36.0, ['disk:wd-blue-sn580-1tb', '/nvme/1/temperature/0']],
      ['Used Space', 'Load', '%', 38.2], ['Available Spare', 'Level', '%', 100], ['Percentage Used', 'Level', '%', 1],
      ['Data Read', 'Data', 'GB', 4102], ['Data Written', 'Data', 'GB', 3377], ['Read Rate', 'Throughput', 'B/s', 0], ['Write Rate', 'Throughput', 'B/s', W(2e4, 2e4, 1)(t)],
    ], sub: [] },
    { id: '/nic/0', name: 'Ethernet', type: 'Network', sensors: [
      ['Data Uploaded', 'Data', 'GB', 3.9], ['Data Downloaded', 'Data', 'GB', 27.4],
      ['Upload Speed', 'Throughput', 'B/s', W(9e4, 8e4, 2)(t)], ['Download Speed', 'Throughput', 'B/s', W(1.8e6, 1.6e6, 2)(t)], ['Network Utilization', 'Load', '%', W(0.6, 0.5, 2)(t)],
    ], sub: [] },
    { id: 'cc', name: 'Corsair Commander Core', type: 'FanHub', sensors: [
      ['liquid', 'Temperature', '°C', s.liquid, ['liquid', 'cc:liquid']],
      ...['pump', 'fan1', 'fan2', 'fan3'].map(f => [f, 'Fan', 'RPM', rpm['cc:' + f]]),
    ], sub: [] },
  ];
  const mm = new Map();
  let sensorsUntil = 0;
  const catalog = () => {
    const t = (performance.now() - t0) / 1000, sn = sensorsAt(t), st = { sensors: sn, rpm: fanState(sn).rpm };
    const hw = h => { const per = {}; return { id: h.id, name: h.name, type: h.type, sub: (h.sub ?? []).map(hw), sensors: h.sensors.map(([name, type, unit, v, aliases]) => {
      const id = `${h.id}/${type.toLowerCase()}/${per[type] = (per[type] ?? -1) + 1}`, value = v == null ? null : +(+v).toFixed(Math.abs(v) < 10 ? 3 : 1);
      const o = mm.get(id) ?? { min: value, max: value };
      if (value != null) { o.min = Math.min(o.min ?? value, value); o.max = Math.max(o.max ?? value, value); }
      mm.set(id, o);
      return { id, name, type, unit, value, min: o.min, max: o.max, ...(aliases ? { aliases } : {}) };
    }) }; };
    return { type: 'sensors', catalog: { time: new Date().toISOString(), hardware: HW(t, st.sensors, st.rpm).map(hw) } };
  };

  /* ------------------------------------------------------------ preview: a rough take on the configured effect */
  const hex = h => { const n = parseInt(String(h ?? '#000').slice(1), 16) || 0; return [n >> 16 & 255, n >> 8 & 255, n & 255]; };
  const hsv = h => { const f = n => { const k = (n + h * 6) % 6; return 255 * (1 - Math.max(0, Math.min(k, 4 - k, 1))); }; return [f(5), f(3), f(1)]; };
  const mix = (a, b, k) => a.map((v, i) => v + (b[i] - v) * k);
  const pal = (cols, u) => { const c = cols.map(hex); if (c.length === 1) return c[0]; u = ((u % 1) + 1) % 1 * c.length; const i = Math.floor(u); return mix(c[i % c.length], c[(i + 1) % c.length], u - i); };
  const ramp = (cols, u) => { const c = cols.map(hex); if (c.length === 1) return c[0]; u = Math.min(1, Math.max(0, u)) * (c.length - 1); const i = Math.min(c.length - 2, Math.floor(u)); return mix(c[i], c[i + 1], u - i); };
  function shade(e, x, y, t, z, i) {
    const type = String(e.type).toLowerCase(), cols = e.colors?.length ? e.colors : ['#ffffff'], sp = e.speed ?? 0.2;
    const a = (e.angle ?? 0) * Math.PI / 180, u = x * Math.cos(a) + y * Math.sin(a), dir = e.reverse ? -1 : 1;
    const along = z && e.perZone ? i / z.count : u;
    switch (type) {
      case 'off': return [0, 0, 0];
      case 'static': return hex(cols[0]);
      case 'gradient': return ramp(cols, u);
      case 'breathing': return hex(cols[0]).map(v => v * (0.25 + 0.75 * (0.5 + 0.5 * Math.sin(t * sp * 6))));
      case 'spectrum': return hsv((t * sp) % 1);
      case 'wave': return hsv(((along * (e.scale ?? 1) - dir * t * sp) % 1 + 1) % 1);
      case 'colorwave': return pal(cols, along * (e.scale ?? 1) - dir * t * sp);
      case 'spin': { if (!z || z.kind !== 'ring') return hex(cols[0]).map(v => v * 0.25); const p = ((i / z.count - t * sp * 1.5) % 1 + 1) % 1; return p < (e.size ?? 0.3) ? hex(cols[0]) : [0, 0, 0]; }
      case 'temperature': { const r = e.range ?? [40, 85], v = sensorsAt(t)[e.sensor] ?? sensorsAt(t).cpu; return ramp(cols, (v - r[0]) / (r[1] - r[0])); }
      case 'plugin': {
        const p = e.params ?? {}, pe = effects.find(q => q.name === e.plugin), c = p.colors ?? pe?.params?.colors?.default ?? pe?.preview ?? cols;
        const s = p.speed ?? 0.4, w = Math.sin(x * 5.2 + t * s * 1.7 + Math.sin(y * 3.1 - t * s)) * 0.5 + 0.5;
        const lum = 0.55 + 0.45 * (Math.sin(x * 9 - t * s * 2.3 + y * 4) * 0.5 + 0.5);
        return pal(c, x * 0.7 + w * 0.6 + t * s * 0.05).map(v => v * lum);
      }
      default: return pal(cols, u * 2 - t * sp);
    }
  }
  const total = zones.reduce((n, z) => n + z.count, 0);
  const AUTO = JSON.parse(JSON.stringify(placements));
  const preview = () => {
    const t = (performance.now() - t0) / 1000 + (window.__mockTime ?? 0), b = new Uint8Array(total * 3);
    const L = (window.__lastConfig ?? INIT.config).lighting, k = L.enabled ? L.brightness / 100 : 0;
    for (const z of zones) {
      const e = L.overrides?.[z.id] ?? L.overrides?.[z.dev] ?? L.effect;
      const pl = L.layout?.[z.id] ?? placements[z.id];
      const pts = typeof window.ledPositions === 'function' ? window.ledPositions(pl, z) : Array.from({ length: z.count }, () => [pl.x + pl.w / 2, pl.y + pl.h / 2]);
      for (let i = 0; i < z.count; i++) {
        const c = shade(e, pts[i][0], pts[i][1], t, z, i), j = (z.start + i) * 3;
        b[j] = c[0] * k; b[j + 1] = c[1] * k; b[j + 2] = c[2] * k;
      }
      if (testPattern?.zone === z.id) // same pattern as LightingEngine.PaintTestPattern: white first, red last, a colour per 8 LEDs
        for (let i = 0; i < z.count; i++) {
          const BL = [[0, 255, 0], [0, 80, 255], [255, 200, 0], [200, 0, 255], [0, 255, 255], [255, 100, 0]];
          const c = i >= testPattern.count ? [0, 0, 0] : i === 0 ? [255, 255, 255] : i === testPattern.count - 1 ? [255, 0, 0] : BL[Math.floor(i / 8) % BL.length], j = (z.start + i) * 3;
          b[j] = c[0]; b[j + 1] = c[1]; b[j + 2] = c[2];
        }
    }
    const fw = 64, fh = 40, f = new Uint8Array(fw * fh * 3);
    for (let y = 0; y < fh; y++) for (let x = 0; x < fw; x++) {
      const c = shade(L.effect, x / fw, y / fh, t, null, 0), j = (y * fw + x) * 3;
      f[j] = c[0] * k; f[j + 1] = c[1] * k; f[j + 2] = c[2] * k;
    }
    const enc = a => { let s = ''; for (const x of a) s += String.fromCharCode(x); return btoa(s); };
    return { type: 'preview', rgb: enc(b), field: enc(f), fw, fh };
  };

  window.mockBridge = {
    addEventListener: (n, f) => et.addEventListener(n, f),
    postMessage: m => {
      m = structuredClone(m); // like WebView2: the host gets a copy, never the page's live objects
      if (m.type === 'ready') { setTimeout(() => { emit(INIT); /* two minutes of history for the sparklines */ for (let i = 120; i >= 0; i--) emit(state(i)); }, 10); setInterval(() => emit(state()), 1000); setInterval(() => performance.now() < sensorsUntil && emit(catalog()), 1000); setInterval(() => previewOn && emit(preview()), 50); }
      if (m.type === 'preview') previewOn = m.on;
      if (m.type === 'sensorsWanted') { if (m.reset) mm.clear(); if (performance.now() >= sensorsUntil) setTimeout(() => emit(catalog()), 5); sensorsUntil = performance.now() + 3000; }
      if (m.type === 'sensorsReset') mm.clear();
      if (m.type === 'config' && m.config.homeAssistant?.password != null) INIT.haHasPassword = m.config.homeAssistant.password !== '';
      if (m.type === 'config') window.__lastConfig = m.config;
      (window.__msgs ??= []).push(m);
      if (m.type === 'ready' || m.type === 'setupCheck') setTimeout(() => emit({ type: 'setup', report: setupReport() }), m.type === 'ready' ? 40 : 120);
      if (m.type === 'installUpdate' || (m.type === 'setupFix' && m.fix === 'installUpdate')) runInstall();
      if (m.type === 'testPattern') testPattern = m.on ? { zone: m.zone, count: m.count } : (testPattern?.zone === m.zone ? null : testPattern);
      if (m.type === 'autoLayout') { const cfg = window.__lastConfig ?? INIT.config; cfg.lighting.layout = JSON.parse(JSON.stringify(AUTO)); setTimeout(() => emit({ type: 'layout', placements: AUTO, config: cfg }), 20); }
    },
  };
})();
