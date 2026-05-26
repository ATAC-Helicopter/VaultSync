using System;
using System.Threading.Tasks;

namespace VaultSync.UI.Infrastructure;

public static class DetachedTask
{
    public static void Run(Action operation, string operationName)
    {
        _ = Task.Run(() =>
        {
            try
            {
                operation();
            }
            catch (Exception ex)
            {
                LogFailure(operationName, ex);
            }
        });
    }

    public static void Run(Func<Task> operation, string operationName)
    {
        _ = Task.Run(() => RunAsync(operation, operationName));
    }

    public static async Task RunAsync(Func<Task> operation, string operationName)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailure(operationName, ex);
        }
    }

    private static void LogFailure(string operationName, Exception ex)
    {
        DiagnosticsLogger.Record(
            $"Detached operation failed ({operationName}): {ex.GetType().Name} - {ex.Message}");
    }
}
