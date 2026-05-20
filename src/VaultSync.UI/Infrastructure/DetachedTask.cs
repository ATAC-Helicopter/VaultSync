using System;
using System.Threading.Tasks;

namespace VaultSync.UI.Infrastructure;

public static class DetachedTask
{
    public static async Task RunAsync(Func<Task> operation, string operationName)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Record(
                $"Detached operation failed ({operationName}): {ex.GetType().Name} - {ex.Message}");
        }
    }
}
