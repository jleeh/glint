// TEMPLATE — copy to %APPDATA%\Glint\plugins\ as <your-effect>.js. Saving reloads it live.
module.exports = {
  type: 'effect',
  name: 'My effect',
  description: 'What it looks like in a few words',
  preview: ['#ff0080', '#00d0ff'],
  params: {
    speed: { type: 'number', label: 'Speed', min: 0.05, max: 2, step: 0.05, default: 0.5 },
    colors: { type: 'colors', label: 'Colours', default: ['#ff0080', '#00d0ff'] },
  },
  render(ctx, leds, out) {
    const p = ctx.params;
    for (let i = 0; i < leds.length; i++) {
      const c = glint.palette(p.colors, leds[i].x + ctx.time * p.speed * 0.2);
      out[i * 3] = c[0]; out[i * 3 + 1] = c[1]; out[i * 3 + 2] = c[2];
    }
  },
};
