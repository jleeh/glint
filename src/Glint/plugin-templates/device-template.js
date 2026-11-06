// TEMPLATE — copy to %APPDATA%\Glint\plugins\ as <your-device>.js and fill in the blanks.
// Find VID/PID in Device Manager → your device → Details → Hardware Ids (VID_xxxx&PID_xxxx).
// Look up the protocol in OpenRGB / liquidctl / OpenLinkHub source, or capture it with Wireshark + USBPcap.
module.exports = {
  type: 'device',
  id: 'myhub',                    // short id; zones become "myhub:<zone>"
  name: 'My RGB Hub',
  vendorId: 0x1234,
  productIds: [0x5678],
  // interface: 0,                // optional: USB interface number (the MI_xx in the hardware id)
  // usagePage: 0xff00,           // optional: HID usage page of the collection to use
  // reportSize: 65,              // optional: minimum output report size (incl. report id byte)
  // featureReports: true,        // optional: set if the device is driven with feature reports
  minGapMs: 0,                    // optional: idle time the device needs between frames

  // Called once. Identify the device (read its firmware, check a reply) and describe it.
  // Return false if this isn't the device you expected — Glint will leave it alone.
  init(dev) {
    // dev.write([0x00, 0x01]); const reply = dev.read(200); if (!reply) return false;
    return {
      zones: [
        { id: 'ch1', name: 'Channel 1', leds: dev.ledCount('ch1', 30), kind: 'strip' }, // kind: strip | ring | matrix | single | stick
      ],
      fans: [
        // { id: 'fan1', name: 'Fan 1' },
      ],
    };
  },

  // Called for each frame: rgb = [r,g,b, r,g,b, ...] for every LED of every zone, in order.
  render(dev, rgb) {
    const packet = [0x00, 0x02, 0x00 /* zone */];
    for (let i = 0; i < rgb.length; i++) packet.push(rgb[i]);
    dev.write(packet);
  },

  // Optional fan support.
  // poll(dev) { return { rpm: { fan1: 1200 }, temps: { probe1: 31.5 } }; },
  // setDuty(dev, duty) { dev.write([0x00, 0x03, 0, duty.fan1]); },

  // Optional: hand control back to the device's own mode when Glint exits.
  // shutdown(dev) { dev.write([0x00, 0x04]); },
};
