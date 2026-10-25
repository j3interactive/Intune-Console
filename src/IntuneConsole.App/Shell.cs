using System.Windows;
using System.Windows.Controls;

namespace IntuneConsole.App;

/// <summary>Navigation and status messages for the main window.</summary>
public static class Shell
{
    private static MainWindow? Window => Application.Current?.MainWindow as MainWindow;

    public static void Navigate(UserControl view) => Window?.ShowView(view);

    public static void Status(string message, bool isError = false) => Window?.ShowStatus(message, isError);

    /// <summary>Runs an async action with the busy indicator and turns exceptions into a readable status.</summary>
    public static async Task<bool> Run(string busyText, Func<Task> action, string? doneText = null)
    {
        Window?.SetBusy(busyText);
        try
        {
            await action();
            if (doneText is not null) Status(doneText);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Status(ex.Message, isError: true);
            return false;
        }
        finally
        {
            Window?.SetBusy(null);
        }
    }

    public static bool Confirm(string title, string message) =>
        MessageBox.Show(Window!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
        == MessageBoxResult.Yes;
}
