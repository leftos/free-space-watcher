using System.Windows;

namespace FreeSpaceWatcher.Tray.Alerts;

/// <summary>Asks the user to confirm, and tells the user about failures.</summary>
public interface IUserDialogs
{
    /// <summary>Asks a yes/no question; no is the default.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="message">The question.</param>
    /// <returns>Whether the user chose yes.</returns>
    bool Confirm(string title, string message);

    /// <summary>Shows an error.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="message">What went wrong.</param>
    void ShowError(string title, string message);
}

/// <summary>Shows <see cref="IUserDialogs"/> questions and errors as message boxes.</summary>
public sealed class MessageBoxDialogs : IUserDialogs
{
    /// <inheritdoc/>
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <inheritdoc/>
    public void ShowError(string title, string message) => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
