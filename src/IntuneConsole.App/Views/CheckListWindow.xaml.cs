using System.Text;
using System.Windows;
using System.Windows.Media;
using IntuneConsole.Core.Models;

namespace IntuneConsole.App.Views;

/// <summary>Shows a step-by-step check result (app install check, readiness check).</summary>
public partial class CheckListWindow : Window
{
    private readonly CheckResult _result;

    public CheckListWindow(CheckResult result, string? footnote = null)
    {
        InitializeComponent();
        _result = result;
        Owner = Application.Current.MainWindow;
        Title = result.Title;
        HeadingText.Text = result.Title;
        VerdictText.Text = result.Verdict;
        FootnoteText.Text = footnote ?? "";
        StepsGrid.ItemsSource = result.Steps;

        var (bg, border, fg) = result.Overall switch
        {
            CheckStatus.Fail => ("ErrorBg", "ErrorFg", "ErrorFg"),
            CheckStatus.Pass => ("OkBg", "OkFg", "OkFg"),
            CheckStatus.Warn => ("WarnBg", "WarnFg", "WarnFg"),
            _ => ("Surface", "Border", "Ink"),
        };
        VerdictBox.Background = (Brush)FindResource(bg);
        VerdictBox.BorderBrush = (Brush)FindResource(border);
        VerdictText.Foreground = (Brush)FindResource(fg);
    }

    public static void Show(CheckResult result, string? footnote = null) => new CheckListWindow(result, footnote).ShowDialog();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine(_result.Title);
        sb.AppendLine(_result.Verdict);
        sb.AppendLine();
        foreach (var s in _result.Steps)
        {
            sb.AppendLine($"[{s.StatusText}] {s.Name}: {s.Detail}");
            if (!string.IsNullOrWhiteSpace(s.Fix)) sb.AppendLine($"        -> {s.Fix}");
        }
        Clipboard.SetText(sb.ToString());
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
