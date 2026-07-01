using System;
using System.IO;
using System.IO.Compression;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SnapshotExplorerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vaultsync-explorer-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SnapshotExplorerService _service = new();

    public SnapshotExplorerServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void List_FolderBackup_ReturnsFoldersAndFiles()
    {
        string backup = CreateFolderBackup();

        SnapshotExplorerResult root = _service.List(backup);
        SnapshotExplorerResult src = _service.List(backup, "src");

        Assert.Contains(root.Entries, e => e.Kind == SnapshotExplorerEntryKind.Folder && e.Path == "src");
        Assert.Contains(src.Entries, e => e.Kind == SnapshotExplorerEntryKind.File && e.Path == "src/app.json");
    }

    [Fact]
    public void PreviewText_FolderBackup_ReturnsSupportedText()
    {
        string backup = CreateFolderBackup();

        SnapshotPreviewResult preview = _service.PreviewText(backup, "src/app.json");

        Assert.True(preview.Success);
        Assert.Contains("\"name\"", preview.Text);
    }

    [Fact]
    public void PreviewText_FolderBackup_ReturnsSourceCodeText()
    {
        string backup = CreateFolderBackup();

        SnapshotPreviewResult preview = _service.PreviewText(backup, "src/Program.cs");

        Assert.True(preview.Success);
        Assert.Contains("Console.WriteLine", preview.Text);
    }

    [Fact]
    public void PreviewText_FolderBackup_RejectsBinaryContent()
    {
        string backup = CreateFolderBackup();

        SnapshotPreviewResult preview = _service.PreviewText(backup, "asset.bin");

        Assert.False(preview.Success);
        Assert.Contains("text-like files", preview.Error);
    }

    [Fact]
    public void List_ArchiveBackup_SupportsSearchAndPreview()
    {
        string backup = CreateArchiveBackup();

        SnapshotExplorerResult search = _service.List(backup, search: "notes");
        SnapshotPreviewResult preview = _service.PreviewText(backup, "docs/notes.md");

        Assert.Contains(search.Entries, e => e.Kind == SnapshotExplorerEntryKind.File && e.Path == "docs/notes.md");
        Assert.True(preview.Success);
        Assert.Contains("# Notes", preview.Text);
    }

    [Fact]
    public void RestoreSelection_ArchiveFolder_RestoresOnlySelectedFolder()
    {
        string backup = CreateArchiveBackup();
        string target = Path.Combine(_root, "restore");

        SnapshotRestoreSelectionResult result = _service.RestoreSelection(backup, target, ["docs"]);

        Assert.Equal(1, result.FileCount);
        Assert.True(File.Exists(Path.Combine(target, "docs", "notes.md")));
        Assert.False(File.Exists(Path.Combine(target, "src", "app.json")));
    }

    [Fact]
    public void List_EncryptedArchive_ReturnsExplicitEncryptedSource()
    {
        string backup = Path.Combine(_root, "encrypted-backup");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, BackupArchiveCryptoService.EncryptedArchiveFileName), "encrypted");

        SnapshotExplorerResult result = _service.List(backup);

        Assert.Equal(SnapshotExplorerSourceKind.EncryptedArchive, result.SourceKind);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void PreviewText_RejectsUnsafePath()
    {
        string backup = CreateFolderBackup();

        Assert.Throws<InvalidDataException>(() => _service.PreviewText(backup, "../outside.txt"));
    }

    private string CreateFolderBackup()
    {
        string backup = Path.Combine(_root, "folder-backup");
        Directory.CreateDirectory(Path.Combine(backup, "src"));
        Directory.CreateDirectory(Path.Combine(backup, "docs"));
        File.WriteAllText(Path.Combine(backup, "src", "app.json"), "{\"name\":\"VaultSync\"}");
        File.WriteAllText(Path.Combine(backup, "src", "Program.cs"), "Console.WriteLine(\"VaultSync\");");
        File.WriteAllText(Path.Combine(backup, "docs", "notes.md"), "# Notes");
        File.WriteAllBytes(Path.Combine(backup, "asset.bin"), [0, 1, 2]);
        return backup;
    }

    private string CreateArchiveBackup()
    {
        string backup = Path.Combine(_root, "archive-backup");
        Directory.CreateDirectory(backup);
        using ZipArchive archive = ZipFile.Open(Path.Combine(backup, BackupArchiveCryptoService.PlainArchiveFileName), ZipArchiveMode.Create);
        AddArchiveText(archive, "src/app.json", "{\"name\":\"VaultSync\"}");
        AddArchiveText(archive, "docs/notes.md", "# Notes");
        return backup;
    }

    private static void AddArchiveText(ZipArchive archive, string path, string text)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path);
        using StreamWriter writer = new(entry.Open());
        writer.Write(text);
    }
}
