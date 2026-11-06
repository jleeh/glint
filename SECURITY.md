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
- **Updates** (one-click, or automatic when the user switched that on): see [Updates](#updates) below. Glint only
  ever runs a setup of a newer release of this repository that passed every check.

## Updates

Installing an update means Glint, running as administrator, downloads a program and runs it. The updater
(`src/Glint/Core/Updater.cs`) treats that like the installer's own PawnIO / WebView2 downloads:

- **What it installs.** Only the release GitHub's `releases/latest` API reports for `jleeh/glint` (the repository is
  a constant in one place, `UpdateChecker.Repo`), only if it is newer than the running version, never a pre-release,
  draft or downgrade, and only if its tag is a plain `vX.Y.Z`. A portable copy or a non-elevated Glint never installs
  (it offers the release page instead).
- **Where it downloads from.** `https://github.com/jleeh/glint/releases/download/v<version>/Glint-Setup-<version>.exe`
  and `SHA256SUMS.txt` from the same release, built by Glint itself (not taken from the API answer). Redirects are
  followed by hand, at most 5, and only to `https://objects.githubusercontent.com` or
  `https://release-assets.githubusercontent.com` (GitHub's asset hosts). Size caps (200 MB setup, 64 KB checksums,
  8 MB attestation data), timeouts and a stall timeout apply.
- **Where it puts it.** A new folder `%ProgramData%\Glint\updates\<random>` that only Administrators and SYSTEM can
  open (inheritance off, owner Administrators, an OWNER RIGHTS entry so a file's owner gets no implicit right to
  change its permissions). `%ProgramData%\Glint` and `...\updates` are refused if they are a junction/link or were
  created by a non-administrator, so a normal program can't pre-create them and swap the download. Never the
  user's TEMP or APPDATA. Old folders are deleted two minutes after Glint starts.
- **What it verifies.** (1) The SHA-256 of the downloaded setup equals the line for exactly that file name in the
  release's `SHA256SUMS.txt`. (2) GitHub has a build attestation for that digest
  (`GET https://api.github.com/repos/jleeh/glint/attestations/sha256:<hex>`, following `bundle_url` to GitHub's
  attestation storage `tmaproduction.blob.core.windows.net` when the bundle isn't inlined) whose in-toto statement has
  the file's digest as subject, predicate type `https://slsa.dev/provenance/v1`, and was produced by workflow
  `.github/workflows/release.yml` of `https://github.com/jleeh/glint` for `refs/tags/v<version>`. Any mismatch
  aborts, deletes the folder and shows why.
- **How it runs it.** The hash is computed again right before launch while the file is held open against writes and
  renames; the setup is started by its absolute path (no search path), with TEMP/TMP pointing into the protected
  folder (Inno Setup unpacks itself there instead of the user's TEMP), with `/SILENT /SUPPRESSMSGBOXES /NORESTART
  /NOCANCEL /MERGETASKS=!disablesignalrgb` (the previous install's tasks are kept; PawnIO / WebView2 are only offered
  when missing). The setup closes Glint and starts it again through its logon task.
- **Automatic install** is off by default. When on, it runs only when a newer release exists, the PC has had no
  keyboard/mouse input for 10 minutes, no fullscreen app or game is in front, and the PC isn't on battery, and it
  tries each version at most once a day (remembered in the admin-only folder), so a failing update never loops.
- **Control pipe.** `update.install` (ctl / MCP) needs `confirm=true` and can only start this same verified flow, so a
  normal-privilege caller can make Glint install the newest official release, nothing else.

**What is not verified.** Glint does not check the Sigstore signature, certificate chain or transparency-log entry
inside the attestation bundle itself. It trusts GitHub: the TLS-protected attestations API of this repository, and
that only this repository's release workflow (running on a `v*` tag, see `release.yml`) can create attestations for
it. Someone who controls the repository or its release workflow, or GitHub itself, could ship a bad update; so could
someone who can break TLS to `api.github.com`/`github.com`. The setup is not Authenticode-signed yet. To verify a
download completely yourself:

```powershell
gh attestation verify Glint-Setup-<version>.exe --repo jleeh/glint
```

Releases made before attestations were added have none, so the updater refuses them (install those from the
release page).

## In scope

- Privilege escalation from a normal-privilege program, plugin file, web content or LAN device to Glint's
  administrator rights (file write/read/delete, code or process execution, DLL loading, SMBus approval bypass).
- Making the updater run anything other than a verified setup of a newer official release (swapping the download,
  redirecting it, bypassing the checksum or attestation check, downgrades).
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
