using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace VaultSync.UI.Infrastructure;

public static class ClipboardHelper
{
    public static async Task<bool> TryCopyAsync(string text)
    {
        return await TryCopyAsync(text, GetApplicationClipboard());
    }

    public static async Task<bool> TryCopyAsync(string text, IClipboard? clipboard)
    {
        try
        {
            if (clipboard is null)
                return false;

            await clipboard.SetTextAsync(text);
            return true;
        }
        catch
        {
            // Best effort: ignore clipboard failures.
        }

        return false;
    }

    private static IClipboard? GetApplicationClipboard()
    {
        return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime
            ? lifetime.MainWindow?.Clipboard
            : null;
    }
}
