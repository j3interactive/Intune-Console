# Roadmap

Planned changes to the Intune Troubleshooting Console. Each item lists what it does, where the data comes from, and anything you need to set up. Shipped changes move to [CHANGELOG.md](CHANGELOG.md).

**Status key:** Planned · In progress · Shipped · Needs decision

---

## v0.2: Find users and their devices

### User search · Planned

Search for a person and land on their devices in one step. Today you have to know the device name.

- One search box at the top of every screen. It matches display name, UPN, email, employee ID, department and job title, and also still matches device name, serial number and asset tag.
- Results show people and devices together. Picking a person opens a **user page**:
  - Name, UPN, job title, department, office, manager, account enabled or disabled
  - Every device where they're the primary user (Intune), plus devices they've signed in to recently
  - Each device's compliance, last check-in and Datto status, with one click into the device page
  - Their recent sign-ins with app, device, location and result (needs Entra ID P1)
- Search starts after 2 characters and works with partial names ("jsmi", "smith").

**Data:** Graph `/users` with `$search` (needs the `ConsistencyLevel: eventual` header), `/users/{id}/managedDevices`, `/users/{id}/registeredDevices`, and `/auditLogs/signIns`.

**Setup:** add delegated permissions `User.Read.All`, `Device.Read.All` and `AuditLog.Read.All` to the app registration and grant admin consent again.

### Better device search · Planned

- Search across all device fields at once: name, serial, user, UPN, model, asset tag, IP address and MAC address.
- Saved filters, for example "Finance laptops not checked in for 7 days".

---

## v0.3: Asset management

### Asset records · Planned

Intune and Datto know what a device *is*, but neither tracks who it was issued to over time, what it cost, or where it is when it's not enrolled. This adds a lightweight asset register.

Each device gets an asset record that combines live data with fields you maintain:

| Live (read only) | Maintained by your team |
| --- | --- |
| Serial, model, manufacturer (Intune) | Asset tag |
| CPU, RAM, disk size and free space (Datto audit) | Status: In use, In stock, Loaner, Repair, Retired, Lost |
| Primary user, last check-in (Intune) | Purchase date, cost, supplier, PO number |
| Autopilot group tag and profile (Intune) | Location, cost center |
| Warranty end date (manufacturer lookup) | Notes |

- **Assignment history:** every time the assigned user changes, the console records who, when and who made the change. This lets you answer "who had this laptop before?"
- **Check out and check in** for loaners and new hires, with a printable handover sheet.
- **Devices without Intune:** spares on a shelf, devices waiting for repair, retired devices. Add them by serial number.
- **Warranty lookup** by serial number for Dell, Lenovo and HP. Each needs a free API key from the manufacturer.
- **Import and export** to CSV, to load what you track in spreadsheets today.
- **Reports:** devices out of warranty in the next 90 days, devices older than 4 years, stock on hand, lost or unreturned devices.

**Shared storage:** asset records have to be shared by all 4–5 admins, which the desktop app can't do on its own. The plan is a **SharePoint list** that the app reads and writes through Graph:

- Included in Microsoft 365, so no extra cost and nothing to host.
- Admins can also see and edit it in SharePoint or Excel.
- Access is controlled with a SharePoint group, and SharePoint keeps version history.

**Setup:** create a SharePoint site (or use an existing IT one). Add the delegated permission `Sites.Selected` and grant the app access to that one site, which is a single PowerShell command included in the guide.

**Needs decision:** if you already track assets somewhere (a spreadsheet, a PSA like Autotask, an existing list), the console can read from and write to that instead. Tell me where it lives.

---

## v0.4: Remote control and PowerShell

### Remote control · Planned

Datto already does this well, so the console will hand off to Datto instead of building its own.

- **Connect** button on every device page and in user search results, which opens Datto Web Remote for that device. (v0.1 already has a basic "Start web remote" button on the device page; this makes it available everywhere.)
- Datto Web Remote includes screen control, a remote PowerShell or terminal session, and file transfer. A full interactive shell stays there.
- Remote sessions are logged in Datto's activity log under the admin's own Datto account.

### Run PowerShell from the console · Planned

For quick, one-off commands without opening a full remote session:

- A PowerShell box on the device page. Type or paste a command, run it, and the output appears in the console, usually within 10–30 seconds when the device is online.
- It runs through a Datto component that takes the script as a variable and runs it as System. The console then reads the job's output through the Datto API.
- **Snippet library:** saved, reviewed commands such as "free disk space", "last reboot", "pending reboot", "installed apps", "dsregcmd /status" and "gpresult". Each can be run with one click.
- Every command and its output is saved to the action log.
- Admins can choose to allow only saved snippets and turn off free-form commands.

This is not an interactive shell. Each run is a separate job, so it can't keep state between commands or answer prompts. Use Datto Web Remote for that.

**To verify before building:** the exact Datto API call for reading a job's output (stdout/stderr) for a device.

---

## v0.5: Features Intune and Datto don't offer

These fill gaps that come up in day-to-day troubleshooting. Each is marked with how much it would help a small team. **Pick what you want and in what order.**

### Help most

- **Device timeline.** One chronological view per device that combines Intune events (enrollment, policy and app changes, remote actions), Datto activity (jobs, alerts, remote sessions), user sign-ins and the console's own actions. It answers "what changed before it broke?" Today this is spread across three portals.
- **One-click LAPS password and BitLocker key.** Show the local admin password or BitLocker recovery key on the device page, with a reason prompt and a logged reveal. Both exist in the Intune portal but take several clicks to find. Needs `DeviceLocalCredential.Read.All` and `BitLockerKey.Read.All`.
- **Record reconciliation.** Find devices that are out of sync between Intune, Entra ID, Autopilot and Datto: in Datto but not Intune, duplicate Intune records for one serial, stale Entra devices, Autopilot devices with no group tag. Includes a cleanup list, and nothing is deleted without confirmation.
- **"Why didn't this app install?" check.** For one app and one device, walk through the real reasons: the device isn't in the assigned group, an assignment filter excludes it, a requirement rule failed, a dependency failed, or it's waiting in the IME retry schedule. Intune shows the result but rarely the reason.
- **Ticket summary export.** One button that produces a short text summary (device, user, health, failing apps or policies, recent actions, likely cause) to paste into a ticket or Teams.

### Useful

- **Primary user mismatch.** Flag devices where the person signing in every day isn't the Intune primary user. This breaks user-targeted apps and self-service.
- **Health score.** One number per device from compliance, last check-in, disk space, pending reboot, failing apps and Datto alerts. The device list can sort by it so the worst devices come first.
- **Bulk actions.** Select many devices, or paste a list of names or serials, and sync, collect diagnostics, run a remediation or run a Datto component on all of them, with a progress view.
- **Offboarding checklist.** For a leaver: list their devices, mark them for return, set the asset status, retire or wipe once returned, and record who did each step.
- **New device readiness.** Before handing over a device: Autopilot registered, correct group tag, enrolled, compliant, required apps installed, BitLocker on. Shown as one green or red checklist.

### Later

- **Shared notes per device and user** (needs the SharePoint list from v0.3).
- **Alerts in Teams** for devices that drop out of compliance or stop checking in.
- **Push file via Intune** for devices offline in Datto. Intune's on-demand remediation can't take inputs, so this would need a different approach.

---

## Permissions summary

New delegated Graph permissions these features need, added only when a feature ships:

| Permission | Used for | Version |
| --- | --- | --- |
| `User.Read.All` | User search and user page | v0.2 |
| `Device.Read.All` | Entra device records, registered devices | v0.2 |
| `AuditLog.Read.All` | Recent sign-ins (needs Entra ID P1) | v0.2 |
| `Sites.Selected` | Asset register in one SharePoint site | v0.3 |
| `DeviceManagementServiceConfig.Read.All` | Autopilot group tag and profile | v0.3 |
| `DeviceLocalCredential.Read.All` | LAPS password reveal | v0.5 |
| `BitLockerKey.Read.All` | BitLocker key reveal | v0.5 |

All of them are delegated, so each admin can still only see and do what their own Entra and Intune roles allow.

---

## Open questions

1. Where do you track assets today, if anywhere?
2. Do you have Entra ID P1 or P2? Sign-in history on the user page needs it.
3. Which manufacturers make most of your devices (for warranty lookup)?
4. Should free-form PowerShell be available to every admin, or only saved snippets for some?
5. From the v0.5 list, what's most useful to you first?
