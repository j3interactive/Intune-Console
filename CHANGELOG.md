# Changelog

Shipped changes to the Intune Troubleshooting Console. Planned work is in [ROADMAP.md](ROADMAP.md).

## v0.3.1 · 2026-10-25

### Added

- **"Why isn't this installing?"** on the device page. Select an app (or double-click it) and the console walks the same gates Intune does, in order:
  1. Assigned: is the device or its primary user in an assigned group, or is it All devices / All users?
  2. Not excluded: exclusion groups on the device or the user.
  3. Intent: Required, Available (needs Company Portal) or Uninstall.
  4. Assignment filters: the filter rule is evaluated against the device (manufacturer, model, name, OS version, ownership, category, enrollment profile). Rules using properties the console can't see are shown for you to check.
  5. Requirements: architecture, minimum Windows release, free disk space and memory. Custom registry, file or script rules are listed.
  6. Dependencies and their install status on the device.
  7. The device's reported result, with the error explained.

  The first failing gate becomes the verdict. Copy as text for a ticket.
- **Check handover readiness** on the device page: Autopilot registration and group tag, check-in in the last 24 hours, compliance, BitLocker, failed or pending apps, primary user, Datto agent, and LAPS backup (date only, no password revealed).
- **Bulk actions** (Bulk action… on the Devices screen). Select devices with Ctrl/Shift-click, or paste names or serial numbers. Actions: sync, collect diagnostics, run an Intune Remediation, restart the IME (Datto), clear the IME download cache (Datto) and restart devices. Shows a live result per device, runs a few devices at a time, can be stopped, and logs every device. Restarting more than five devices asks twice.
- **Primary user checks in the Record health check**, from the last 14 days of PC sign-ins:
  - *Primary user mismatch:* the primary user hasn't signed in, and someone else clearly uses the PC. Suggests who to change it to.
  - *No primary user, one main user:* suggests setting them.

  Shared PCs (no clear main user) aren't flagged.

### Setup needed

- Add `GroupMember.Read.All` and grant admin consent.

### Notes

- 88 automated checks now pass, covering filter evaluation, the app check, readiness, primary user detection and bulk targeting. The Windows UI still hasn't been compiled on Windows.

## v0.3.0 · 2026-10-25

### Added

- **Passwords and keys** on the device page:
  - **Show local admin password (LAPS):** the current Windows LAPS password with account name, backup time and next rotation, plus up to two previous passwords.
  - **Show BitLocker recovery key:** every key backed up for the device, with the Key ID to match against the recovery screen.
  - Both ask for a reason first, record the reveal in the action log, and are recorded by Entra in its audit log. The window closes after two minutes and clears the clipboard if it still holds the copied secret.
- **Device timeline** on the device page (Load timeline): Intune audit events for the device (30 days), the primary user's sign-ins on that device (14 days, repeats grouped), console actions, diagnostics requests and uploads, enrollment, last check-in and last Datto contact. Failures are highlighted.
- **Copy ticket summary** on the device page: device, user, compliance, encryption, Datto status, failed and pending apps with plain-English explanations, policy problems, diagnostics and actions taken.
- **Record health check** screen (new in the sidebar). It compares Intune, Entra ID, Autopilot and Datto and lists:
  - Duplicate Intune records for one serial
  - Intune devices with no check-in for 30+ days
  - Devices in Intune but not Datto, and in Datto but not Intune
  - Enabled Entra devices with no sign-in for 90+ days
  - Entra-joined devices with no Intune record
  - Autopilot devices with no group tag, or registered but never enrolled
  - Intune devices not registered in Autopilot

  Each finding has a suggested fix. Filter by check, export to CSV, or double-click to open the device. Nothing is changed.
- The console now remembers your last 30 days of actions between sessions, so timelines and ticket summaries include them.

### Setup needed

- Add `Device.Read.All`, `DeviceManagementServiceConfig.Read.All`, `DeviceLocalCredential.Read.All` and `BitlockerKey.Read.All`, then grant admin consent.
- LAPS passwords need the Cloud Device Administrator or Intune Administrator role in Entra. BitLocker keys also work with Helpdesk Administrator.

### Notes

- Datto activity isn't in the timeline yet.
- The new logic passed 62 automated checks in total. The Windows UI still hasn't been compiled on Windows.

## v0.2.0 · 2026-10-25

### Added

- **Search people and devices** from a search bar on every screen (Ctrl+K or Ctrl+F). People match on display name, UPN, email, employee ID, job title and department. Devices match on name, primary user, UPN, serial, model and Wi-Fi MAC address. Arrow keys and Enter open a result.
- **User page:**
  - Profile: UPN, email, employee ID, office, mobile, manager, and whether the account is enabled.
  - Devices: every Intune device where they're the primary user, plus devices they signed in to in the last 14 days (any platform). Shows compliance, last check-in, last sign-in and Datto status. Double-click to troubleshoot.
  - Recent sign-ins for the last 14 days: app, device, location, IP and result, with failure reasons and a failures-only filter.
  - Copy UPN and Open in Entra buttons.
- **Primary user is now clickable** on the device page and opens their user page.
- **Device list filter** also matches Wi-Fi MAC address, ignoring separators.

### Changed

- Sign-in now requests all admin-consented Graph permissions (`.default`) instead of a fixed list, so new permissions work as soon as they're consented.

### Setup needed

- Add `User.Read.All` and `AuditLog.Read.All` to the app registration and grant admin consent.
- Give admins who need sign-in history the Reports Reader role in Entra.

### Notes

- Name search matches whole words from the start, a limit of Microsoft's directory search. "jsmi" won't find John Smith; "john" or "smi" will.
- As with v0.1, the Windows UI hasn't been compiled on Windows yet. The new search and sign-in logic passed 42 automated checks.

## v0.1.0 · 2026-10-25

First version.

### Added

- **Sign-in** with each admin's own Microsoft account through the Windows account broker. Calls use delegated permissions, so Intune roles still apply.
- **Device list** of Intune-managed Windows devices: compliance, last check-in, OS version, model, encryption and Datto agent status. Includes filters (noncompliant, stale, not encrypted, Datto offline, not in Datto), search by device, user, UPN, serial or model, and CSV export.
- **Device page:**
  - Intune actions: sync, restart, collect diagnostics.
  - Run any Intune Remediation on demand.
  - Datto actions: restart the Intune Management Extension, clear its download cache, reset a stuck Win32 app, open the device in Datto, start Web Remote.
  - App install status with error codes explained in plain English.
  - Configuration and compliance policy problems.
  - Push a file to the device through Datto, staged in an Azure Blob container and checked with a SHA-256 hash.
- **Log viewer:** downloads and unpacks Intune diagnostics bundles, parses Intune Management Extension (CMTrace) logs and event logs, filters by level and text, and suggests a likely cause with the log line it matched.
- **Settings** stored per admin. Datto keys and storage tokens are kept in Windows Credential Manager.
- **Scripts:** four Datto components and two Intune Remediations.
- **Packaging:** `build.ps1` builds a self-contained exe and an Intune Win32 package with install, uninstall and detection scripts. A GitHub Actions workflow runs the same build.

### Known limitations

- The Windows UI hadn't been compiled when this version was committed. The first build may need fixes.
- Windows devices only.
- No push file through Intune for devices that are offline in Datto.
