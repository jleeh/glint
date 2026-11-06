# Security policy

## Supported versions

Only the latest release gets security fixes. Glint is a single-maintainer project, so there are no backports:
update to the newest version from the [releases page](https://github.com/jleeh/glint/releases/latest).

## Reporting a vulnerability

Please **don't open a public issue**. Report it privately with GitHub Security Advisories:
[Security → Report a vulnerability](https://github.com/jleeh/glint/security/advisories/new) on the repository.
Include the Glint version, what an attacker needs (e.g. "any program running as the user", "a device on the LAN"),
and steps or a proof of concept. You should get an answer within a week. Once a fix is released the advisory is
published, with credit if you want it.

## Threat model

Glint is installed to `Program Files` and started **elevated** (administrator, high integrity) at logon by a
scheduled task, because fan and SMBus access needs it. That makes it a privilege boundary: anything a normal
(medium-integrity) program running as the same user, a web page, a plugin, or a device on the network can make
Glint do with administrator rights is a vulnerability. Glint defends that boundary like this:

- **Control pipe** (`GlintControl_<user>`, used by `Glint.exe ctl` and the MCP server): only the current user, never
  over the network; when elevated, only administrators can create more instances of the name. Requests are size
  limited and one bad client can't take the service down. The pipe is meant for the user's own non-elevated tools,
  so everything it offers is safe for a normal-privilege caller: it can't approve SMBus plugins, can't name files
  outside the plugin folder, and never returns the stored MQTT password.
- **User folders** (`%APPDATA%\Glint`: config, plugins, layout backups, status): every read, write and delete there
  runs impersonating a de-elevated copy of Glint's token (Administrators deny-only, no privileges, medium
  integrity), so a junction or symbolic link planted in those folders reaches only what the user could reach anyway.
- **Plugins** run sandboxed: device plugins on Jint (no CLR access, memory, time and recursion limits, only the HID
  device they declared), effect plugins on V8 (no host objects, heap and ArrayBuffer caps, per-call watchdog).
  SMBus plugins need a one-time approval in the dashboard, stored by SHA-256 of the exact code in
  `trusted-plugins.json` next to `Glint.exe` (admin-only when installed); SPD EEPROM addresses are always blocked,
  undeclared addresses and writes before identifying a device are refused.
- **Dashboard** (WebView2): only the bundled page on `https://glint.local/` is loaded (navigation, new windows and
  downloads elsewhere are blocked, messages from any other origin ignored, strict Content-Security-Policy, all
  plugin/device/config text escaped), and `WEBVIEW2_*` environment overrides are ignored. WebView2 runs its browser
  processes de-elevated even when Glint is elevated, so its per-user profile folder grants no extra privilege. Only Glint's repository and the PawnIO /
  WebView2 download pages can be opened in the browser.
- **Network**: the update check talks only to the GitHub API (TLS, size-limited answer). Home Assistant (MQTT) and
  OpenRGB connect only to hosts the user configured; data from them is size-checked.

## In scope

- Privilege escalation from a normal-privilege program, plugin file, web content or LAN device to Glint's
  administrator rights (file write/read/delete, code or process execution, DLL loading, SMBus approval bypass).
- Escapes from the plugin sandboxes; SMBus guard bypasses (SPD writes, undeclared addresses).
- Script injection into the dashboard.
- Crashing or hanging the elevated service from the pipe, a plugin, a config file or a network peer (it controls fans).
- Secrets (the MQTT password) leaking into logs, `status.json` or API output.

## Out of scope

- Anything that already requires administrator rights (writing to `Program Files`, installing drivers, changing the
  scheduled task).
- A portable copy run elevated from a folder normal programs can write: protect the folder or use the installer.
- Same-user attacks that Windows itself does not treat as a boundary and that affect every elevated program started
  this way, such as runtime environment variables (`CORECLR_*` profilers) set in the user's environment before Glint
  starts. Glint disables .NET startup hooks and ignores WebView2 overrides, but can't block what the runtime loads
  before `Main`.
- What a plugin is allowed to do by design: drive the HID device it declared, read sensors, change lighting.
  Install plugins only from sources you trust.
- What the user configures: the Home Assistant / OpenRGB hosts Glint connects to (plain MQTT sends the password
  unencrypted unless `tls` is on), fan curves within the validated limits.
- Denial of service by squatting Glint's single-instance mutex or pipe name before it starts.
