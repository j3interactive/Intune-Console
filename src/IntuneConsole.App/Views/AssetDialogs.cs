using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IntuneConsole.Core.Graph;
using IntuneConsole.Core.Models;

namespace IntuneConsole.App.Views;

/// <summary>Check-out (pick a person) and check-in dialogs, built in code.</summary>
public static class AssetDialogs
{
    public sealed record PickResult(DirectoryUser User, string Status, string? Location, string? Accessories, string? Notes, bool Print);
    public sealed record CheckInResult(string Status, string? Accessories, string? Notes, bool Print);

    private static Window NewWindow(Window owner, string title) => new()
    {
        Title = title,
        Width = 560,
        SizeToContent = SizeToContent.Height,
        ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Owner = owner,
        ShowInTaskbar = false,
        Background = (Brush)Application.Current.FindResource("Surface"),
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 14,
    };

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 12, 0, 4) };

    private static StackPanel Buttons(Window win, string okText, Func<bool> onOk)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 110, Margin = new Thickness(8, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center,
                              Style = (Style)Application.Current.FindResource("PrimaryButton") };
        ok.Click += (_, _) => { if (onOk()) win.DialogResult = true; };
        panel.Children.Add(cancel);
        panel.Children.Add(ok);
        return panel;
    }

    /// <summary>Search Entra for a person and choose how the device is issued.</summary>
    public static PickResult? PickPerson(Window owner, DirectoryService directory, string defaultStatus)
    {
        var win = NewWindow(owner, "Assign / check out");
        var root = new StackPanel { Margin = new Thickness(24) };

        root.Children.Add(new TextBlock { Text = "Person", FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 4) });
        var search = new TextBox();
        System.Windows.Automation.AutomationProperties.SetName(search, "Search for a person");
        root.Children.Add(search);
        var results = new ListBox { Height = 180, Margin = new Thickness(0, 6, 0, 0) };
        results.ItemTemplate = BuildUserTemplate();
        System.Windows.Automation.AutomationProperties.SetName(results, "People");
        root.Children.Add(results);
        var searchStatus = new TextBlock { FontSize = 12, Foreground = (Brush)Application.Current.FindResource("Muted"), Margin = new Thickness(0, 4, 0, 0),
                                           Text = "Type at least 2 characters of a name, UPN or employee ID." };
        root.Children.Add(searchStatus);

        root.Children.Add(Label("Issue as"));
        var status = new ComboBox { ItemsSource = new[] { AssetStatus.InUse, AssetStatus.Loaner }, SelectedItem = defaultStatus };
        root.Children.Add(status);
        root.Children.Add(Label("Location (optional)"));
        var location = new TextBox();
        root.Children.Add(location);
        root.Children.Add(Label("Accessories (optional, printed on the sheet)"));
        var accessories = new TextBox { Text = "Charger" };
        root.Children.Add(accessories);
        root.Children.Add(Label("Notes (optional, e.g. ticket number or return date)"));
        var notes = new TextBox();
        root.Children.Add(notes);
        var print = new CheckBox { Content = "Open a handover sheet to print and sign", IsChecked = true, Margin = new Thickness(0, 14, 0, 0) };
        root.Children.Add(print);
        var error = new TextBlock { Foreground = (Brush)Application.Current.FindResource("ErrorFg"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(error);

        PickResult? result = null;
        root.Children.Add(Buttons(win, "Check out", () =>
        {
            if (results.SelectedItem is not DirectoryUser u) { error.Text = "Choose a person from the list."; return false; }
            result = new PickResult(u, status.SelectedItem as string ?? AssetStatus.InUse, Blank(location.Text), Blank(accessories.Text), Blank(notes.Text), print.IsChecked == true);
            return true;
        }));

        // Debounced search.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        CancellationTokenSource? cts = null;
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            cts?.Cancel();
            var my = cts = new CancellationTokenSource();
            searchStatus.Text = "Searching…";
            try
            {
                var users = await directory.SearchUsersAsync(search.Text, top: 20, ct: my.Token);
                if (my.IsCancellationRequested) return;
                results.ItemsSource = users;
                if (users.Count > 0) results.SelectedIndex = 0;
                searchStatus.Text = users.Count == 0 ? "No one found." : $"{users.Count} found.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { searchStatus.Text = $"Search failed: {ex.Message}"; }
        };
        search.TextChanged += (_, _) => { timer.Stop(); if (search.Text.Trim().Length >= 2) timer.Start(); };

        win.Content = root;
        win.Loaded += (_, _) => search.Focus();
        return win.ShowDialog() == true ? result : null;
    }

    private static DataTemplate BuildUserTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 4, 2, 4));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DirectoryUser.DisplayName)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        var sub = new FrameworkElementFactory(typeof(TextBlock));
        sub.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(DirectoryUser.Subtitle)));
        sub.SetValue(TextBlock.FontSizeProperty, 12.0);
        sub.SetValue(TextBlock.ForegroundProperty, Application.Current.FindResource("Muted"));
        panel.AppendChild(name);
        panel.AppendChild(sub);
        return new DataTemplate { VisualTree = panel };
    }

    public static CheckInResult? CheckIn(Window owner, string fromName)
    {
        var win = NewWindow(owner, "Check in");
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(fromName) ? "Return this device to IT." : $"Returning from {fromName}.", TextWrapping = TextWrapping.Wrap });
        root.Children.Add(Label("New status"));
        var status = new ComboBox { ItemsSource = new[] { AssetStatus.InStock, AssetStatus.Repair, AssetStatus.Retired, AssetStatus.Lost }, SelectedIndex = 0 };
        root.Children.Add(status);
        root.Children.Add(Label("Accessories returned (optional)"));
        var accessories = new TextBox { Text = "Charger" };
        root.Children.Add(accessories);
        root.Children.Add(Label("Condition / notes (optional)"));
        var notes = new TextBox { AcceptsReturn = true, MinHeight = 60, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top };
        root.Children.Add(notes);
        var print = new CheckBox { Content = "Open a return sheet to print and sign", IsChecked = false, Margin = new Thickness(0, 14, 0, 0) };
        root.Children.Add(print);
        root.Children.Add(new TextBlock { Text = "If the device is being reissued or retired, consider an Autopilot reset or wipe from the device page.",
                                          FontSize = 12, Foreground = (Brush)Application.Current.FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });

        CheckInResult? result = null;
        root.Children.Add(Buttons(win, "Check in", () =>
        {
            result = new CheckInResult(status.SelectedItem as string ?? AssetStatus.InStock, Blank(accessories.Text), Blank(notes.Text), print.IsChecked == true);
            return true;
        }));
        win.Content = root;
        return win.ShowDialog() == true ? result : null;
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
