using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using IntuneConsole.App.Services;
using IntuneConsole.Core.Diagnostics;
using IntuneConsole.Core.Logs;
using IntuneConsole.Core.Models;
using IntuneConsole.Core.Settings;
using Microsoft.Win32;

namespace IntuneConsole.App.Views;

public partial class DeviceDetailView : UserControl
{
    private ManagedDevice _device;
    private List<PolicyState> _policies = new();
    private List<AppInstallStatus>? _apps;
    private List<LogCollectionRequest> _diagnostics = new();
    private LogCollectionRequest? _latestDiag;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateTimeOffset _pollUntil;
    private bool _loaded;

    private static Session S => Session.Current;

    public DeviceDetailView(ManagedDevice device)
    {
        InitializeComponent();
        _device = device;
        _poll.Tick += async (_, _) => await PollDiagnosticsAsync();

        var actions = new ListCollectionView(S.ActionLog)
        {
            Filter = o => o is ActionLogEntry a && a.DeviceName.Equals(_device.DeviceName, StringComparison.OrdinalIgnoreCase)
        };
        ActionsList.ItemsSource = actions;

        RenderHeader();
    }

    private void RenderHeader()
    {
        var d = _device;
        CrumbText.Text = $"/ {d.DeviceName}";
        NameText.Text = d.DeviceName;
        CompliancePill.Tag = d.ComplianceText;
        ComplianceText.Text = d.ComplianceText;
        ComplianceText.Foreground = (System.Windows.Media.Brush)new Converters.PillForegroundConverter()
            .Convert(d.ComplianceText, typeof(object), null, System.Globalization.CultureInfo.CurrentCulture);

        var dattoLabel = d.Datto is null ? "Not in Datto" : d.Datto.Online ? "Datto agent online" : "Datto agent offline";
        DattoPill.Tag = dattoLabel;
        DattoText.Text = dattoLabel;
        DattoText.Foreground = (System.Windows.Media.Brush)new Converters.PillForegroundConverter()
            .Convert(dattoLabel, typeof(object), null, System.Globalization.CultureInfo.CurrentCulture);

        InfoPanel.Children.Clear();
        AddInfo("Primary user", d.UserDisplayName ?? d.UserPrincipalName ?? "None",
            string.IsNullOrWhiteSpace(d.UserId) || d.UserId == Guid.Empty.ToString() ? null : OpenPrimaryUser);
        AddInfo("Model", string.Join(" ", new[] { d.Manufacturer, d.Model }.Where(s => !string.IsNullOrWhiteSpace(s))));
        AddInfo("OS version", d.OsVersion);
        AddInfo("Serial", d.SerialNumber);
        AddInfo("Join / enrollment", string.Join(" · ", new[] { d.JoinType, d.EnrollmentType }.Where(s => !string.IsNullOrWhiteSpace(s))));
        AddInfo("Last Intune check-in", d.LastSyncDateTime is null ? "Never" : $"{d.LastSyncText} ({d.LastSyncDateTime.Value.ToLocalTime():g})");
        AddInfo("Encryption", d.IsEncrypted switch { true => "Encrypted", false => "Not encrypted", _ => "Unknown" });
        if (S.Assets is not null)
        {
            var asset = S.FindAsset(d.SerialNumber);
            if (S.AssetRecords is null) AddInfo("Asset", "Loading…");
            else if (asset is null) AddInfo("Asset", "Not in register · add", OpenAsset);
            else AddInfo("Asset", string.Join(" · ", new[]
                {
                    asset.AssetTag, asset.Status,
                    asset.WarrantyEnd is null ? null : (asset.WarrantyEnd < DateTimeOffset.UtcNow ? "warranty expired" : $"warranty to {asset.WarrantyEnd.Value.ToLocalTime():yyyy-MM-dd}"),
                }.Where(s => !string.IsNullOrWhiteSpace(s))), OpenAsset);
        }
        if (d.Datto is not null)
            AddInfo("Datto last seen", d.Datto.LastSeen is null ? "Unknown" : $"{Humanize.Ago(d.Datto.LastSeen)} · {d.Datto.SiteName}");

        // Datto actions only make sense when the device is matched in Datto.
        var dattoReady = S.Datto is not null && d.Datto is not null;
        DattoActions.IsEnabled = dattoReady;
        DattoNote.Visibility = dattoReady && d.Datto!.Online ? Visibility.Collapsed : Visibility.Visible;
        DattoNote.Text = S.Datto is null ? "Datto isn't set up yet. Add your API details in Settings."
            : d.Datto is null ? "No Datto device with this hostname was found."
            : "The agent is offline. Jobs will wait until it's back online.";
        OpenDattoButton.IsEnabled = !string.IsNullOrWhiteSpace(d.Datto?.PortalUrl);
        WebRemoteButton.IsEnabled = !string.IsNullOrWhiteSpace(d.Datto?.WebRemoteUrl);
    }

    private void AddInfo(string label, string? value, Action? onClick = null)
    {
        var panel = new StackPanel { Width = 220, Margin = new Thickness(0, 0, 16, 14) };
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label") });
        if (onClick is not null)
        {
            // Clickable value, e.g. the primary user opens their user page.
            var link = new Button { Content = value, Style = (Style)FindResource("LinkButton"), FontWeight = FontWeights.Medium,
                                    Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            link.Click += (_, _) => onClick();
            panel.Children.Add(link);
            InfoPanel.Children.Add(panel);
            return;
        }
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "–" : value,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        InfoPanel.Children.Add(panel);
    }

    private void OpenAsset()
    {
        var record = S.FindAsset(_device.SerialNumber);
        new AssetWindow(new IntuneConsole.Core.Models.AssetRow { Record = record, Device = _device }).ShowDialog();
        RenderHeader();
    }

    private void OpenPrimaryUser()
    {
        // The user page loads full details itself; seed it with what the device already knows.
        Shell.Navigate(new UserView(new DirectoryUser
        {
            Id = _device.UserId!,
            DisplayName = _device.UserDisplayName ?? _device.UserPrincipalName ?? "User",
            UserPrincipalName = _device.UserPrincipalName,
        }));
    }

    // ---------------- Loading ----------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) { if (_latestDiag?.IsPending == true) _poll.Start(); return; }
        _loaded = true;
        await Shell.Run($"Loading {_device.DeviceName}…", RefreshAsync);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _poll.Stop();

    private async Task RefreshAsync()
    {
        var intune = S.Intune ?? throw new InvalidOperationException("Sign-in isn't set up yet.");
        var id = _device.Id;

        var deviceTask = intune.GetDeviceAsync(id);
        var appsTask = intune.GetAppStatusAsync(id);
        var policyTask = intune.GetPolicyStatesAsync(id);
        var remTask = intune.GetRemediationsAsync();
        var diagTask = intune.GetDiagnosticsAsync(id);

        var problems = new List<string>();

        try
        {
            var fresh = await deviceTask;
            fresh.Datto = _device.Datto;
            _device = fresh;
            RenderHeader();
        }
        catch (Exception ex) { problems.Add($"device details ({ex.Message})"); }

        try
        {
            var apps = await appsTask;
            _apps = apps;
            AppsGrid.ItemsSource = apps;
            var failed = apps.Count(a => a.IsFailed);
            var pending = apps.Count(a => a.IsPending);
            AppsSummary.Text = apps.Count == 0 ? "No app assignments reported" : $"{failed} failed · {pending} pending · {apps.Count} total";
        }
        catch (Exception ex) { problems.Add($"app status ({ex.Message})"); AppsSummary.Text = "Couldn't load app status"; }

        try
        {
            _policies = await policyTask;
            ShowPolicies();
        }
        catch (Exception ex) { problems.Add($"policies ({ex.Message})"); }

        try
        {
            var scripts = await remTask;
            RemediationCombo.ItemsSource = scripts;
            var preferred = scripts.FirstOrDefault(s => s.DisplayName.Contains("IME", StringComparison.OrdinalIgnoreCase)
                                                      || s.DisplayName.Contains("Management Extension", StringComparison.OrdinalIgnoreCase));
            RemediationCombo.SelectedItem = preferred ?? scripts.FirstOrDefault();
            if (scripts.Count == 0) RemediationNote.Text = "No Remediations found in your tenant yet. Upload the ones in scripts\\remediations (see the setup guide).";
        }
        catch (Exception ex) { RemediationNote.Text = $"Couldn't list Remediations: {ex.Message}"; }

        try
        {
            var diags = await diagTask;
            _diagnostics = diags;
            _latestDiag = diags.FirstOrDefault();
            UpdateDiagUi();
            if (_latestDiag?.IsPending == true) StartPolling();
        }
        catch (Exception ex) { DiagStatusText.Text = $"Couldn't check diagnostics: {ex.Message}"; }

        if (S.Assets is not null && S.AssetRecords is null)
        {
            try { await S.LoadAssetsAsync(); } catch (Exception ex) { problems.Add($"asset register ({ex.Message})"); }
            RenderHeader();
        }

        if (problems.Count > 0)
            Shell.Status("Some sections didn't load: " + string.Join("; ", problems), isError: true);
        else
            Shell.Status($"{_device.DeviceName} loaded.");
    }

    private void ShowPolicies()
    {
        var showAll = ShowAllPolicies.IsChecked == true;
        var list = showAll ? _policies : _policies.Where(p => p.IsProblem).ToList();
        PolicyGrid.ItemsSource = list;
        PolicyGrid.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PolicyEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PolicyEmpty.Text = showAll ? "No policy states reported for this device." : "No policy conflicts or errors on this device.";
    }

    private void ShowAllPolicies_Changed(object sender, RoutedEventArgs e) { if (_loaded) ShowPolicies(); }

    private void Back_Click(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.ShowDevices();

    // ---------------- Intune actions ----------------

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        await Shell.Run("Sending sync…", async () =>
        {
            await S.Intune!.SyncAsync(_device.Id);
            S.Log(_device.DeviceName, "Sync", "Intune", "Sent");
        }, $"Sync sent to {_device.DeviceName}. It checks in within a few minutes if it's online.");
    }

    private async void Reboot_Click(object sender, RoutedEventArgs e)
    {
        if (!Shell.Confirm("Restart device", $"Restart {_device.DeviceName} now?\n\nThe user may lose unsaved work. Intune sends the restart at the device's next check-in."))
            return;
        await Shell.Run("Sending restart…", async () =>
        {
            await S.Intune!.RebootAsync(_device.Id);
            S.Log(_device.DeviceName, "Restart device", "Intune", "Sent");
        }, $"Restart sent to {_device.DeviceName}.");
    }

    private async void RunRemediation_Click(object sender, RoutedEventArgs e)
    {
        if (RemediationCombo.SelectedItem is not RemediationScript script)
        {
            Shell.Status("Pick a Remediation first.", isError: true);
            return;
        }
        await Shell.Run($"Starting \"{script.DisplayName}\"…", async () =>
        {
            await S.Intune!.RunRemediationAsync(_device.Id, script.Id);
            S.Log(_device.DeviceName, $"Run \"{script.DisplayName}\"", "Intune Remediation", "Requested");
        }, $"\"{script.DisplayName}\" requested. Results show in Intune under Remediations once the device runs it.");
    }

    // ---------------- Diagnostics ----------------

    private async void Collect_Click(object sender, RoutedEventArgs e)
    {
        await Shell.Run("Requesting diagnostics…", async () =>
        {
            _latestDiag = await S.Intune!.RequestDiagnosticsAsync(_device.Id);
            try { await S.Intune.SyncAsync(_device.Id); } catch { /* the sync is only a nudge */ }
            S.Log(_device.DeviceName, "Collect diagnostics", "Intune", "Requested");
            UpdateDiagUi();
            StartPolling();
        }, "Diagnostics requested. The device uploads them at its next check-in (a sync was sent too). This page checks every 30 seconds.");
    }

    private void StartPolling()
    {
        _pollUntil = DateTimeOffset.Now.AddMinutes(60);
        _poll.Start();
    }

    private async Task PollDiagnosticsAsync()
    {
        if (DateTimeOffset.Now > _pollUntil) { _poll.Stop(); return; }
        try
        {
            var diags = await S.Intune!.GetDiagnosticsAsync(_device.Id);
            _latestDiag = diags.FirstOrDefault();
            UpdateDiagUi();
            if (_latestDiag is { IsPending: false })
            {
                _poll.Stop();
                if (_latestDiag.IsCompleted)
                {
                    S.Log(_device.DeviceName, "Diagnostics uploaded", "Intune", "Ready to open");
                    Shell.Status($"Diagnostics from {_device.DeviceName} are ready. Click \"Open latest diagnostics\".");
                }
            }
        }
        catch
        {
            // Transient polling failures are ignored; the next tick tries again.
        }
    }

    private void UpdateDiagUi()
    {
        var d = _latestDiag;
        OpenDiagButton.IsEnabled = d?.IsCompleted == true && (d.ExpiresUtc is null || d.ExpiresUtc > DateTimeOffset.UtcNow);
        DiagStatusText.Text = d switch
        {
            null => "No diagnostics collected yet.",
            { IsPending: true } => $"Waiting for the device to upload (requested {d.RequestedUtc?.ToLocalTime():t}).",
            { IsCompleted: true } when d.ExpiresUtc < DateTimeOffset.UtcNow => $"Latest bundle ({d}) has expired in Intune. Collect a new one.",
            { IsCompleted: true } => $"Latest bundle: {d}",
            _ => $"Latest request: {d}",
        };
    }

    private async void OpenDiag_Click(object sender, RoutedEventArgs e)
    {
        var req = _latestDiag;
        if (req is null || !req.IsCompleted) return;

        LogBundle? bundle = null;
        var ok = await Shell.Run("Downloading and unpacking diagnostics…", async () =>
        {
            var safeName = string.Concat(_device.DeviceName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var stamp = (req.ReceivedUtc ?? req.RequestedUtc ?? DateTimeOffset.UtcNow).ToLocalTime().ToString("yyyyMMdd-HHmm");
            var folder = Path.Combine(SettingsStore.GetDiagnosticsFolder(S.Settings), safeName, $"{stamp}-{req.Id[..Math.Min(8, req.Id.Length)]}");
            var filesFolder = Path.Combine(folder, "files");

            if (Directory.Exists(filesFolder) && Directory.EnumerateFiles(filesFolder, "*", SearchOption.AllDirectories).Any())
            {
                bundle = await Task.Run(() => LogBundle.Open(filesFolder));
                return;
            }

            var zip = Path.Combine(folder, "bundle.zip");
            await S.Intune!.DownloadDiagnosticsAsync(_device.Id, req.Id, zip);
            bundle = await Task.Run(() => LogBundle.Extract(zip, filesFolder));
        });
        if (ok && bundle is not null)
            Shell.Navigate(new LogViewerView(bundle, _device.DeviceName));
    }

    // ---------------- Passwords and keys ----------------

    private string? RequireEntraId()
    {
        if (string.IsNullOrWhiteSpace(_device.AzureAdDeviceId) || _device.AzureAdDeviceId == Guid.Empty.ToString())
        {
            Shell.Status("This device has no Entra device ID in Intune, so its LAPS password and BitLocker keys can't be looked up.", isError: true);
            return null;
        }
        return _device.AzureAdDeviceId;
    }

    private async void Laps_Click(object sender, RoutedEventArgs e)
    {
        var entraId = RequireEntraId();
        if (entraId is null || S.Secrets is null) return;
        var reason = Dialogs.AskReason("Show local admin password",
            $"You're about to reveal the local administrator password for {_device.DeviceName}.");
        if (reason is null) return;

        LapsInfo? laps = null;
        var ok = await Shell.Run("Getting LAPS password…", async () => laps = await S.Secrets.GetLapsAsync(entraId));
        if (!ok)
        {
            ShowSecretsHint();
            return;
        }
        if (laps is null || laps.Credentials.Count == 0)
        {
            S.Log(_device.DeviceName, $"View LAPS password ({reason})", "Entra", "No password backed up");
            Shell.Status($"No LAPS password is backed up to Entra for {_device.DeviceName}. Check that a Windows LAPS policy targets it.", isError: true);
            return;
        }

        S.Log(_device.DeviceName, $"View LAPS password ({reason})", "Entra", "Revealed");
        var current = laps.Credentials[0];
        var rows = new List<Dialogs.SecretRow>
        {
            new($"Account: {current.AccountName}", current.Password,
                $"Backed up {current.BackupTime?.ToLocalTime():g}" +
                (laps.NextRotation is { } next ? $" · next rotation {next.ToLocalTime():g}" : "")),
        };
        rows.AddRange(laps.Credentials.Skip(1).Take(2).Select(c =>
            new Dialogs.SecretRow($"Previous: {c.AccountName}", c.Password, $"Backed up {c.BackupTime?.ToLocalTime():g}")));
        Dialogs.ShowSecrets($"LAPS – {_device.DeviceName}",
            "Consider rotating the password after use (Intune > device > Rotate local admin password).", rows);
    }

    private async void BitLocker_Click(object sender, RoutedEventArgs e)
    {
        var entraId = RequireEntraId();
        if (entraId is null || S.Secrets is null) return;
        var reason = Dialogs.AskReason("Show BitLocker recovery key",
            $"You're about to reveal the BitLocker recovery key(s) for {_device.DeviceName}.");
        if (reason is null) return;

        var rows = new List<Dialogs.SecretRow>();
        var ok = await Shell.Run("Getting BitLocker keys…", async () =>
        {
            var keys = await S.Secrets.ListBitLockerKeysAsync(entraId);
            foreach (var k in keys)
            {
                k.Key = await S.Secrets.GetBitLockerKeyAsync(k.Id);
                rows.Add(new Dialogs.SecretRow($"{k.VolumeText} · Key ID {k.ShortId}", k.Key,
                    $"Backed up {k.Created?.ToLocalTime():g}. Match the Key ID with the one on the recovery screen."));
            }
        });
        if (!ok)
        {
            ShowSecretsHint();
            return;
        }
        if (rows.Count == 0)
        {
            S.Log(_device.DeviceName, $"View BitLocker key ({reason})", "Entra", "No key backed up");
            Shell.Status($"No BitLocker recovery key is backed up to Entra for {_device.DeviceName}.", isError: true);
            return;
        }
        S.Log(_device.DeviceName, $"View BitLocker key ({reason})", "Entra", $"Revealed {rows.Count} key(s)");
        Dialogs.ShowSecrets($"BitLocker – {_device.DeviceName}", "Newest first.", rows);
    }

    private void ShowSecretsHint()
    {
        SecretsNote.Text = "If this says access denied: the app registration needs DeviceLocalCredential.Read.All and BitlockerKey.Read.All " +
                           "with admin consent, and your account needs an Entra role. LAPS passwords need Cloud Device Administrator or " +
                           "Intune Administrator; BitLocker keys also allow Helpdesk Administrator.";
        SecretsNote.Visibility = Visibility.Visible;
    }

    // ---------------- App install check ----------------

    private void AppsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        AppCheckButton.IsEnabled = AppsGrid.SelectedItem is AppInstallStatus { ApplicationId.Length: > 0 };

    private void AppsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AppsGrid.SelectedItem is AppInstallStatus) AppCheck_Click(sender, e);
    }

    private async void AppCheck_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not AppInstallStatus row || string.IsNullOrEmpty(row.ApplicationId) || S.AppCheck is null) return;
        CheckResult? result = null;
        string? note = null;
        await Shell.Run($"Checking {row.AppName}: assignments, filters, requirements…", async () =>
        {
            var appTask = S.AppCheck.GetAppAsync(row.ApplicationId);
            var factsTask = S.AppCheck.GetDeviceFactsAsync(_device);
            var (app, assignments) = await appTask;
            var facts = await factsTask;
            if (!facts.GroupsKnown) note = "Group membership couldn't be read; add GroupMember.Read.All for a complete check.";
            result = AppInstallCheck.Evaluate(app, assignments, facts, row, _apps ?? new List<AppInstallStatus>());
        });
        if (result is null) return;
        S.Log(_device.DeviceName, $"App check: {row.AppName}", "Console", result.Overall.ToString());
        CheckListWindow.Show(result, note ?? "Intune evaluates custom requirement scripts and some filter properties on the device itself.");
    }

    // ---------------- Readiness ----------------

    private async void Readiness_Click(object sender, RoutedEventArgs e)
    {
        if (S.Records is null || S.Secrets is null) return;
        AutopilotDevice? ap = null;
        bool apKnown = false, lapsKnown = false;
        DateTimeOffset? lapsBackup = null;

        await Shell.Run("Checking readiness…", async () =>
        {
            var apTask = S.Records.GetAutopilotDevicesAsync();
            Task<DateTimeOffset?>? lapsTask = string.IsNullOrEmpty(_device.AzureAdDeviceId) ? null : S.Secrets.GetLapsLastBackupAsync(_device.AzureAdDeviceId);
            try
            {
                var all = await apTask;
                ap = all.FirstOrDefault(a => string.Equals(a.SerialNumber?.Trim(), _device.SerialNumber?.Trim(), StringComparison.OrdinalIgnoreCase));
                apKnown = true;
            }
            catch (Exception) { }
            if (lapsTask is not null)
            {
                try { lapsBackup = await lapsTask; lapsKnown = true; } catch (Exception) { }
            }
        });

        var result = Readiness.Evaluate(new Readiness.Inputs
        {
            Device = _device,
            Autopilot = ap,
            AutopilotKnown = apKnown,
            Apps = _apps,
            LapsLastBackup = lapsBackup,
            LapsKnown = lapsKnown,
            DattoConfigured = S.Datto is not null,
        });
        S.Log(_device.DeviceName, "Readiness check", "Console", result.Verdict);
        CheckListWindow.Show(result, "Checks the LAPS backup date only; no password is revealed.");
    }

    // ---------------- Ticket summary ----------------

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        var text = TicketSummary.Build(_device, _apps, _policies, _latestDiag, S.ActionLog);
        Clipboard.SetText(text);
        Shell.Status("Ticket summary copied. Paste it into the ticket or Teams.");
    }

    // ---------------- Timeline ----------------

    private async void LoadTimeline_Click(object sender, RoutedEventArgs e)
    {
        if (S.Records is null || S.Directory is null) return;
        var problems = new List<string>();
        List<TimelineEvent>? audit = null;
        List<SignInEvent>? signIns = null;

        await Shell.Run("Building timeline (reading 30 days of Intune audit events)…", async () =>
        {
            var auditTask = S.Records.GetDeviceAuditEventsAsync(_device.Id, _device.DeviceName);
            Task<List<SignInEvent>>? signInTask = string.IsNullOrWhiteSpace(_device.UserId) || _device.UserId == Guid.Empty.ToString()
                ? null : S.Directory.GetSignInsAsync(_device.UserId!, days: 14, top: 500);

            try { audit = await auditTask; } catch (Exception ex) { problems.Add($"Intune audit ({ex.Message})"); }
            if (signInTask is not null)
            {
                try { signIns = await signInTask; } catch (Exception ex) { problems.Add($"sign-ins ({ex.Message})"); }
            }
        });

        var events = Timeline.Build(_device, audit, signIns, S.ActionLog, _diagnostics);
        TimelineGrid.ItemsSource = events;
        TimelineGrid.Visibility = Visibility.Visible;
        TimelineButton.Content = "Refresh";
        var failures = events.Count(x => x.IsFailure);
        TimelineSummary.Text = $"{events.Count} events{(failures > 0 ? $" · {failures} failures highlighted" : "")}" +
                               (problems.Count > 0 ? $" · not loaded: {string.Join("; ", problems)}" : "");
        if (problems.Count > 0) Shell.Status("Part of the timeline didn't load: " + string.Join("; ", problems), isError: true);
    }

    // ---------------- Datto actions ----------------

    private async void DattoRestartIme_Click(object sender, RoutedEventArgs e) =>
        await RunDattoAsync("Restart IME service", S.Settings.DattoRestartImeComponent, null);

    private async void DattoClearCache_Click(object sender, RoutedEventArgs e) =>
        await RunDattoAsync("Clear IME download cache", S.Settings.DattoClearCacheComponent, null);

    private async void DattoResetApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not AppInstallStatus app || string.IsNullOrWhiteSpace(app.ApplicationId))
        {
            Shell.Status("Select an app in the Apps list first.", isError: true);
            return;
        }
        if (!Shell.Confirm("Reset app install state",
                $"Reset \"{app.AppName}\" on {_device.DeviceName}?\n\nThis clears the Intune Management Extension's record of the app and restarts the service, so Intune re-evaluates and retries the install. It doesn't uninstall anything."))
            return;
        await RunDattoAsync($"Reset app \"{app.AppName}\"", S.Settings.DattoResetAppComponent,
            new Dictionary<string, string> { ["AppId"] = app.ApplicationId });
    }

    private async Task RunDattoAsync(string action, string componentName, Dictionary<string, string>? vars, Func<Task>? afterFinish = null)
    {
        var datto = S.Datto;
        var target = _device.Datto;
        if (datto is null || target is null) return;

        string jobUid = "";
        ActionLogEntry? entry = null;
        var started = await Shell.Run($"{action} via Datto…", async () =>
        {
            var componentUid = await S.ResolveComponentAsync(componentName);
            jobUid = await datto.RunQuickJobAsync(target.Uid, $"{action} – {_device.DeviceName} (Intune Console)", componentUid, vars);
            entry = S.Log(_device.DeviceName, action, "Datto", target.Online ? "Running" : "Queued (agent offline)");
        }, $"{action}: Datto job started on {_device.DeviceName}.");

        if (!started || entry is null || string.IsNullOrEmpty(jobUid)) return;

        // Watch the job in the background so the UI stays free.
        try
        {
            var result = await datto.WaitForJobAsync(jobUid, TimeSpan.FromMinutes(target.Online ? 5 : 1));
            var outcome = result.IsFinished ? $"Job {result.Status.ToLowerInvariant()}" : "Still running, check Datto for output";
            S.UpdateLog(entry, outcome);
            if (result.IsFinished) Shell.Status($"{action} on {_device.DeviceName}: {outcome}. Component output is in Datto's job results.");
            // Only clean up once the job is done; a queued job still needs its inputs (e.g. a staged file).
            if (result.IsFinished && afterFinish is not null) await afterFinish();
        }
        catch (Exception ex)
        {
            S.UpdateLog(entry, $"Couldn't check job status ({ex.Message})");
        }
    }

    private void OpenDatto_Click(object sender, RoutedEventArgs e) => OpenUrl(_device.Datto?.PortalUrl);

    private void WebRemote_Click(object sender, RoutedEventArgs e) => OpenUrl(_device.Datto?.WebRemoteUrl);

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // ---------------- Push file ----------------

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose a file to push" };
        if (dlg.ShowDialog() == true) PushFilePath.Text = dlg.FileName;
    }

    private async void Push_Click(object sender, RoutedEventArgs e)
    {
        var path = PushFilePath.Text.Trim().Trim('"');
        var dest = PushDestination.Text.Trim().Trim('"');

        if (S.Datto is null || _device.Datto is null)
        {
            Shell.Status("Pushing files needs Datto set up and this device found in Datto.", isError: true);
            return;
        }
        if (S.FileDrop is null)
        {
            Shell.Status("Set up the file drop container in Settings first (see the setup guide). It's how the file reaches the device.", isError: true);
            return;
        }
        if (!File.Exists(path))
        {
            Shell.Status("Choose a file that exists.", isError: true);
            return;
        }
        if (dest.Length < 3 || dest[1] != ':' || dest.IndexOfAny(['"', '*', '?', '<', '>', '|']) >= 0)
        {
            Shell.Status("Destination must be a full folder path like C:\\ProgramData\\Contoso\\", isError: true);
            return;
        }

        var name = Path.GetFileName(path);
        if (!Shell.Confirm("Push file", $"Push \"{name}\" to {dest} on {_device.DeviceName}?")) return;

        Core.Storage.FileDrop.StagedFile? staged = null;
        var uploaded = await Shell.Run($"Uploading {name} to the file drop…", async () =>
        {
            staged = await S.FileDrop.UploadAsync(path);
        });
        if (!uploaded || staged is null) return;

        var drop = S.FileDrop;
        var blob = staged.BlobName;
        await RunDattoAsync($"Push file {name}", S.Settings.DattoPushFileComponent, new Dictionary<string, string>
        {
            ["FileUrl"] = staged.DownloadUrl,
            ["FileName"] = name,
            ["Destination"] = dest,
            ["Sha256"] = staged.Sha256,
            ["Overwrite"] = PushOverwrite.IsChecked == true ? "true" : "false",
        }, afterFinish: () => drop.DeleteAsync(blob));
    }
}
