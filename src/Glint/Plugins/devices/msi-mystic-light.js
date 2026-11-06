// MSI Mystic Light motherboard RGB (USB HID, vendor 1462): onboard LEDs + JRGB / JRAINBOW / JCORSAIR / JPIPE / JARGB headers.
// Protocol per OpenRGB Controllers/MSIMotherboardController: MSIMotherboard185Controller (per-LED "direct" mode over
// feature reports 0x52/0x53, or the zone-based direct packet), MSIMotherboard162Controller (static zone colours),
// MSIMotherboard761Controller (newest boards: feature reports 0x50 config + 0x51 per-header LED data).
// The report variant is decided like OpenRGB does by feature-report size, but Glint only exposes the collection's maximum
// feature size, so that size is combined with the board's PID (OpenRGB's per-board tables) - 185: PID in the 185 table,
// 761: the MS-7E20/32/34/59/66/70/76/80/81/86 boards, 162: any other PID with a 162/163-byte feature report. The 112-byte
// variant has no PID in the OpenRGB detector list and is not supported. Unknown boards are declined.
// Boards with no direct mode (OpenRGB 'DIRECT_MODE_DISABLED') are declined too: this plugin only ever uses live (not
// saved) state: save_data is always 0, and exiting Glint re-sends the configuration read at start-up. Never sends the
// firmware / ROM commands. Header lengths (JRAINBOW/JCORSAIR/JARGB) can't be read: set them on the Devices page.
// Not supported without DMI access: OpenRGB's 761-byte board-name table (all boards are treated as the common
// JAF + JARGB1-3 layout) and 0DB0:0076 "common PID" boards. MSI Center / Dragon Center must not be running.
const ZSETS = [
  'R1,R2,B1,B2,O',
  'R1,B1,B2,C',
  'R1,B1,B2,O',
  'R1,B1,B2,C,P1,O',
  'R1,B1,B2,P1',
  'R1,B1',
  'B1',
  'R1,R2,B1,B2,P1,O',
  'R1,B1,B2,C,P1,P2',
  'R1,B1,B2,C,P1',
  'R1,B1,B2,B3',
  'R1,B1,B2',
  'R1,B1,B2,B3,P1,O',
  'R1,R2,B1,B2',
  'B1,B2',
  'R1,B1,B2,B3,P1,P2,O',
  'R1,B1,B2,B3,P1,P2',
  'R1,R2,B1,B2,P1',
  'R1,B1,B2,B3,P1',
  'B1,B2,B3',
  'R1',
  'R1,R2,B1,B2,C,P1,P2,O'
];
const BOARDS185 = [   // [pid, onboard LEDs, pipe1 LEDs, pipe2 LEDs, JRGB headers, zone set, direct mode (P per-LED, Z zone-based, D none), min firmware]
  [0x7B93, 6, 1, 0, 1, 3, 'Z', 0],  // MPG X570 GAMING PRO CARBON WIFI
  [0x7C34, 0, 1, 1, 1, 8, 'Z', 0],  // MEG X570 GODLIKE
  [0x7C35, 0, 1, 0, 1, 9, 'Z', 0],  // MEG X570 ACE
  [0x7C36, 6, 1, 0, 1, 3, 'Z', 0],  // PRESTIGE X570 CREATION
  [0x7C37, 6, 0, 0, 2, 0, 'Z', 0],  // MPG X570 GAMING PLUS
  [0x7C56, 6, 0, 0, 1, 2, 'P', 6],  // MPG B550 GAMING PLUS
  [0x7C59, 0, 8, 0, 1, 9, 'D', 0],  // CREATOR TRX40
  [0x7C60, 6, 0, 0, 1, 1, 'P', 0],  // TRX40-A PRO
  [0x7C67, 6, 0, 0, 1, 5, 'P', 0],  // MAG B365M MORTAR
  [0x7C71, 6, 6, 0, 1, 3, 'P', 0],  // MEG Z490 ACE
  [0x7C73, 6, 4, 0, 1, 3, 'P', 0],  // MPG Z490 GAMING CARBON WIFI
  [0x7C75, 6, 0, 0, 1, 2, 'P', 0],  // MPG Z490 GAMING PLUS
  [0x7C76, 6, 0, 0, 2, 0, 'D', 0],  // MPG Z490M GAMING EDGE
  [0x7C77, 0, 0, 0, 0, 14, 'P', 0],  // MEG Z490I UNIFY
  [0x7C79, 6, 0, 0, 2, 0, 'P', 0],  // MPG Z490 GAMING EDGE WIFI
  [0x7C80, 6, 0, 0, 2, 0, 'P', 0],  // MAG Z490 TOMAHAWK
  [0x7C02, 6, 0, 0, 2, 0, 'P', 0],  // MAG B450 TOMAHAWK MAX
  [0x7C81, 6, 0, 0, 2, 0, 'P', 0],  // MAG B460 TOMAHAWK
  [0x7C82, 6, 0, 0, 1, 5, 'P', 0],  // MAG B460M MORTAR WIFI
  [0x7C83, 6, 0, 0, 1, 5, 'D', 0],  // B460M PRO-VDH WIFI
  [0x7C84, 6, 0, 0, 2, 0, 'Z', 0],  // MAG X570 TOMAHAWK WIFI
  [0x7C86, 6, 0, 0, 1, 5, 'D', 0],  // MPG B460I GAMING EDGE
  [0x7C87, 6, 0, 0, 2, 13, 'P', 0],  // B450M BAZOOKA MAX WIFI
  [0x7C90, 6, 4, 0, 1, 3, 'P', 0],  // MPG B550 GAMING CARBON WIFI
  [0x7C91, 6, 0, 0, 2, 0, 'P', 0],  // MAG B550 TOMAHAWK
  [0x7C92, 6, 0, 0, 0, 6, 'P', 0],  // MPG B550I GAMING EDGE WIFI
  [0x7C94, 6, 0, 0, 1, 11, 'P', 0],  // MAG B550M MORTAR
  [0x7C95, 6, 0, 0, 2, 0, 'P', 0],  // B550M PRO-VDH WIFI
  [0x7C98, 6, 0, 0, 1, 5, 'P', 0],  // Z490 PLUS
  [0x7D03, 0, 15, 18, 1, 8, 'P', 0],  // MPG Z590 GODLIKE
  [0x7D06, 4, 4, 0, 1, 3, 'P', 0],  // MPG Z590 GAMING FORCE
  [0x7D07, 4, 5, 0, 2, 7, 'P', 0],  // MPG Z590 GAMING EDGE WIFI
  [0x7D08, 6, 0, 0, 2, 0, 'P', 0],  // MAG Z590 TOMAHAWK
  [0x7D09, 6, 0, 0, 2, 11, 'P', 0],  // Z590-A PRO WIFI
  [0x7D13, 6, 0, 0, 1, 1, 'P', 0],  // MEG B550 UNIFY
  [0x7D14, 6, 0, 0, 1, 5, 'P', 0],  // A520M PRO
  [0x7D15, 6, 0, 0, 2, 0, 'P', 0],  // MAG B560 TOMAHAWK WIFI
  [0x7D17, 6, 0, 0, 2, 11, 'P', 0],  // MAG B560M MORTAR
  [0x7D18, 6, 0, 0, 2, 5, 'P', 0],  // MAG B560M PRO-VDH
  [0x7D19, 6, 0, 0, 2, 5, 'P', 0],  // MPG B560I GAMIING EDGE WIFI
  [0x7D20, 6, 0, 0, 2, 5, 'P', 0],  // MAG B560M PRO
  [0x7D25, 6, 0, 0, 2, 11, 'P', 0],  // PRO Z690-A WIFI DDR4
  [0x7D27, 6, 0, 0, 2, 1, 'P', 0],  // MEG Z690 ACE
  [0x7D28, 6, 0, 0, 1, 1, 'P', 0],  // MEG Z690 UNIFY-X
  [0x7D29, 6, 0, 0, 0, 6, 'P', 0],  // MEG Z690I UNIFY
  [0x7D30, 6, 6, 0, 2, 3, 'P', 0],  // MPG Z690 CARBON WIFI
  [0x7D31, 4, 8, 0, 2, 12, 'P', 0],  // MPG EDGE WIFI DDR4
  [0x7D32, 1, 0, 0, 1, 10, 'P', 0],  // MAG Z690 TOMAHAWK WIFI DDR4
  [0x7D33, 6, 0, 0, 2, 5, 'P', 0],  // PRO Z790-VC WIFI
  [0x7D36, 6, 0, 0, 2, 5, 'P', 0],  // PRO Z690-P DDR4
  [0x7D37, 6, 0, 0, 2, 11, 'P', 0],  // PRO B760M-VC WIFI (MS-7D37) - fans on JRAINBOW2
  [0x7D38, 0, 0, 0, 1, 1, 'P', 0],  // MEG Z590 UNIFY-X
  [0x7D40, 0, 0, 0, 1, 5, 'P', 0],  // MPG B760i EDGE WIFI DDR4
  [0x7D41, 6, 0, 0, 2, 13, 'P', 0],  // MAG B660M TOMAHAWK WIFI DDR4
  [0x7D42, 6, 0, 0, 2, 11, 'P', 0],  // MAG B660 MORTAR WIFI DDR4
  [0x7D43, 0, 0, 0, 2, 11, 'P', 0],  // PRO B660M-A WIFI DDR4
  [0x7D46, 0, 1, 1, 0, 11, 'P', 0],  // PRO H610M-G DDR4
  [0x7D50, 6, 12, 0, 1, 3, 'P', 0],  // MEG X570S ACE MAX
  [0x7D51, 6, 0, 0, 2, 1, 'P', 0],  // MEG X570S UNIFY-X MAX
  [0x7D52, 6, 14, 0, 1, 3, 'P', 0],  // MPG X570S CARBON EK X
  [0x7D53, 6, 0, 0, 2, 0, 'P', 0],  // MPG X570S EDGE MAX WIFI
  [0x7D54, 6, 0, 0, 2, 0, 'P', 0],  // MAG X570S TOMAHAWK MAX WIFI
  [0x7D59, 0, 0, 0, 2, 11, 'P', 0],  // PRO B660-A DDR4
  [0x7D67, 0, 0, 0, 1, 11, 'P', 0],  // PRO X670-P WIFI
  [0x7D69, 9, 2, 4, 1, 15, 'P', 0],  // MEG X670E ACE
  [0x7D70, 0, 6, 0, 1, 9, 'P', 0],  // MPG X670E Carbon WIFI
  [0x7D73, 1, 0, 0, 0, 6, 'P', 0],  // MPG B650I EDGE WIFI
  [0x7D74, 0, 6, 0, 1, 18, 'P', 0],  // MPG B650 CARBON WIFI
  [0x7D75, 0, 0, 0, 2, 13, 'P', 0],  // MAG B650 TOMAHAWK WIFI
  [0x7D76, 0, 0, 0, 2, 13, 'P', 0],  // MAG B650M MORTAR WIFI
  [0x7D77, 0, 0, 0, 2, 13, 'P', 0],  // PRO B650M-A WIFI
  [0x7D78, 0, 0, 0, 2, 13, 'P', 0],  // PRO B650-P WIFI
  [0x7D86, 0, 18, 4, 1, 16, 'P', 0],  // MEG Z790 ACE
  [0x7D88, 0, 0, 0, 1, 20, 'P', 0],  // MPG Z790-S WIFI
  [0x7D89, 0, 6, 0, 1, 18, 'P', 0],  // MPG Z790 CARBON WIFI
  [0x7D90, 0, 0, 0, 1, 11, 'P', 0],  // B760M BOMBER DDR4
  [0x7D91, 1, 0, 0, 1, 10, 'P', 0],  // MAG Z790 TOMAHAWK WIFI
  [0x7D93, 6, 0, 0, 1, 2, 'P', 0],  // Z790 GAMING PRO WIFI
  [0x7D96, 0, 0, 0, 2, 13, 'P', 0],  // MAG B760 TOMAHAWK WIFI DDR5
  [0x7D97, 6, 0, 0, 2, 11, 'P', 0],  // MAG B660 MORTAR MAX WIFI DDR4
  [0x7D98, 0, 0, 0, 1, 11, 'P', 0],  // PRO B760-P WIFI DDR4
  [0x7D99, 6, 0, 0, 1, 11, 'P', 0],  // PRO B760M-A WIFI DDR4
  [0x7E01, 0, 0, 0, 1, 11, 'P', 0],  // MAG B760M MORTAR MAX
  [0x7E03, 6, 0, 0, 0, 6, 'P', 0],  // MPG Z790I EDGE WIFI
  [0x7E06, 0, 0, 0, 2, 11, 'P', 0],  // PRO Z790-P WIFI DDR4
  [0x7E07, 0, 0, 0, 2, 10, 'P', 0],  // PRO Z790-A WIFI DDR4
  [0x7E09, 0, 0, 0, 0, 19, 'P', 0],  // B650M PROJECT ZERO
  [0x7E10, 0, 6, 0, 2, 17, 'P', 0],  // MPG B650 EDGE WIFI
  [0x7E12, 0, 0, 0, 2, 13, 'P', 0],  // MAG X670E TOMAHAWK WIFI
  [0x0076, 0, 0, 0, 2, 13, 'P', 0],  // MAG X670E TOMAHAWK WIFI (Common PID)
  [0x7E16, 0, 0, 0, 2, 13, 'P', 0],  // X670E GAMING PLUS WIFI
  [0x7E24, 0, 0, 0, 2, 13, 'P', 0],  // B650M GAMING PLUS WIFI
  [0x7E26, 0, 0, 0, 2, 13, 'P', 0],  // B650 GAMING PLUS WIFI
  [0x7E27, 0, 0, 0, 2, 13, 'P', 0],  // PRO B650M-P
  [0x7E28, 6, 0, 0, 2, 21, 'Z', 0],  // MSI PRO A620M-B (MS-7E28)
  [0x7E30, 0, 0, 0, 2, 13, 'P', 0],  // B650M GAMING WIFI (MS-7E30)
];

const BOARDS162 = [[0x1720, 10, 0], [0x7B12, 10, 0], [0x7B17, 10, 0], [0x7B18, 6, 1], [0x7B50, 6, 2], [0x7B85, 7, 0], [0x7B92, 10, 0], [0xB926, 3, 3]];
const ZSETS162 = ['R1,R2,B1,C,O', 'R1,R2,B1,O', 'R1,R2,B1,B2,O', 'B1,O'];
const PIDS761 = [0x7E20, 0x7E32, 0x7E34, 0x7E59, 0x7E66, 0x7E70, 0x7E76, 0x7E80, 0x7E81, 0x7E86];
const ZN = {   // short zone code -> [id, name]
  R1: ['jrgb1', 'JRGB1'], R2: ['jrgb2', 'JRGB2'], B1: ['jrainbow1', 'JRAINBOW1'], B2: ['jrainbow2', 'JRAINBOW2'],
  B3: ['jrainbow3', 'JRAINBOW3'], C: ['jcorsair', 'JCORSAIR'], P1: ['pipe1', 'PIPE1'], P2: ['pipe2', 'PIPE2'], O: ['onboard', 'Onboard LEDs'],
};
// byte offsets of the ZoneData blocks (10 bytes; the rainbow / corsair ones are 11) in the 185-byte 0x52 report
const Z185 = { R1: 1, P1: 11, P2: 21, B1: 31, B2: 42, C: 53, B3: 53, OUTER: 64, O: 74, OB1: 84, R2: 174 };
const ONBOARD185 = [84, 94, 104, 114, 124, 134, 144, 154, 164];   // on_board_led_1..9
const SYNC_JR = 40, SYNC_CORSAIR = 120;
const FLAG_CUSTOM = 0x80, BASIC_SYNC = 0xb1, FULL_SYNC = 0xbf;

module.exports = {
  type: 'device',
  id: 'mystic',
  name: 'MSI Mystic Light (motherboard)',
  vendorId: 0x1462,
  productIds: [
  0x1720, 0x7B12, 0x7B16, 0x7B17, 0x7B18, 0x7B50, 0x7B85, 0x7B92, 0x7B93, 0x7C02, 0x7C34, 0x7C35, 0x7C36,
  0x7C37, 0x7C56, 0x7C59, 0x7C60, 0x7C67, 0x7C71, 0x7C73, 0x7C75, 0x7C76, 0x7C77, 0x7C79, 0x7C80, 0x7C81,
  0x7C82, 0x7C83, 0x7C84, 0x7C86, 0x7C87, 0x7C90, 0x7C91, 0x7C92, 0x7C94, 0x7C95, 0x7C98, 0x7D03, 0x7D04,
  0x7D06, 0x7D07, 0x7D08, 0x7D09, 0x7D13, 0x7D14, 0x7D15, 0x7D17, 0x7D18, 0x7D19, 0x7D20, 0x7D25, 0x7D27,
  0x7D28, 0x7D29, 0x7D30, 0x7D31, 0x7D32, 0x7D33, 0x7D36, 0x7D37, 0x7D38, 0x7D40, 0x7D41, 0x7D42, 0x7D43,
  0x7D46, 0x7D50, 0x7D51, 0x7D52, 0x7D53, 0x7D54, 0x7D59, 0x7D67, 0x7D69, 0x7D70, 0x7D73, 0x7D74, 0x7D75,
  0x7D76, 0x7D77, 0x7D78, 0x7D86, 0x7D88, 0x7D89, 0x7D90, 0x7D91, 0x7D93, 0x7D96, 0x7D97, 0x7D98, 0x7D99,
  0x7E01, 0x7E03, 0x7E06, 0x7E07, 0x7E09, 0x7E10, 0x7E20, 0x7E30, 0x7E32, 0x7E34, 0x7E59, 0x7E66, 0x7E70,
  0x7E76, 0x7E80, 0x7E81, 0x7E86, 0xB926
  ],
  usagePage: 0x0001,
  reportSize: 162,
  featureReports: true,
  minGapMs: 30,

  init(dev) {
    const pid = dev.productId, fs = dev.featureSize;
    this.kind = '';
    if (PIDS761.includes(pid)) return init761(this, dev, fs);
    const b = BOARDS185.find(x => x[0] === pid);
    if (b) return init185(this, dev, fs, b);
    if (fs === 162 || fs === 163) return init162(this, dev, pid);
    dev.log(`feature report size ${fs} for ${pid.toString(16)} is not a supported Mystic Light variant`);
    return false;
  },

  render(dev, rgb) {
    if (this.kind === '185p') render185p(this, dev, rgb);
    else if (this.kind === '185z') render185z(this, dev, rgb);
    else if (this.kind === '162') render162(this, dev, rgb);
    else if (this.kind === '761') render761(this, dev, rgb);
  },

  shutdown(dev) {
    // hand the board back its own configuration (as read at start-up; save flag stays 0)
    if (this.orig) { try { dev.sendFeature(this.orig); } catch (e) { /* ignore */ } }
  },

  tests: [
    {
      // enable_per_led_msg (constructor + SelectPerLedProtocol, sync mode) then the 0x53 sync packet with the 240-colour buffer:
      // onboard 6 | pipe1 0 | pipe2 0 | JRGB x2 | JRAINBOW1 40 | JRAINBOW2 40 | corsair 120
      name: 'MS-7C91 (185-byte, per-LED): firmware query, per-LED enable message, sync packet layout',
      device: { productId: 0x7C91, reportSize: 725 },
      replies: [{ when: '01B0', reply: '010060' }],
      expectZones: 68,
      expectInit: ['01B0CC', '5201000000080000008000010000002A0000008000010000002A00000080000100000029000000800028010000002900000080002801000000290000008200780100000028000000800025000000A9000000BF00010000002800000080000100000028000000800001000000280000008000010000002800000080000100000028000000800001000000280000008000010000002800000080000100000028000000800001000000280000008000010000002A000000800000'],
      expectFrame: ['5325060000BABBBCBDBEBFC0C1C2C3C4C5C6C7C8C9CACB000102030405060708'],
    },
    {
      name: 'MS-7C37 (185-byte, zone-based direct): one colour per zone in the 0x52 packet, onboard LEDs in on_board_led_1..6',
      device: { productId: 0x7C37, reportSize: 185 },
      expectZones: 10,
      expectFrame: ['52010001022800010280000100000028000000800001000000280000008000010607082806070880006401090A0B28090A0B80006401000000280000000000000100000000000000000001000000280000008000010C0D0E280C0D0E8000'],
    },
    {
      name: 'MS-7C56 needs APROM firmware byte >= 6 (compared raw, as OpenRGB does): 6 accepted',
      device: { productId: 0x7C56, reportSize: 725 },
      replies: [{ when: '01B0', reply: '010006' }],
      expectZones: 67,
    },
    {
      name: 'MS-7C56 with firmware byte 5 is declined',
      device: { productId: 0x7C56, reportSize: 725 },
      replies: [{ when: '01B0', reply: '010005' }],
      expectDeclined: true,
    },
    {
      // MSIMotherboard162Controller: static zone colours (speed low, brightness 100% = 0x28, custom colour flag 0x80)
      name: 'MS-7B18 (162-byte): zone colours written into the 0x52 packet, save flag 0',
      device: { productId: 0x7B18, reportSize: 162 },
      expectZones: 9,
      expectFrame: ['520100010228000102800001060708280607088000000000000000000000000000000000000000000001090A0B28090A0B8000'],
    },
    {
      // MSIMotherboard761Controller: config row Y16 = LED count; Update() sends JAF, JARGB1, JARGB2, JARGB3 as 0x51 packets
      name: 'MS-7E34 (761-byte): config push, JAF + JARGB per-header 0x51 packets',
      device: { productId: 0x7E34, reportSize: 761 },
      expectZones: 90,
      expectInit: ['5009FF000000FF000000FFFFFFFF03151E09FF000000FF000000FFFFFFFF03151E09FF000000FF000000FFFFFFFF03151E09FF000000FF000000FFFFFFFF0315000000000000000000000000000000001E0000000000000000000000000000001E0000000000000000000000000000001E0000000000000000000000000000001E0000000000000000000000000000001E0000000000000000000000000000001E0000000000000000000000000000001E0000000000000000000000000000001E0000000000000000000000000000001E0000000000000000000000000000001E0000000000000000000000000000001E0000000000000000000000000000001E0000000000000000000000000000001E09FF000000FF000000FFFFFFFF03951E00'],
      expectFrame: ['510908000000', '5109040000001E000102030405', '5109040100001E'],
    },
    { name: 'declines a 185-byte board whose feature report is too small for per-LED mode', device: { productId: 0x7C91, reportSize: 162 }, expectDeclined: true },
    { name: 'declines boards without a direct mode (MS-7C76)', device: { productId: 0x7C76, reportSize: 185 }, expectDeclined: true },
    { name: 'declines a 185-byte report on a 162-table board', device: { productId: 0x7B18, reportSize: 185 }, expectDeclined: true },
    { name: 'declines a 761-byte board with a short report', device: { productId: 0x7E34, reportSize: 162 }, expectDeclined: true },
  ]
};

const EFF185 = [1, 11, 21, 31, 42, 53, 64, 74, 84, 94, 104, 114, 124, 134, 144, 154, 164, 174];
const EFF162 = [1, 11, 21, 31, 41, 51, 61, 71, 81, 91, 101, 111, 121, 131, 141, 151];
// ---------------------------------------------------------------------------------------------- shared helpers
function zonesOf(set) { return set.split(','); }
function u8(n) { return new Uint8Array(n); }
function putZone(b, off, z) {         // ZoneData: effect, color(3), speed|brightness, color2(3), color flags, padding
  b[off] = z.effect === undefined ? 1 : z.effect;
  const c = z.color || [0, 0, 0], c2 = z.color2 || c;
  b[off + 1] = c[0]; b[off + 2] = c[1]; b[off + 3] = c[2];
  b[off + 4] = z.sb || 0;
  b[off + 5] = c2[0]; b[off + 6] = c2[1]; b[off + 7] = c2[2];
  b[off + 8] = z.cf || 0; b[off + 9] = z.pad || 0;
}
function read52(dev, size, effects) {   // the board's current configuration (feature report 0x52), or null if it looks wrong
  const r = dev.getFeature(0x52);
  if (!r || r.length < size || (r[0] !== 0x52 && r[0] !== 0x00)) return null;
  const orig = new Uint8Array(size);
  for (let i = 0; i < size; i++) orig[i] = r[i];
  orig[0] = 0x52; orig[size - 1] = 0;                   // report id, and never save
  for (const off of effects) if (orig[off] > 100) return null;   // effect ids are 0-39 / 0x25 / 100
  return orig;
}

// ---------------------------------------------------------------------------------------------- 185-byte boards
// A 185-byte FeaturePacket_185 with the C++ default member values (every ZoneData effect = static, rainbow cycle 100,
// corsair fan_flags 40).
function default185() {
  const b = u8(185);
  b[0] = 0x52;
  for (const off of [1, 11, 21, 64, 74, 84, 94, 104, 114, 124, 134, 144, 154, 164, 174]) b[off] = 1;
  b[31] = 1; b[41] = 100; b[42] = 1; b[52] = 100;
  b[53] = 1; b[57] = 40;
  return b;
}

// enable_per_led_msg of MSIMotherboard185Controller (constructor + SelectPerLedProtocol)
function enable185(sync, has) {
  const b = default185();
  const set = (off, sb, cf) => { b[off + 4] = sb; b[off + 8] = cf; };
  set(1, 0x08, 0x80);                       // j_rgb_1
  set(11, 0x2a, 0x80); set(21, 0x2a, 0x80); // j_pipe_1, j_pipe_2
  set(31, 0x29, 0x80); b[41] = 0x28;        // j_rainbow_1
  set(42, 0x29, 0x80); b[52] = 0x28;        // j_rainbow_2
  b[57] = 0x29; b[58] = 0x00; b[61] = 0x82; b[63] = 0x78;   // j_corsair: fan flags, quantity, padding[2], is_individual
  set(64, 0x28, 0x80);                      // j_corsair_outerll120
  b[74] = 0x25; b[78] = 0x29 | 0x80; b[82] = FULL_SYNC;     // on_board_led: direct, sync master
  for (const off of ONBOARD185) set(off, 0x28, 0x80);       // on_board_led_1..9
  set(174, 0x2a, 0x80);                     // j_rgb_2
  if (sync) {
    b[31] = 1; b[41] = SYNC_JR; b[42] = 1; b[52] = SYNC_JR;
    b[53] = 1; b[63] = SYNC_CORSAIR;
    b[82] = FULL_SYNC;
  } else {
    if (has.b1) { b[31] = 0x25; b[41] = 200; }
    if (has.b2) { b[42] = 0x25; b[52] = 240; }
    if (has.c) { b[53] = 0x25; b[63] = 240; }
    b[82] = BASIC_SYNC;
  }
  return b;
}

// zone_based_per_led_data of MSIMotherboard185Controller
function zoneBased185() {
  const b = default185();
  for (const off of [1, 11, 21, 31, 42, 74, ...ONBOARD185, 174]) { b[off + 4] = 40; b[off + 8] |= 0x80; }   // 100% brightness, custom colour
  return b;
}

function init185(self, dev, fs, board) {
  const [pid, nOnboard, nPipe1, nPipe2, nJrgb, setIdx, mode, minFw] = board;
  if (mode === 'D') { dev.log(`MS-${pid.toString(16)}: this board has no direct mode`); return false; }
  if (fs < 185 || (mode === 'P' && fs < 725)) { dev.log(`feature report size ${fs} doesn't match a 185-byte board`); return false; }
  const codes = zonesOf(ZSETS[setIdx]);
  const has = { o: codes.includes('O'), b1: codes.includes('B1'), b2: codes.includes('B2'), c: codes.includes('C') || codes.includes('B3') };
  if (mode === 'P' && !(has.o || has.b1 || has.b2 || has.c)) { dev.log('no per-LED capable zone on this board'); return false; }
  // firmware: APROM version, needed >= minFw on a few boards (output report 1: 01 B0 CC CC ...)
  let fw = -1;
  try {
    const q = u8(64); q[0] = 0x01; q[1] = 0xb0; for (let i = 2; i < 64; i++) q[i] = 0xcc;
    dev.write(q);
    const r = dev.read(300);
    if (r) fw = r[2];
  } catch (e) { /* no output report: version unknown */ }
  if (minFw > 0 && (fw < 0 || fw < minFw)) { dev.log(`firmware ${fw} is older than the minimum (${minFw}) for this board`); return false; }
  const orig = read52(dev, 185, EFF185);
  if (!orig) { dev.log('unexpected 0x52 configuration report'); return false; }
  self.orig = orig;
  const mixed = pid >= 0x7d03;
  const zones = [], items = [];
  for (const c of codes) {
    const [id, name] = ZN[c];
    let n, kind = 'strip';
    if (c === 'R1' || c === 'R2') n = 1;
    else if (c === 'P1') n = mode === 'Z' ? Math.min(nPipe1, 1) : nPipe1;
    else if (c === 'P2') n = mode === 'Z' ? Math.min(nPipe2, 1) : nPipe2;
    else if (c === 'O') n = nOnboard;
    else n = dev.ledCount(id, mode === 'Z' ? 1 : 30);      // JRAINBOW / JCORSAIR: length is user-set
    const cap = mode === 'Z' ? (c === 'O' ? 9 : 1) : capOf(c, false);
    n = Math.max(0, Math.min(cap, n));
    if (n === 0) continue;
    if (n === 1) kind = 'single';
    items.push({ c, n });
    zones.push({ id, name, leds: n, kind, category: 'case' });
  }
  if (!zones.length) return false;
  self.mixed = mixed; self.items = items; self.has = has;
  if (mode === 'Z') {
    self.kind = '185z';
    self.packet = zoneBased185();
  } else {
    // sync mode keeps every header within the single 240-LED packet; a longer header switches to per-header packets
    let sync = true;
    for (const it of items) if ((it.c === 'B1' || it.c === 'B2') && it.n > SYNC_JR) sync = false; else if ((it.c === 'B3' || it.c === 'C') && it.n > SYNC_CORSAIR) sync = false;
    for (const it of items) it.n = Math.min(it.n, capOf(it.c, sync));
    self.kind = '185p'; self.sync = sync;
    self.counts = { o: nOnboard, p1: nPipe1, p2: nPipe2, j: nJrgb };
    self.hdr = { sync: [0x53, 0x25, 0x06, 0, 0], b1: [0x53, 0x25, 0x04, 0, 0], b2: [0x53, 0x25, 0x04, 1, 0], c: [0x53, 0x25, 0x05, 0, 0] };
    // SelectPerLedProtocol: switch the board to per-LED direct mode
    dev.sendFeature(enable185(sync, has));
    dev.sleep(10);
  }
  dev.log(`MS-${pid.toString(16)} ${self.kind === '185z' ? 'zone-based' : self.sync ? 'per-LED (sync)' : 'per-LED'}: ${zones.map(z => z.id + '=' + z.leds).join(', ')}`);
  return { zones };
}

function capOf(c, sync) {            // GetMaxDirectLeds + the sync-mode limits
  switch (c) {
    case 'B1': return sync ? SYNC_JR : 200;
    case 'B2': return sync ? SYNC_JR : 240;
    case 'B3': case 'C': return sync ? SYNC_CORSAIR : 240;
    case 'R1': case 'R2': return 1;
    default: return 240;
  }
}

function render185p(self, dev, rgb) {
  const sync = u8(5 + 720), j1 = u8(5 + 720), j2 = u8(5 + 720), jc = u8(5 + 720);
  const H = self.hdr, C = self.counts;
  sync.set(H.sync); j1.set(H.b1); j2.set(H.b2); jc.set(H.c);
  const oOnboard = 0, oP1 = oOnboard + C.o, oP2 = oP1 + C.p1, oJ = oP2 + C.p2, oR1 = oJ + C.j, oR2 = oR1 + SYNC_JR, oC = oR2 + SYNC_JR;
  const put = (buf, idx, s) => { if (idx >= 0 && idx < 240) { buf[5 + idx * 3] = rgb[s]; buf[6 + idx * 3] = rgb[s + 1]; buf[7 + idx * 3] = rgb[s + 2]; } };
  let off = 0;
  for (const it of self.items) {
    for (let i = 0; i < it.n; i++) {
      const s = (off + i) * 3;
      switch (it.c) {
        case 'R1': put(sync, oJ, s); break;
        case 'R2': put(sync, oJ + 1, s); break;
        case 'P1': if (i < C.p1) put(sync, oP1 + i, s); break;
        case 'P2': if (i < C.p2) put(sync, oP2 + i, s); break;
        case 'O': if (i < C.o) put(sync, oOnboard + i, s); break;
        case 'B1': put(self.sync ? sync : j1, self.sync ? oR1 + i : i, s); break;
        case 'B2': put(self.sync ? sync : j2, self.sync ? oR2 + i : i, s); break;
        default: put(self.sync ? sync : jc, self.sync ? oC + i : i, s);   // B3 / C
      }
    }
    off += it.n;
  }
  if (!self.sync) {
    if (self.has.b1) { dev.sendFeature(j1); dev.sleep(13); }
    if (self.has.b2) { dev.sendFeature(j2); dev.sleep(13); }
    if (self.has.c) { dev.sendFeature(jc); dev.sleep(13); }
  }
  dev.sendFeature(sync);
}

function render185z(self, dev, rgb) {
  const b = self.packet;
  let off = 0;
  for (const it of self.items) {
    for (let i = 0; i < it.n; i++) {
      let z = -1;
      switch (it.c) {
        case 'R1': z = 1; break;
        case 'R2': z = self.mixed ? 134 : 174; break;       // mixed-mode boards keep JRGB2 in the on_board_led_6 slot
        case 'P1': z = 11; break;
        case 'P2': z = 21; break;
        case 'B1': z = 31; break;
        case 'B2': z = 42; break;
        case 'B3': case 'C': z = 53; break;
        case 'O': z = i < 9 ? ONBOARD185[i] : -1; if (z === 134 && self.mixed) z = 64; break;   // on_board_led_i+1; 6th is outerll120 when mixed
      }
      if (z >= 0) {
        const s = (off + i) * 3;
        for (let k = 0; k < 3; k++) { b[z + 1 + k] = rgb[s + k]; b[z + 5 + k] = rgb[s + k]; }   // color and color2
      }
    }
    off += it.n;
  }
  b[184] = 0;
  dev.sendFeature(b);
}

// ---------------------------------------------------------------------------------------------- 162-byte boards
// Static zone colours only (no direct mode on this generation): the board's own configuration is read, the zone colours are
// changed and the packet is written back (save_data 0). Resent only when a colour changed, plus now and then as keep-alive.
const Z162 = { R1: 0, B1: 1, C: 2, O: 4, R2: 15, B2: 14 };   // ZoneData slot index; on_board_led_n = 4 + n
function init162(self, dev, pid) {
  const orig = read52(dev, 162, EFF162);
  if (!orig) { dev.log('unexpected 0x52 configuration report'); return false; }
  const b = BOARDS162.find(x => x[0] === pid);
  const nOnboard = b ? b[1] : 10;
  const codes = zonesOf(ZSETS162[b ? b[2] : 0]);
  self.orig = orig;
  const zones = [], items = [];
  for (const c of codes) {
    const [id, name] = ZN[c];
    const n = c === 'O' ? nOnboard : 1;
    items.push({ c, n });
    zones.push({ id, name, leds: n, kind: n === 1 ? 'single' : 'strip', category: 'case' });
  }
  self.kind = '162'; self.items = items; self.last = ''; self.tick = 0;
  self.data = new Uint8Array(orig);
  dev.log(`162-byte board: ${zones.map(z => z.id + '=' + z.leds).join(', ')}`);
  return { zones };
}

function render162(self, dev, rgb) {
  const b = self.data;
  let off = 0;
  for (const it of self.items) {
    for (let i = 0; i < it.n; i++) {
      const slot = it.c === 'O' ? Z162.O + i : Z162[it.c];
      const o = 1 + slot * 10, s = (off + i) * 3;
      // SetMode(static, speed low, brightness 100%, custom colour) + SetZoneColor
      b[o] = 1; b[o + 4] = 40; b[o + 8] = (b[o + 8] | FLAG_CUSTOM) & ~0x01; b[o + 9] = 0;
      for (let k = 0; k < 3; k++) { b[o + 1 + k] = rgb[s + k]; b[o + 5 + k] = rgb[s + k]; }
    }
    off += it.n;
  }
  b[161] = 0;
  const key = b.join(',');
  if (key === self.last && (++self.tick % 60) !== 0) return;
  self.last = key;
  dev.sendFeature(b);
}

// ---------------------------------------------------------------------------------------------- 761-byte boards
// config rows: 0-2 JARGB1-3, 3 JAF, 4-8 JPIPE1-5, 9-10 JRGB1-2, 11-16 onboard, 17 select-all (16 bytes each after the 0x50 id)
function config761(counts) {          // initial_setup_array: JARGB1-3 + JAF on (counts = their LED counts), everything else off
  const a = u8(290);
  a[0] = 0x50;
  const on = [0x09, 0xff, 0, 0, 0, 0xff, 0, 0, 0, 0xff, 0xff, 0xff, 0xff, 0x03, 0x15, 0x78];
  for (let r = 0; r < 18; r++) {
    const o = 1 + r * 16;
    if (r <= 3) { for (let i = 0; i < 16; i++) a[o + i] = on[i]; a[o + 15] = counts[r]; }
    else if (r === 17) { for (let i = 0; i < 16; i++) a[o + i] = on[i]; a[o + 14] = 0x95; a[o + 15] = 0x1e; }   // "select all" row
    else a[o + 15] = 0x1e;                                // rows 4-16 (pipes, JRGB, onboard): switched off, LED count 0x1E
  }
  return a;
}

function init761(self, dev, fs) {
  if (fs < 727) { dev.log(`feature report size ${fs} doesn't match a 761-byte board`); return false; }
  // "read enable" sequence of the reference: read 0x50, then the 0x51 settings
  dev.getFeature(0x50);
  const s = dev.getFeature(0x51);
  if (!s || (s[0] !== 0x51 && s[0] !== 0x00)) return false;
  const defs = [['jaf', 'JAF', 0, 3], ['jargb1', 'JARGB 1', 30, 0], ['jargb2', 'JARGB 2', 30, 1], ['jargb3', 'JARGB 3', 30, 2]];
  const zones = [], counts = [0x1e, 0x1e, 0x1e, 0x1e];
  self.items = [];
  for (const [id, name, def, row] of defs) {
    const n = Math.max(0, Math.min(240, dev.ledCount(id, def)));
    counts[row] = n;
    if (n === 0) continue;
    self.items.push({ id, n, hdr0: id === 'jaf' ? 0x08 : 0x04, hdr1: id === 'jaf' ? 0 : row, row });
    zones.push({ id, name, leds: n, kind: 'strip', category: 'case' });
  }
  if (!zones.length) return false;
  self.cnt = { jaf: counts[3], jargb1: counts[0], jargb2: counts[1], jargb3: counts[2] };
  self.kind = '761';
  // push the configuration so colour packets are accepted (zone_set1: JAF + JARGB1-3 on, everything else off)
  dev.sendFeature(config761(counts));
  dev.sleep(10);
  dev.log(`761-byte board: ${zones.map(z => z.id + '=' + z.leds).join(', ')}`);
  return { zones };
}

function render761(self, dev, rgb) {
  // Update(): JAF, JARGB1, JARGB2, JARGB3 packets in that order, each 0x51 09 hdr0 hdr1 00 00 count + colours
  const order = ['jaf', 'jargb1', 'jargb2', 'jargb3'];
  let off = 0;
  const by = {};
  for (const it of self.items) { by[it.id] = { it, off }; off += it.n; }
  for (const id of order) {
    const p = u8(7 + 720);
    p[0] = 0x51; p[1] = 0x09;
    p[2] = id === 'jaf' ? 0x08 : 0x04; p[3] = id === 'jargb2' ? 1 : id === 'jargb3' ? 2 : 0;
    p[6] = self.cnt[id];
    const e = by[id];
    if (e) for (let i = 0; i < e.it.n * 3; i++) p[7 + i] = rgb[e.off * 3 + i];
    dev.sendFeature(p);
  }
}
