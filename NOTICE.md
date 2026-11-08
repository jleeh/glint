# Third-party notices

Glint is licensed under the GNU General Public License v3.0 (see `LICENSE`).

## Protocol references

The bundled device plugins implement USB/SMBus lighting and fan protocols. The protocol knowledge comes from
these open-source projects; each plugin names the reference it follows:

| Project | License | Used for |
|---|---|---|
| [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) | GPL-2.0 | RGB controller protocols (ASUS Aura, Corsair, MSI, Gigabyte, ASRock, Razer, SteelSeries, HyperX, Logitech, ENE, Crucial, Kingston) |
| [liquidctl](https://github.com/liquidctl/liquidctl) | GPL-3.0 | Corsair Commander Pro, Hydro Platinum, NZXT Smart Device / Kraken |
| [OpenLinkHub](https://github.com/jurkovic-nikola/OpenLinkHub) | GPL-3.0 | Corsair Commander Core / Core XT / ST, iCUE LINK |

## Ported code

| Project | License | Used for |
|---|---|---|
| [Headroom](https://github.com/eviljarv89-ctrl/Headroom) (Copyright (c) 2026 Headroom contributors) | MIT | GPU tuning: NVML offsets, the CUDA stress test and its PTX kernels, crash classification (ported to C# in `src/Glint/Tuning/`) |

## Bundled binaries and libraries

| Component | License |
|---|---|
| `SmbusPIIX4.bin` from [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) (embedded, loaded by the separately installed [PawnIO](https://pawnio.eu) driver) | LGPL-2.1 |
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | MPL-2.0 |
| [HidSharp](https://www.zer7.com/software/hidsharp) | Apache-2.0 |
| [Jint](https://github.com/sebastienros/jint) | BSD-2-Clause |
| [Microsoft ClearScript](https://github.com/microsoft/ClearScript) (V8) | MIT |
| [NAudio](https://github.com/naudio/NAudio) | MIT |
| [Microsoft WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2) | Microsoft license (BSD-style) |

Product names and trademarks belong to their owners. Glint is not affiliated with or endorsed by any hardware vendor.
