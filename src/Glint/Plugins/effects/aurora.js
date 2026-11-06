// Aurora — slow, flowing curtains of colour, like the northern lights.
module.exports = {
  type: 'effect',
  name: 'Aurora',
  description: 'Flowing curtains of colour',
  preview: ['#00ffa0', '#00a0ff', '#a040ff'],
  params: {
    colors: { type: 'colors', label: 'Colours', default: ['#00ffa0', '#00a0ff', '#a040ff'] },
    speed: { type: 'number', label: 'Speed', min: 0.05, max: 2, step: 0.05, default: 0.4 },
    depth: { type: 'number', label: 'Contrast', min: 0, max: 1, step: 0.05, default: 0.6 },
  },
  render(ctx, leds, out) {
    const p = ctx.params, t = ctx.time * p.speed;
    for (let i = 0; i < leds.length; i++) {
      const l = leds[i];
      const n = glint.noise(l.x * 6 + t * 0.7, l.u * 2 + t * 0.4);
      const c = glint.palette(p.colors, n * 1.5 + t * 0.05);
      const b = 1 - p.depth + p.depth * glint.noise(l.x * 3 - t, 5.3);
      out[i * 3] = c[0] * b; out[i * 3 + 1] = c[1] * b; out[i * 3 + 2] = c[2] * b;
    }
  },
};
