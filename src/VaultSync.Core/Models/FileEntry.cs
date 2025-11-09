namespace VaultSync.Core.Models;

public record FileEntry(
    string RelPath,
    long Size,
    DateTime MTimeUtc,
    string HashSha256 // empty during pre-hash
);