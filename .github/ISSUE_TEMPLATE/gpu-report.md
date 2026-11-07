---
name: Graphics card fan report
about: Tell us whether Glint can read / drive the fans on your graphics card
labels: gpu, device-support
---

Thanks! Only NVIDIA desktop cards have been tested on real hardware so far, so every report helps.

**Card:** (brand + model, e.g. Sapphire Pulse RX 7800 XT)
**Driver version:** (NVIDIA / AMD Adrenalin / Intel Arc version)
**Laptop or desktop:**

**What the Plugins page shows for "Graphics card fans":**
(e.g. "built in · 2 fans on …" or "no controllable graphics-card fans found")

**Sensor read-out (read-only, safe):** with Glint running, open a terminal and run

```
"C:\Program Files\Glint\Glint.exe" ctl sensors.all > sensors.json
```

then paste **only your graphics card's section** below (search for `Gpu`). It lists the card's temperatures, fan
speeds and `Control` entries; please leave out the rest of the file.

```json

```

**Optional, only if you're comfortable:** put the card's fans in a rule (Cooling page), set a manual speed of 50%
(`Glint.exe ctl fans.override name="<rule>" percent=50`), and tell us whether the fan RPM changed. Then
`percent=null` to go back to automatic. Glint hands the fans back to the driver when you take them out of the rule.

- Did the RPM follow? yes / no
- Any warning on the Setup page (e.g. "Another program is controlling …")? 
- Zero-RPM / fan-stop mode on (AMD)? yes / no / don't know
