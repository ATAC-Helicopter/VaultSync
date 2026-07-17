using System;
using System.IO;
using System.Linq;
using VaultSync.UI.Infrastructure;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiagnosticsRetentionTests
{
    [Fact]
    public void PruneDiagnostics_LimitsHangDumpCount()
    {
        using var temp = new TestSupport.TempDirectory();
        CreateDiagnostic(temp.Path, "hangdump-old.dmp", 10, DateTime.UtcNow.AddMinutes(-3));
        CreateDiagnostic(temp.Path, "hangdump-middle.dmp", 10, DateTime.UtcNow.AddMinutes(-2));
        CreateDiagnostic(temp.Path, "hangdump-new.dmp", 10, DateTime.UtcNow.AddMinutes(-1));

        DiagnosticsLogger.PruneDiagnostics(temp.Path, maxHangDumpFiles: 2, maxDiagnosticsBytes: 100);

        string[] retained = Directory.GetFiles(temp.Path, "hangdump-*.dmp");
        Assert.Equal(2, retained.Length);
        Assert.DoesNotContain(retained, path => path.EndsWith("hangdump-old.dmp", StringComparison.Ordinal));
    }

    [Fact]
    public void PruneDiagnostics_LimitsCombinedDiagnosticSize()
    {
        using var temp = new TestSupport.TempDirectory();
        CreateDiagnostic(temp.Path, "hangdump-old.dmp", 60, DateTime.UtcNow.AddMinutes(-3));
        CreateDiagnostic(temp.Path, "sample-middle.txt", 30, DateTime.UtcNow.AddMinutes(-2));
        CreateDiagnostic(temp.Path, "session-new.log", 30, DateTime.UtcNow.AddMinutes(-1));

        DiagnosticsLogger.PruneDiagnostics(temp.Path, maxHangDumpFiles: 2, maxDiagnosticsBytes: 70);

        FileInfo[] retained = Directory.GetFiles(temp.Path).Select(path => new FileInfo(path)).ToArray();
        Assert.Equal(60, retained.Sum(file => file.Length));
        Assert.DoesNotContain(retained, file => file.Name == "hangdump-old.dmp");
    }

    private static void CreateDiagnostic(string directory, string name, int length, DateTime lastWriteUtc)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[length]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
    }
}
