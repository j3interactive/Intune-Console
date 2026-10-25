# Changelog

Shipped changes to the Intune Troubleshooting Console. Planned work is in [ROADMAP.md](ROADMAP.md).

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
