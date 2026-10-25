# Intune Troubleshooting Console

A Windows desktop app for your admin team that puts Intune troubleshooting and Datto RMM actions on one screen:

- **Search people and devices** from anywhere (Ctrl+K): name, UPN, email, employee ID, job title or department for people, and device name, serial or MAC for devices. A **user page** shows the person's details, every device they use, and their recent sign-ins.
- **Device list** of Intune-managed Windows devices with compliance, last check-in, encryption and Datto agent status, plus filters and CSV export.
- **Device page** with Intune actions (sync, restart, collect diagnostics), Intune Remediations run on demand, Datto actions (restart the Intune Management Extension, clear its download cache, reset a stuck Win32 app, open the device in Datto), app install errors explained in plain English, policy conflicts, and **push a file** to the device.
- **Passwords and keys:** reveal a device's LAPS local admin password or BitLocker recovery key in one click, with a reason prompt and a logged reveal.
- **Device timeline** that merges Intune changes and actions, the user's sign-ins on that device, console actions and check-ins into one list, plus a **ticket summary** you can paste into a ticket.
- **Record health check** that compares Intune, Entra ID, Autopilot and Datto and lists duplicates, stale records and devices missing from one of them.
- **Log viewer** that downloads and unpacks Intune diagnostics bundles, parses the Intune Management Extension logs, highlights errors and suggests a likely cause with the log line it came from.

Each admin signs in with their own Microsoft account and their own Datto API key. Intune's RBAC still decides what each person can do, and both Intune's and Datto's audit logs show who did what. Nothing extra is installed on managed devices.

See [ROADMAP.md](ROADMAP.md) for planned features and [CHANGELOG.md](CHANGELOG.md) for what has shipped.

Setup takes about 30–45 minutes the first time. Steps 1, 4 and 5 are required. Steps 2 and 3 can wait.

---

## 1. Create the Entra app registration (required, ~10 min)

In the [Entra admin center](https://entra.microsoft.com) go to **Identity > Applications > App registrations > New registration**.

1. **Name:** `Intune Troubleshooting Console`
2. **Supported account types:** Accounts in this organizational directory only.
3. Leave the redirect URI empty for now and click **Register**.
4. Copy the **Application (client) ID** and **Directory (tenant) ID** from the Overview page.
5. **Authentication > Add a platform > Mobile and desktop applications.** Add both of these redirect URIs (replace the GUID with your client ID):
   - `http://localhost`
   - `ms-appx-web://microsoft.aad.brokerplugin/<your-client-id>`

   Leave "Allow public client flows" set to **No**; the app doesn't need it.
6. **API permissions > Add a permission > Microsoft Graph > Delegated permissions.** Add:
   - `DeviceManagementManagedDevices.ReadWrite.All` (device list, diagnostics)
   - `DeviceManagementManagedDevices.PrivilegedOperations.All` (sync, restart, run remediations)
   - `DeviceManagementApps.Read.All` (app install status)
   - `DeviceManagementConfiguration.Read.All` (policy states, list remediations)
   - `User.Read.All` (people search and the user page, added in v0.2)
   - `AuditLog.Read.All` (recent sign-ins on the user page, added in v0.2)
   - `Device.Read.All` and `DeviceManagementServiceConfig.Read.All` (record health check: Entra and Autopilot records, added in v0.3)
   - `DeviceLocalCredential.Read.All` and `BitlockerKey.Read.All` (reveal LAPS passwords and BitLocker keys, added in v0.3)
7. Click **Grant admin consent for <your tenant>**.

   The app asks for whatever permissions have admin consent, so when a new version needs another permission, adding it here and granting consent is all it takes. Until then, only the feature that needs it shows a message.

   **Sign-ins also need an Entra role:** admins who should see a user's sign-in history need **Reports Reader** (or Global Reader or Security Reader). Without it, the rest of the user page still works.

   **Passwords and keys also need an Entra role.** Microsoft only returns LAPS passwords to admins with **Cloud Device Administrator** or **Intune Administrator**. BitLocker keys are also available to **Helpdesk Administrator**. Use Privileged Identity Management for these roles if you have it. Every reveal is recorded in Entra's audit log.
8. Recommended: in **Enterprise applications**, open the same app, go to **Properties**, set **Assignment required** to **Yes**, then under **Users and groups** add your admins group. Only those people can sign in. Your normal Conditional Access policies (MFA, compliant device) apply.

These are *delegated* permissions: the app can only do what the signed-in admin's Intune role allows. A helpdesk role without restart rights can't restart a device through the console either. Admins need Intune role permissions for the remote tasks they'll use (sync, restart, collect diagnostics, run remediations).

## 2. Upload the fix-it Remediations (optional, ~5 min)

These let you restart the Intune Management Extension or clear its cache through Intune when a device isn't reachable in Datto. Remediations need Windows Enterprise E3/E5 (or equivalent) licensing.

In the [Intune admin center](https://intune.microsoft.com) go to **Devices > Scripts and remediations > Remediations > Create**, once for each folder in `scripts\remediations`:

| Name | Detection script | Remediation script |
| --- | --- | --- |
| Intune Console - Restart IME | `Restart-IME\Detect.ps1` | `Restart-IME\Remediate.ps1` |
| Intune Console - Clear IME Download Cache | `Clear-IMEDownloadCache\Detect.ps1` | `Clear-IMEDownloadCache\Remediate.ps1` |

Settings for both: **Run this script using the logged-on credentials: No**, **Enforce script signature check: No** (unless you sign them), **Run script in 64-bit PowerShell: Yes**.

**Don't assign them to any groups.** They're built to run on demand from the console. Assigned on a schedule, they would restart the service every time.

The console lists every Remediation in your tenant, so your existing ones can be run on demand too.

## 3. Datto RMM (optional but recommended, ~10 min)

**Enable API access** (once, by a Datto admin): **Setup > Global Settings > Access Control > Enable API Access**.

**Each admin generates their own key:** **Setup > Users >** click your user **> Generate API Keys**. Copy the API key, the secret, and the API URL shown on that page (for example `https://merlot-api.centrastage.net`). The secret can't be shown again after you leave the page.

**Import the four components** (once): **Automation > Components > New Component**, category **Scripts**, script type **PowerShell**. Paste each script from `scripts\datto` and use these exact names (or change the names in the console's Settings to match yours):

| Component name | Script | Variables to add (all type String) |
| --- | --- | --- |
| Intune Console - Restart IME | `Restart-IME.ps1` | none |
| Intune Console - Clear IME Download Cache | `Clear-IMEDownloadCache.ps1` | none |
| Intune Console - Reset Win32 App | `Reset-Win32App.ps1` | `AppId` |
| Intune Console - Push File | `Push-File.ps1` | `FileUrl`, `FileName`, `Destination`, `Sha256`, `Overwrite` |

The console matches Intune devices to Datto devices by hostname.

## 4. Build the app (required, ~5 min)

On any Windows machine with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```powershell
cd IntuneConsole
.\build.ps1
```

You get:

- `out\publish\IntuneConsole.exe`: the app. Run it directly to try it before deploying.
- `out\IntuneConsole.intunewin`: the Intune package.
- `out\Detect.ps1`: the detection script for Intune.

To code-sign the exe with a certificate in your store, add `-CertThumbprint <thumbprint>`. Unsigned, SmartScreen may warn on first run, and WDAC/AppLocker policies need an exception.

**No Windows build machine handy?** Push this folder to a GitHub repo. The included workflow (`.github/workflows/build.yml`) builds it on GitHub's Windows runners, and you can download the package from the run's **Artifacts**.

**Pre-fill settings for everyone (optional):** copy `packaging\defaults.example.json` to `packaging\defaults.json`, fill in your tenant ID, client ID, Datto API URL and file drop URL, then build. Admins then only enter their own Datto key.

## 5. Deploy with Intune (required, ~5 min)

**Apps > Windows > Add > Windows app (Win32)**, upload `out\IntuneConsole.intunewin`:

- **Install command:** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1`
- **Uninstall command:** `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall.ps1`
- **Install behavior:** System
- **Detection rules:** Use a custom detection script → upload `out\Detect.ps1`
- **Assignments:** Required, to your admins group.

It installs to `C:\Program Files\IntuneConsole` with a Start Menu shortcut. For a new version, run `.\build.ps1 -Version 0.4.0` and supersede or update the app; the detection script checks the version.

## 6. File drop for "Push file" (optional, ~10 min)

Datto components can't receive a file at run time, so the console stages the file in an Azure Blob container and the Push File component downloads it. Cost is pennies a month.

1. Create a **Storage account** (Standard, LRS) and a **private** container, e.g. `intune-console-drop`.
2. Add a **lifecycle management** rule that deletes blobs older than 1 day. The console deletes files after a successful push, and this catches anything left over.
3. On the container, create two **stored access policies** so you can revoke tokens later:
   - `upload` with **Create, Write, Delete**
   - `download` with **Read**

   Give both an expiry, e.g. 6 months.
4. **Generate SAS** for the container from each policy. You get two tokens starting `sp=…` or `si=…`.
5. In the console's **Settings > File drop**, enter the container URL (`https://<account>.blob.core.windows.net/intune-console-drop`) and paste both tokens.

Devices only ever get the read-only token, inside a link to one file. Every file's SHA-256 hash is checked on the device before it's saved. Devices need outbound HTTPS to `*.blob.core.windows.net`.

## First run

1. Open the app. On first launch it shows Settings: enter the tenant and client IDs (pre-filled if you used `defaults.json`), plus your Datto details if ready.
2. Click **Save**. Windows asks you to sign in (often silently with your current account), then the device list loads.
3. Double-click a device to troubleshoot it.

**Collecting logs:** on a device page click **Collect diagnostics**. The console also sends a sync to speed things up, then checks every 30 seconds. When the device has uploaded, click **Open latest diagnostics**. The bundle is downloaded to `%LOCALAPPDATA%\IntuneConsole\Diagnostics` and opened in the log viewer. You can also open any bundle zip you downloaded from the Intune portal with **Diagnostics > Open a bundle**.

## If something goes wrong on the first run

| Message | Fix |
| --- | --- |
| `AADSTS50011` redirect URI mismatch | Check both redirect URIs in step 1.5, with your exact client ID. |
| `AADSTS65001` consent required | Grant admin consent in step 1.7. |
| `AADSTS50105` not assigned | Add the admin to the enterprise app's users and groups (step 1.8). |
| "Access denied by Intune" | The admin's Intune role is missing that permission. |
| "Datto sign-in failed" | Check the API URL (it's platform-specific) and regenerate the key if unsure. |
| "Datto component … wasn't found" | The name in Settings must match the component name exactly. |
| App status says "Couldn't load" | Intune's per-device app report is a beta API; send me the exact message. |
| People search shows "Access denied" | Add `User.Read.All` and grant admin consent (step 1.6–1.7). |
| User page says sign-ins need a permission | Add `AuditLog.Read.All` with consent, and give the admin the Reports Reader role. |
| LAPS or BitLocker says access denied | Add the permission with consent, and check the admin's Entra role (see step 1.7). |
| "No LAPS password is backed up" | No Windows LAPS policy targets the device yet, or it hasn't rotated since the policy arrived. |
| Health check skips Entra or Autopilot | Add `Device.Read.All` / `DeviceManagementServiceConfig.Read.All` with consent. |
| Can't find someone by a partial name | Name search matches whole words from the start ("john", "smi"), not the middle of a word. UPN, email and employee ID match from the start. |

If the build fails, copy the error lines and send them over.

## Security notes

- No client secret exists anywhere: the app registration is a public client using delegated permissions.
- Datto keys and storage tokens are stored per Windows user in Credential Manager, never in files.
- Restarts, app resets and file pushes ask for confirmation.
- The app keeps a local record of each admin's actions in `%LOCALAPPDATA%\IntuneConsole\actions.jsonl`. Intune's audit log and Datto's activity log are the authoritative records.
- The Datto API key's security level controls what that admin can do in Datto. Use the least privileged level that can run components.

## What's not in version 0.1

- Push file via Intune (for devices offline in Datto). Intune's on-demand remediation can't take per-run inputs.
- Bulk actions across many devices.
- Shared notes between admins.
- macOS, iOS and Android devices (the list shows Windows only).

## Project layout

```
src/IntuneConsole.Core    Graph, Datto, log parsing, likely-cause rules (no Windows dependencies)
src/IntuneConsole.App     WPF app: sign-in, views, Credential Manager, event log reader
scripts/datto             Datto components
scripts/remediations      Intune Remediations
packaging                 Install / Uninstall / Detect scripts, defaults example
build.ps1                 Build + package
```
