using System;
using System.Linq;

namespace VaultSync.UI.Infrastructure;

internal static class ExpectedDesktopNoise
{
    public static bool IsExpectedUnobservedTaskException(Exception ex)
    {
        if (ex is AggregateException aggregate)
            return aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsExpectedUnobservedTaskException);

        string text = $"{ex.GetType().FullName}: {ex.Message}";
        return OperatingSystem.IsLinux() &&
               text.Contains("org.freedesktop.DBus.Error.ServiceUnknown", StringComparison.OrdinalIgnoreCase) &&
               text.Contains("com.canonical.AppMenu.Registrar", StringComparison.OrdinalIgnoreCase);
    }
}
