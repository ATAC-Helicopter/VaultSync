using System.Collections.Concurrent;
using System.Text;

namespace VaultSync.Core.Services;

public interface IInstallationIdentityProvider
{
    string GetOrCreate();
}

/// <summary>
/// Provides the durable, machine-local identity used by repository coordination.
/// This identity is deliberately independent of telemetry and the mutable host name.
/// </summary>
public sealed class InstallationIdentityService : IInstallationIdentityProvider
{
    public const string IdentityFileName = "installation.id";

    private static readonly ConcurrentDictionary<string, object> PathGates =
        new(GetPathComparer());

    private readonly string _dataDirectory;

    public InstallationIdentityService(string? dataDirectory = null)
    {
        _dataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? ResolveDefaultDataDirectory()
            : Path.GetFullPath(dataDirectory);
    }

    public string IdentityPath => Path.Combine(_dataDirectory, IdentityFileName);

    public string GetOrCreate()
    {
        object pathGate = PathGates.GetOrAdd(IdentityPath, static _ => new object());
        lock (pathGate)
        {
            PrivateDataPermissions.EnsureDirectory(_dataDirectory);

            if (File.Exists(IdentityPath))
                return ReadExistingIdentity();

            return CreateIdentityAtomically();
        }
    }

    private string ReadExistingIdentity()
    {
        FileAttributes attributes = File.GetAttributes(IdentityPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Installation identity must be a regular private file: '{IdentityPath}'.");
        }

        string serialized = File.ReadAllText(IdentityPath, Encoding.UTF8).Trim();
        if (!Guid.TryParseExact(serialized, "N", out Guid parsed) || parsed == Guid.Empty)
        {
            throw new InvalidDataException(
                $"Installation identity is malformed and was not replaced: '{IdentityPath}'.");
        }

        string canonical = parsed.ToString("N");
        if (!string.Equals(serialized, canonical, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Installation identity is not in canonical form and was not replaced: '{IdentityPath}'.");
        }

        PrivateDataPermissions.RestrictFile(IdentityPath);
        return canonical;
    }

    private string CreateIdentityAtomically()
    {
        string identity = Guid.NewGuid().ToString("N");
        string temporaryPath = Path.Combine(
            _dataDirectory,
            $".{IdentityFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                       bufferSize: 1024,
                       leaveOpen: true))
            {
                writer.WriteLine(identity);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            PrivateDataPermissions.RestrictFile(temporaryPath);

            try
            {
                File.Move(temporaryPath, IdentityPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(IdentityPath))
            {
                return ReadExistingIdentity();
            }

            PrivateDataPermissions.RestrictFile(IdentityPath);
            return identity;
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static string ResolveDefaultDataDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            throw new InvalidOperationException(
                "The application data directory is unavailable; a durable installation identity cannot be created.");
        }

        return Path.Combine(appData, "VaultSync");
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A failed cleanup does not invalidate a successfully persisted identity.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed cleanup does not invalidate a successfully persisted identity.
        }
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
