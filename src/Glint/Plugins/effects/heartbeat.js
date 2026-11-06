// Heartbeat — a double "lub-dub" pulse whose rate follows a temperature sensor.
module.exports = {
  type: 'effect',
  name: 'Heartbeat',
  description: 'Pulse rate follows a temperature',
  preview: ['#ff1040', '#300008'],
  params: {
    color: { type: 'color', label: 'Colour', default: '#ff1040' },
    sensor: { type: 'sensor', label: 'Sensor', default: 'cpu' },
    calm: { type: 'number', label: 'Calm at (°C)', min: 20, max: 80, step: 1, default: 40 },
    racing: { type: 'number', label: 'Racing at (°C)', min: 40, max: 100, step: 1, default: 85 },
  },
  init(ctx) { ctx.state.phase = 0; },
  render(ctx, leds, out) {
    const p = ctx.params, temp = ctx.sensors[p.sensor] ?? p.calm;
    const k = glint.clamp((temp - p.calm) / (p.racing - p.calm), 0, 1);
    ctx.state.phase += ctx.dt * (0.8 + k * 2.2);            // 48 → 180 bpm
    const ph = ctx.state.phase % 1;
    const beat = Math.exp(-((ph - 0.08) ** 2) / 0.002) + 0.6 * Math.exp(-((ph - 0.28) ** 2) / 0.002);
    const b = 0.08 + 0.92 * glint.clamp(beat, 0, 1);
    for (let i = 0; i < leds.length; i++) {
      out[i * 3] = p.color[0] * b; out[i * 3 + 1] = p.color[1] * b; out[i * 3 + 2] = p.color[2] * b;
    }
  },
};
