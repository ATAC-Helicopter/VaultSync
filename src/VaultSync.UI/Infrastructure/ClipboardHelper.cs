using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace VaultSync.UI.Infrastructure;

public static class ClipboardHelper
{
    public static async Task<bool> TryCopyAsync(string text)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime &&
                lifetime.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
                return true;
            }
        }
        catch
        {
            // Best effort: ignore clipboard failures.
        }

        return false;
    }
}
