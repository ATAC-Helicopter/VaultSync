using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SnapshotExplorerServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vaultsync-explorer-tests-" + Guid.NewGuid().ToString("N"));

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

        SnapshotExplorerResult root = SnapshotExplorerService.List(backup);
        SnapshotExplorerResult src = SnapshotExplorerService.List(backup, "src");

        Assert.Contains(root.Entries, e => e.Kind == SnapshotExplorerEntryKind.Folder && e.Path == "src");
        Assert.Contains(src.Entries, e => e.Kind == SnapshotExplorerEntryKind.File && e.Path == "src/app.json");
    }

    [Fact]
    public void PreviewText_FolderBackup_ReturnsSupportedText()
    {
        string backup = CreateFolderBackup();

        SnapshotPreviewResult preview = SnapshotExplorerService.PreviewText(backup, "src/app.json");

        Assert.True(preview.Success);
        Assert.Contains("\"name\"", preview.Text);
    }

    [Fact]
    public void PreviewText_FolderBackup_ReturnsSourceCodeText()
    {
        string backup = CreateFolderBackup();

        SnapshotPreviewResult preview = SnapshotExplorerService.PreviewText(backup, "src/Program.cs");

        Assert.True(preview.Success);
        Assert.Contains("Console.WriteLine", preview.Text);
    }

    [Fact]
    public void PreviewText_FolderBackup_RejectsBinaryContent()
    {
        string backup = CreateFolderBackup();

        SnapshotPreviewResult preview = SnapshotExplorerService.PreviewText(backup, "asset.bin");

        Assert.False(preview.Success);
        Assert.Contains("text-like files", preview.Error);
    }

    [Fact]
    public void List_ArchiveBackup_SupportsSearchAndPreview()
    {
        string backup = CreateArchiveBackup();

        SnapshotExplorerResult search = SnapshotExplorerService.List(backup, search: "notes");
        SnapshotPreviewResult preview = SnapshotExplorerService.PreviewText(backup, "docs/notes.md");

        Assert.Contains(search.Entries, e => e.Kind == SnapshotExplorerEntryKind.File && e.Path == "docs/notes.md");
        Assert.True(preview.Success);
        Assert.Contains("# Notes", preview.Text);
    }

    [Fact]
    public void List_ArchiveBackup_ReturnsFoldersAndFiles()
    {
        string backup = CreateArchiveBackup();

        SnapshotExplorerResult root = SnapshotExplorerService.List(backup);
        SnapshotExplorerResult docs = SnapshotExplorerService.List(backup, "docs");

        Assert.Contains(root.Entries, e => e.Kind == SnapshotExplorerEntryKind.Folder && e.Path == "docs");
        Assert.Contains(docs.Entries, e => e.Kind == SnapshotExplorerEntryKind.File && e.Path == "docs/notes.md");
    }

    [Fact]
    public void ArchiveBackup_PreservesCaseDistinctFiles()
    {
        string backup = Path.Combine(_root, "case-distinct-archive");
        Directory.CreateDirectory(backup);
        using (ZipArchive archive = ZipFile.Open(Path.Combine(backup, BackupArchiveCryptoService.PlainArchiveFileName), ZipArchiveMode.Create))
        {
            AddArchiveText(archive, "docs/Foo.txt", "upper");
            AddArchiveText(archive, "docs/foo.txt", "lower");
        }

        SnapshotExplorerResult docs = SnapshotExplorerService.List(backup, "docs");
        SnapshotPreviewResult upper = SnapshotExplorerService.PreviewText(backup, "docs/Foo.txt");
        SnapshotPreviewResult lower = SnapshotExplorerService.PreviewText(backup, "docs/foo.txt");

        Assert.Equal(2, docs.Entries.Count(entry => entry.Kind == SnapshotExplorerEntryKind.File));
        Assert.Equal("upper", upper.Text);
        Assert.Equal("lower", lower.Text);
    }

    [Fact]
    public void RestoreSelection_ArchiveFolder_RestoresOnlySelectedFolder()
    {
        string backup = CreateArchiveBackup();
        string target = Path.Combine(_root, "restore");

        SnapshotRestoreSelectionResult result = SnapshotExplorerService.RestoreSelection(backup, target, ["docs"]);

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

        SnapshotExplorerResult result = SnapshotExplorerService.List(backup);

        Assert.Equal(SnapshotExplorerSourceKind.EncryptedArchive, result.SourceKind);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void PreviewText_RejectsUnsafePath()
    {
        string backup = CreateFolderBackup();

        Assert.Throws<InvalidDataException>(() => SnapshotExplorerService.PreviewText(backup, "../outside.txt"));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("/absolute.txt")]
    public void RestoreSelection_ArchiveRejectsTraversalEntries(string maliciousPath)
    {
        string backup = Path.Combine(_root, "malicious-archive-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(_root, "restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        using (ZipArchive archive = ZipFile.Open(Path.Combine(backup, BackupArchiveCryptoService.PlainArchiveFileName), ZipArchiveMode.Create))
        {
            AddArchiveText(archive, maliciousPath, "escape");
            AddArchiveText(archive, "safe/file.txt", "safe");
        }

        Assert.Throws<InvalidDataException>(() => SnapshotExplorerService.RestoreSelection(backup, target, ["safe", "absolute.txt"]));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public void RestoreSelection_ArchiveRejectsLinkedDirectoryEscape()
    {
        if (OperatingSystem.IsWindows())
            return;

        string backup = CreateArchiveBackup();
        string target = Path.Combine(_root, "linked-restore");
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(target, "docs"), outside);

        Assert.Throws<InvalidDataException>(() => SnapshotExplorerService.RestoreSelection(backup, target, ["docs"]));
        Assert.False(File.Exists(Path.Combine(outside, "notes.md")));
    }

    [Fact]
    public void RestoreSelection_FolderRejectsLinkedDirectoryEscape()
    {
        if (OperatingSystem.IsWindows())
            return;

        string backup = CreateFolderBackup();
        string target = Path.Combine(_root, "linked-folder-restore");
        string outside = Path.Combine(_root, "folder-restore-outside");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(target, "docs"), outside);

        Assert.Throws<InvalidDataException>(() => SnapshotExplorerService.RestoreSelection(backup, target, ["docs"]));
        Assert.False(File.Exists(Path.Combine(outside, "notes.md")));
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
