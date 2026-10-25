# Changelog

Shipped changes to the Intune Troubleshooting Console. Planned work is in [ROADMAP.md](ROADMAP.md).

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
