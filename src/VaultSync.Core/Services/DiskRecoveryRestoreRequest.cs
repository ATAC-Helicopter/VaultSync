namespace VaultSync.Core.Services;

/// <summary>An image-selection hint carried on the recovery export volume.</summary>
public static class DiskRecoveryRestoreRequest
{
    internal const string FileName = ".vaultsync-restore-request";

    public static bool CanSave(string imageFolder, Guid imageId) =>
        imageId != Guid.Empty && Path.GetFileName(Path.TrimEndingDirectorySeparator(imageFolder)) == imageId.ToString("N");

    public static void Save(string imageFolder, Guid imageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageFolder);
        if (!CanSave(imageFolder, imageId))
            throw new ArgumentException("Expected the verified export folder.", nameof(imageFolder));

        var export = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(imageFolder)))!;
        string temporary = Path.Combine(export, FileName + "." + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(export, FileName);
        var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using (stream)
            {
                Span<byte> encoded = stackalloc byte[32];
                if (!imageId.TryFormat(encoded, out _, "N")) throw new InvalidOperationException();
                stream.Write(encoded);
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
                    File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                        UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
