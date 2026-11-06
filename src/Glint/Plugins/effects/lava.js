// Lava lamp — warm blobs drifting slowly around each fan and along each strip.
module.exports = {
  type: 'effect',
  name: 'Lava lamp',
  description: 'Warm blobs drifting slowly',
  preview: ['#ff3000', '#ffa000', '#600010'],
  params: {
    colors: { type: 'colors', label: 'Colours', default: ['#ff3000', '#ffa000', '#ff0060'] },
    background: { type: 'color', label: 'Background', default: '#200004' },
    speed: { type: 'number', label: 'Speed', min: 0.05, max: 1.5, step: 0.05, default: 0.25 },
    blobs: { type: 'number', label: 'Blob size', min: 0.1, max: 1, step: 0.05, default: 0.45 },
  },
  render(ctx, leds, out) {
    const p = ctx.params, t = ctx.time * p.speed;
    for (let i = 0; i < leds.length; i++) {
      const l = leds[i];
      const v = glint.noise(l.u * 3 + l.zone * 7.1, t + l.zone * 1.3);
      const k = glint.clamp((v - (1 - p.blobs)) / 0.25, 0, 1);
      const c = glint.mix(p.background, glint.palette(p.colors, l.zone * 0.37 + t * 0.03), k);
      out[i * 3] = c[0]; out[i * 3 + 1] = c[1]; out[i * 3 + 2] = c[2];
    }
  },
};
