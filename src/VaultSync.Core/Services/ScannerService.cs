using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public class ScannerService
{
    private readonly FilterService _filter;
    public ScannerService(FilterService filter) => _filter = filter;

    public IEnumerable<FileEntry> Scan(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (_filter.ShouldExclude(root, path)) continue;
            var fi = new FileInfo(path);
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            yield return new FileEntry(rel, fi.Length, fi.LastWriteTimeUtc, "");
        }
    }
}