# Roadmap

Planned changes to the Intune Troubleshooting Console. Each item lists what it does, where the data comes from, and anything you need to set up. Shipped changes move to [CHANGELOG.md](CHANGELOG.md).

**Status key:** Planned · In progress · Shipped · Needs decision

---

## v0.2: Find users and their devices

### User search · Shipped in v0.2.0

Search for a person and land on their devices in one step. Today you have to know the device name.

- One search box at the top of every screen. It matches display name, UPN, email, employee ID, department and job title, and also still matches device name, serial number and asset tag.
- Results show people and devices together. Picking a person opens a **user page**:
  - Name, UPN, job title, department, office, manager, account enabled or disabled
  - Every device where they're the primary user (Intune), plus devices they've signed in to recently
  - Each device's compliance, last check-in and Datto status, with one click into the device page
  - Their recent sign-ins with app, device, location and result (you have Entra ID P1, so this is included)
- Search starts after 2 characters. Names match whole words from the start ("john", "smi"), because of how Microsoft's directory search works; UPN, email and employee ID match from the start too.

**Data:** Graph `/users` with `$search` (needs the `ConsistencyLevel: eventual` header), `/users/{id}/managedDevices`, `/users/{id}/registeredDevices`, and `/auditLogs/signIns`.

**Setup:** add delegated permissions `User.Read.All` and `AuditLog.Read.All` to the app registration and grant admin consent again. Admins who should see sign-ins also need the **Reports Reader** (or Global Reader / Security Reader) role in Entra.

### Better device search · Partly shipped in v0.2.0

- Search across all device fields at once: name, serial, user, UPN, model and Wi-Fi MAC are done. Asset tag comes with v0.3. IP address isn't in Intune's device list, so it would come from Datto later.
- Saved filters, for example "Finance laptops not checked in for 7 days". (Moved to v0.3, alongside the asset fields it would filter on.)

---

## v0.4: Asset management

### Asset records · Shipped in v0.4.0

Intune and Datto know what a device *is*, but neither tracks who it was issued to over time, what it cost, or where it is when it's not enrolled. This adds a lightweight asset register.

Each device gets an asset record that combines live data with fields you maintain:

| Live (read only) | Maintained by your team |
| --- | --- |
| Serial (Dell service tag), model (Intune) | Asset tag |
| CPU, RAM, disk size and free space (Datto audit) *(not yet)* | Status: In use, In stock, Loaner, Repair, Retired, Lost |
| Primary user, last check-in (Intune) | Purchase date, cost, supplier, PO number |
| Autopilot group tag and profile (Intune) *(not yet; shown in the readiness check)* | Location, cost center |
| Dell warranty: ship date, end date, support level (Dell API) | Notes |

- **Assignment history:** every time the assigned user changes, the console records who, when and who made the change. This lets you answer "who had this laptop before?"
- **Check out and check in** for loaners and new hires, with a printable handover sheet.
- **Devices without Intune:** spares on a shelf, devices waiting for repair, retired devices. Add them by serial number.
- **Dell warranty lookup** by service tag (all your devices are Dell). It uses Dell's free Warranty API through TechDirect: register at [techdirect.dell.com](https://techdirect.dell.com), request API access, and Dell issues a client ID and secret, usually within a few days. One key for the whole team, stored in each admin's Credential Manager. The console looks up warranties in batches and caches the results in the asset list, so it isn't calling Dell on every screen.
- **Starting from scratch:** since nothing is tracked today, the first run builds the register for you. It creates one record per Intune device with live data and warranty already filled in, so your team only adds asset tags and statuses. CSV import and export are still there for bulk edits in Excel.
- **Reports:** devices out of warranty in the next 90 days, devices older than 4 years, stock on hand, lost or unreturned devices.

**Shared storage:** asset records have to be shared by all 4–5 admins, which the desktop app can't do on its own. The plan is a **SharePoint list** that the app reads and writes through Graph:

- Included in Microsoft 365, so no extra cost and nothing to host.
- Admins can also see and edit it in SharePoint or Excel.
- Access is controlled with a SharePoint group, and SharePoint keeps version history.

**Setup:** create a SharePoint site (or use an existing IT one). Add the delegated permission `Sites.Selected` and grant the app access to that one site, which is a single PowerShell command included in the guide.

**Decided:** nothing is tracked today, so the SharePoint list is the system of record.

**Not done yet in v0.4:** hardware details from Datto's audit on the asset window, the Autopilot group tag on the asset record, and saved custom filters (the built-in views cover the common reports).

---

## v0.5: Remote control and PowerShell

### Remote control · Shipped in v0.5.0

Datto already does this well, so the console will hand off to Datto instead of building its own.

- **Connect** button on every device page and in user search results, which opens Datto Web Remote for that device. (v0.1 already has a basic "Start web remote" button on the device page; this makes it available everywhere.)
- Datto Web Remote includes screen control, a remote PowerShell or terminal session, and file transfer. A full interactive shell stays there.
- Remote sessions are logged in Datto's activity log under the admin's own Datto account.

### Run PowerShell from the console · Shipped in v0.5.0

For quick, one-off commands without opening a full remote session:

- A PowerShell box on the device page. Type or paste a command, run it, and the output appears in the console, usually within 10–30 seconds when the device is online.
- It runs through a Datto component that takes the script as a variable and runs it as System. The console then reads the job's output through the Datto API.
- **Snippet library:** saved, reviewed commands such as "free disk space", "last reboot", "pending reboot", "installed apps", "dsregcmd /status" and "gpresult". Each can be run with one click.
- Every command and its output is saved to the action log.
- **Decided:** every admin can run free-form commands as well as saved snippets. Datto's own security level on each admin's API key still applies.

This is not an interactive shell. Each run is a separate job, so it can't keep state between commands or answer prompts. Use Datto Web Remote for that.

**Still to confirm:** the console reads job output from `/v2/job/{job}/results/{device}/stdout` (not in Datto's public docs). Confirm on the first real run.

---

## v0.3 and later: Features Intune and Datto don't offer

These fill gaps that come up in day-to-day troubleshooting. You asked for these ahead of asset management, so the first batch shipped as **v0.3.0**.

### Help most

- **Device timeline · Shipped in v0.3.0.** One chronological view per device: Intune audit events (changes and remote actions, 30 days), the primary user's sign-ins on that device (14 days), the console's own actions, diagnostics, enrollment and check-ins. *Still to add:* Datto activity (jobs, alerts, remote sessions), once the Datto activity API is confirmed.
- **One-click LAPS password and BitLocker key · Shipped in v0.3.0.** On the device page, with a reason prompt, a logged reveal and a window that closes itself and clears the clipboard.
- **Record reconciliation · Shipped in v0.3.0** as the *Record health check* screen. Checks: duplicate Intune records per serial, Intune devices not checking in, Intune vs Datto both ways, stale Entra devices, Entra-joined devices missing from Intune, Autopilot devices with no group tag or never enrolled, and Intune devices not registered in Autopilot. Read-only, with CSV export.
- **Ticket summary export · Shipped in v0.3.0.**
- **"Why didn't this app install?" check · Shipped in v0.3.1.** For one app and one device, walk through the real reasons: the device isn't in the assigned group, an assignment filter excludes it, a requirement rule failed, a dependency failed, or it's waiting in the IME retry schedule. Intune shows the result but rarely the reason.

### Useful

- **Primary user mismatch · Shipped in v0.3.1** (in the Record health check). Flag devices where the person signing in every day isn't the Intune primary user. This breaks user-targeted apps and self-service.
- **Health score · Shipped in v0.5.0** (without pending reboot, failing apps and Datto alerts, which need a per-device call; they could be added to the device page). One number per device from compliance, last check-in, disk space, pending reboot, failing apps and Datto alerts. The device list can sort by it so the worst devices come first.
- **Bulk actions · Shipped in v0.3.1.** Select many devices, or paste a list of names or serials, and sync, collect diagnostics, run a remediation or run a Datto component on all of them, with a progress view.
- **Offboarding checklist · Shipped in v0.5.0.** For a leaver: list their devices, mark them for return, set the asset status, retire or wipe once returned, and record who did each step.
- **New device readiness · Shipped in v0.3.1.** Before handing over a device: Autopilot registered, correct group tag, enrolled, compliant, required apps installed, BitLocker on. Shown as one green or red checklist.

### Later

- **Shared notes per device and user** (needs the SharePoint list from v0.4).
- **Alerts in Teams** for devices that drop out of compliance or stop checking in.
- **Push file via Intune** for devices offline in Datto. Intune's on-demand remediation can't take inputs, so this would need a different approach.

---

## Permissions summary

New delegated Graph permissions these features need, added only when a feature ships:

| Permission | Used for | Version |
| --- | --- | --- |
| `User.Read.All` | User search and user page | v0.2 |
| `AuditLog.Read.All` | Recent sign-ins (Entra ID P1, plus Reports Reader role) | v0.2 |
| `Device.Read.All` | Entra device records for the health check | v0.3 |
| `DeviceManagementServiceConfig.Read.All` | Autopilot records for the health check | v0.3 |
| `DeviceLocalCredential.Read.All` | LAPS password reveal (plus an Entra role, see README) | v0.3 |
| `BitlockerKey.Read.All` | BitLocker key reveal (plus an Entra role, see README) | v0.3 |
| `Sites.Selected` | Asset register in one SharePoint site | v0.4 |

All of them are delegated, so each admin can still only see and do what their own Entra and Intune roles allow.

---

## Decisions

- **Devices:** all Dell, so warranty lookup uses Dell's API only.
- **Licensing:** Entra ID P1, so sign-in history is included on the user page.
- **Asset tracking:** none today; the SharePoint list becomes the register.
- **PowerShell:** every admin can run free-form commands.
- **Order:** extra features first (v0.3), then asset management (v0.4), then remote control and PowerShell (v0.5).

## Open questions

Everything planned through v0.5 has shipped. What's left:

- **First real build and test on Windows.** The most valuable next step: compile, sign in, and fix what the live tenant, Datto and SharePoint turn up.
- Datto activity (jobs, alerts, remote sessions) in the device timeline.
- Datto hardware audit (CPU, RAM, disk) and the Autopilot group tag on the asset window.
- Shared notes per device and user (in the SharePoint site).
- Teams alerts for devices that drop out of compliance or stop checking in.
- Saved custom filters.
- Push file via Intune for devices offline in Datto.

1. Which of these do you want after the first test build?
