using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace IntuneConsole.App.Views;

/// <summary>Small modal windows built in code: a reason prompt and a secret viewer.</summary>
public static class Dialogs
{
    private static Window? Owner => Application.Current?.MainWindow;

    private static Window NewWindow(string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Owner = Owner,
        ShowInTaskbar = false,
        Background = (Brush)Application.Current.FindResource("Surface"),
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 14,
    };

    /// <summary>Asks why a secret is being revealed. Returns null if cancelled.</summary>
    public static string? AskReason(string title, string message)
    {
        var win = NewWindow(title, 460);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Reason (ticket number or short note)", FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 4) });
        var box = new TextBox();
        System.Windows.Automation.AutomationProperties.SetName(box, "Reason");
        panel.Children.Add(box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var ok = new Button { Content = "Show", IsDefault = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0),
                              Style = (Style)Application.Current.FindResource("PrimaryButton"), HorizontalContentAlignment = HorizontalAlignment.Center };
        string? result = null;
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) { box.Focus(); return; }
            result = box.Text.Trim();
            win.DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        win.Content = panel;
        win.Loaded += (_, _) => box.Focus();
        return win.ShowDialog() == true ? result : null;
    }

    public sealed record SecretRow(string Label, string Value, string? Note = null);

    /// <summary>Shows secrets with copy buttons. Closes itself after two minutes and clears the
    /// clipboard if it still holds a copied secret.</summary>
    public static void ShowSecrets(string title, string intro, IReadOnlyList<SecretRow> rows)
    {
        var win = NewWindow(title, 560);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("Muted"),
                                           Margin = new Thickness(0, 0, 0, 16) });
        string? copied = null;

        foreach (var row in rows)
        {
            panel.Children.Add(new TextBlock { Text = row.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, row.Note is null ? 14 : 2) };
            var copy = new Button { Content = "Copy", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
            DockPanel.SetDock(copy, Dock.Right);
            var value = row.Value;
            copy.Click += (_, _) => { Clipboard.SetText(value); copied = value; copy.Content = "Copied"; };
            line.Children.Add(copy);
            line.Children.Add(new TextBox
            {
                Text = value,
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 16,
                Background = (Brush)Application.Current.FindResource("Page"),
                VerticalContentAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(line);
            if (row.Note is not null)
                panel.Children.Add(new TextBlock { Text = row.Note, FontSize = 12, Foreground = (Brush)Application.Current.FindResource("Muted"),
                                                   TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        }

        var footer = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        DockPanel.SetDock(close, Dock.Right);
        close.Click += (_, _) => win.Close();
        var countdown = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("Muted") };
        footer.Children.Add(close);
        footer.Children.Add(countdown);
        panel.Children.Add(footer);
        win.Content = panel;

        var remaining = 120;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            remaining--;
            countdown.Text = $"Closes in {remaining} s";
            if (remaining <= 0) win.Close();
        };
        countdown.Text = $"Closes in {remaining} s";
        timer.Start();

        win.Closed += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (copied is not null && Clipboard.ContainsText() && Clipboard.GetText() == copied) Clipboard.Clear();
            }
            catch (System.Runtime.InteropServices.COMException) { /* clipboard busy; ignore */ }
        };
        win.ShowDialog();
    }
}
