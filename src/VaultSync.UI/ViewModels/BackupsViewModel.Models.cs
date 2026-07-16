using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels
{
    // ---------- Models ----------

    public sealed class StorageConsumerItem
    {
        public StorageConsumerItem(string projectName, string totalSize, double sharePercent)
        {
            ProjectName = projectName;
            TotalSize = totalSize;
            SharePercent = sharePercent;
        }

        public string ProjectName
        {
            get;
        }
        public string TotalSize
        {
            get;
        }
        public double SharePercent
        {
            get;
        }
        public string SharePercentLabel => $"{SharePercent:0}%";
    }

    public sealed class DiffPreviewPathItem
    {
        public DiffPreviewPathItem(string path, int changes, string changedBytes)
        {
            Path = path;
            Changes = changes;
            ChangedBytes = changedBytes;
        }

        public string Path
        {
            get;
        }
        public int Changes
        {
            get;
        }
        public string ChangedBytes
        {
            get;
        }
    }

    public sealed class DiffPreviewFileItem
    {
        public DiffPreviewFileItem(SnapshotFileChange change)
        {
            Change = change;
            Path = change.Path;
            Kind = change.Kind;
            Marker = change.Kind switch
            {
                SnapshotFileChangeKind.Added => "+",
                SnapshotFileChangeKind.Modified => "~",
                SnapshotFileChangeKind.Deleted => "-",
                _ => "?"
            };
            SizeDelta = UiFormat.FormatSignedBytes(change.SizeDeltaBytes);
        }

        public SnapshotFileChange Change { get; }
        public string Path { get; }
        public SnapshotFileChangeKind Kind { get; }
        public string Marker { get; }
        public string SizeDelta { get; }
        public bool IsAdded => Kind == SnapshotFileChangeKind.Added;
        public bool IsModified => Kind == SnapshotFileChangeKind.Modified;
        public bool IsDeleted => Kind == SnapshotFileChangeKind.Deleted;
    }

    public sealed class DiffPreviewTreeNode : ViewModelBase
    {
        private const string ArchiveIcon = "M2 4 H14 V14 H2 Z M1 2 H15 V5 H1 Z M7 7 H9 V11 H7 Z";
        private const string CodeIcon = "M5 3 L1 8 L5 13 M11 3 L15 8 L11 13 M9 2 L7 14";
        private const string DataIcon = "M3 4 C3 1 13 1 13 4 C13 7 3 7 3 4 M3 4 V12 C3 15 13 15 13 12 V4 M3 8 C3 11 13 11 13 8";
        private const string DocumentIcon = "M3 1 H10 L14 5 V15 H3 Z M10 1 V5 H14 M5 8 H12 M5 11 H12";
        private const string ImageIcon = "M2 2 H14 V14 H2 Z M4 11 L7 8 L9 10 L11 7 L14 11 M5 5 H5.1";
        private const string MarkupIcon = "M5 3 L1 8 L5 13 M11 3 L15 8 L11 13";
        private const string OtherFileIcon = "M3 1 H10 L14 5 V15 H3 Z M10 1 V5 H14";

        private bool _isExpanded;

        private DiffPreviewTreeNode(
            string name,
            string path,
            DiffPreviewTreeNode? parent,
            DiffPreviewFileItem? file)
        {
            Name = name;
            Path = path;
            Parent = parent;
            File = file;
            (FileTypeLabel, FileTypeKind) = file is null
                ? (string.Empty, DiffPreviewFileType.Folder)
                : DescribeFileType(name);
        }

        public string Name { get; }
        public string Path { get; }
        public DiffPreviewTreeNode? Parent { get; }
        public DiffPreviewFileItem? File { get; }
        public ObservableCollection<DiffPreviewTreeNode> Children { get; } = [];
        public bool IsFolder => File is null;
        public bool IsFile => File is not null;
        public string FileTypeLabel { get; }
        public DiffPreviewFileType FileTypeKind { get; }
        public string FileTypeIconData => FileTypeKind switch
        {
            DiffPreviewFileType.Code => CodeIcon,
            DiffPreviewFileType.Markup => MarkupIcon,
            DiffPreviewFileType.Data => DataIcon,
            DiffPreviewFileType.Image => ImageIcon,
            DiffPreviewFileType.Document => DocumentIcon,
            DiffPreviewFileType.Archive => ArchiveIcon,
            _ => OtherFileIcon
        };
        public string Marker => File?.Marker ?? string.Empty;
        public string SizeDelta => File?.SizeDelta ?? string.Empty;
        public bool HasSizeDelta => File?.Change.SizeDeltaBytes != 0;
        public bool IsAdded => File?.IsAdded == true;
        public bool IsModified => File?.IsModified == true;
        public bool IsDeleted => File?.IsDeleted == true;

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetField(ref _isExpanded, value);
        }

        public void ExpandAncestors()
        {
            for (DiffPreviewTreeNode? node = Parent; node is not null; node = node.Parent)
                node.IsExpanded = true;
        }

        public static IReadOnlyList<DiffPreviewTreeNode> Build(
            IEnumerable<DiffPreviewFileItem> files,
            bool expandAll)
        {
            var roots = new List<DiffPreviewTreeNode>();
            var folders = new Dictionary<string, DiffPreviewTreeNode>(StringComparer.Ordinal);

            foreach (DiffPreviewFileItem file in files.OrderBy(static item => item.Path, StringComparer.Ordinal))
            {
                string normalizedPath = file.Path.Replace('\\', '/').Trim('/');
                string[] parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                ObservableCollection<DiffPreviewTreeNode>? children = null;
                DiffPreviewTreeNode? parent = null;
                string folderPath = string.Empty;
                for (int index = 0; index < parts.Length - 1; index++)
                {
                    folderPath = folderPath.Length == 0 ? parts[index] : $"{folderPath}/{parts[index]}";
                    if (!folders.TryGetValue(folderPath, out DiffPreviewTreeNode? folder))
                    {
                        folder = new DiffPreviewTreeNode(parts[index], folderPath, parent, null)
                        {
                            IsExpanded = expandAll || parent is null
                        };
                        if (children is null)
                            roots.Add(folder);
                        else
                            children.Add(folder);
                        folders.Add(folderPath, folder);
                    }

                    parent = folder;
                    children = folder.Children;
                }

                var fileNode = new DiffPreviewTreeNode(parts[^1], normalizedPath, parent, file);
                if (children is null)
                    roots.Add(fileNode);
                else
                    children.Add(fileNode);
            }

            SortNodes(roots);
            return roots;
        }

        private static void SortNodes(IList<DiffPreviewTreeNode> nodes)
        {
            var sorted = nodes
                .OrderByDescending(static node => node.IsFolder)
                .ThenBy(static node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static node => node.Name, StringComparer.Ordinal)
                .ToList();
            nodes.Clear();
            foreach (DiffPreviewTreeNode node in sorted)
            {
                SortNodes(node.Children);
                nodes.Add(node);
            }
        }

        private static (string Label, DiffPreviewFileType Kind) DescribeFileType(string fileName)
        {
            string lowerName = fileName.ToLowerInvariant();
            string extension = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            if (extension.Length == 0)
            {
                return lowerName switch
                {
                    "dockerfile" => ("DKR", DiffPreviewFileType.Code),
                    "makefile" => ("MK", DiffPreviewFileType.Code),
                    ".gitignore" or ".gitattributes" or ".editorconfig" => ("CFG", DiffPreviewFileType.Data),
                    "license" or "readme" => ("TXT", DiffPreviewFileType.Document),
                    _ => ("•", DiffPreviewFileType.Other)
                };
            }

            return extension switch
            {
                ".cs" => ("C#", DiffPreviewFileType.Code),
                ".fs" or ".fsx" => ("F#", DiffPreviewFileType.Code),
                ".vb" => ("VB", DiffPreviewFileType.Code),
                ".js" or ".mjs" or ".cjs" => ("JS", DiffPreviewFileType.Code),
                ".ts" or ".tsx" => ("TS", DiffPreviewFileType.Code),
                ".jsx" => ("JSX", DiffPreviewFileType.Code),
                ".py" => ("PY", DiffPreviewFileType.Code),
                ".rs" => ("RS", DiffPreviewFileType.Code),
                ".go" => ("GO", DiffPreviewFileType.Code),
                ".java" => ("JV", DiffPreviewFileType.Code),
                ".kt" or ".kts" => ("KT", DiffPreviewFileType.Code),
                ".swift" => ("SW", DiffPreviewFileType.Code),
                ".c" or ".h" => ("C", DiffPreviewFileType.Code),
                ".cc" or ".cpp" or ".cxx" or ".hpp" => ("C++", DiffPreviewFileType.Code),
                ".sh" or ".bash" or ".zsh" or ".ps1" => ("$", DiffPreviewFileType.Code),
                ".html" or ".htm" => ("<>", DiffPreviewFileType.Markup),
                ".xml" or ".xaml" or ".axaml" or ".svg" or ".csproj" or ".props" or ".targets" =>
                    ("XML", DiffPreviewFileType.Markup),
                ".css" or ".scss" or ".sass" or ".less" => ("CSS", DiffPreviewFileType.Markup),
                ".json" or ".jsonc" => ("{}", DiffPreviewFileType.Data),
                ".yml" or ".yaml" => ("YML", DiffPreviewFileType.Data),
                ".toml" => ("TML", DiffPreviewFileType.Data),
                ".ini" or ".config" or ".conf" or ".env" => ("CFG", DiffPreviewFileType.Data),
                ".sql" or ".db" or ".sqlite" or ".sqlite3" => ("DB", DiffPreviewFileType.Data),
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico" or ".tif" or ".tiff" =>
                    ("IMG", DiffPreviewFileType.Image),
                ".md" or ".mdx" => ("MD", DiffPreviewFileType.Document),
                ".txt" or ".log" or ".csv" or ".tsv" => ("TXT", DiffPreviewFileType.Document),
                ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" or ".xz" =>
                    ("ZIP", DiffPreviewFileType.Archive),
                ".sln" or ".slnx" => ("VS", DiffPreviewFileType.Code),
                _ => ("•", DiffPreviewFileType.Other)
            };
        }
    }

    public enum DiffPreviewFileType
    {
        Folder,
        Code,
        Markup,
        Data,
        Image,
        Document,
        Archive,
        Other
    }

    public sealed class DiffPreviewLineItem
    {
        private DiffPreviewLineItem(
            string oldLineNumber,
            string newLineNumber,
            string marker,
            string content,
            char kind)
        {
            OldLineNumber = oldLineNumber;
            NewLineNumber = newLineNumber;
            Marker = marker;
            Content = content;
            IsAdded = kind == '+';
            IsDeleted = kind == '-';
            IsHunk = kind == '@';
            IsNotice = kind == '!';
        }

        public string OldLineNumber { get; }
        public string NewLineNumber { get; }
        public string Marker { get; }
        public string Content { get; }
        public bool IsAdded { get; }
        public bool IsDeleted { get; }
        public bool IsHunk { get; }
        public bool IsNotice { get; }

        public static DiffPreviewLineItem Notice(string content) =>
            new(string.Empty, string.Empty, "!", content, '!');

        public static IReadOnlyList<DiffPreviewLineItem> ParseUnified(string? diffText)
        {
            if (string.IsNullOrWhiteSpace(diffText))
                return [];

            string normalized = diffText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            var result = new List<DiffPreviewLineItem>(lines.Length);
            int oldLine = 1;
            int newLine = 1;

            foreach (string line in lines)
            {
                if (line.StartsWith("--- ", StringComparison.Ordinal) ||
                    line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    TryReadHunkStarts(line, out oldLine, out newLine);
                    result.Add(new DiffPreviewLineItem(string.Empty, string.Empty, string.Empty, line, '@'));
                    continue;
                }

                char marker = line.Length == 0 ? ' ' : line[0];
                string content = line.Length == 0 ? string.Empty : line[1..];
                if (marker == '+')
                {
                    result.Add(new DiffPreviewLineItem(
                        string.Empty,
                        newLine.ToString(CultureInfo.InvariantCulture),
                        "+",
                        content,
                        marker));
                    newLine++;
                    continue;
                }

                if (marker == '-')
                {
                    result.Add(new DiffPreviewLineItem(
                        oldLine.ToString(CultureInfo.InvariantCulture),
                        string.Empty,
                        "-",
                        content,
                        marker));
                    oldLine++;
                    continue;
                }

                result.Add(new DiffPreviewLineItem(
                    oldLine.ToString(CultureInfo.InvariantCulture),
                    newLine.ToString(CultureInfo.InvariantCulture),
                    string.Empty,
                    marker == ' ' ? content : line,
                    ' '));
                oldLine++;
                newLine++;
            }

            return result;
        }

        private static void TryReadHunkStarts(string header, out int oldLine, out int newLine)
        {
            oldLine = ReadStartAfter(header, '-');
            newLine = ReadStartAfter(header, '+');
        }

        private static int ReadStartAfter(string header, char prefix)
        {
            int start = header.IndexOf(prefix);
            if (start < 0)
                return 1;

            start++;
            int end = start;
            while (end < header.Length && char.IsDigit(header[end]))
                end++;
            return int.TryParse(header.AsSpan(start, end - start), CultureInfo.InvariantCulture, out int value)
                ? Math.Max(1, value)
                : 1;
        }
    }

    public sealed record DiffPreviewKindFilterItem(string Label, SnapshotFileChangeKind? Kind);

    public class BackupSnapshotItem : ViewModelBase
    {
        public string Id { get; set; } = string.Empty;
        public int SnapshotId { get; set; }
        public DateTime Timestamp
        {
            get; set;
        }
        public long SizeBytes
        {
            get; set;
        }
        private bool _isProtected;

        /// <summary>Run trigger type, e.g. "Auto" or "Manual".</summary>
        public string Type { get; set; } = "Manual";

        /// <summary>Status, e.g. "Completed", "Failed".</summary>
        public string Status { get; set; } = "Completed";

        /// <summary>Label shown inside the tag pill.</summary>
        public string? Label
        {
            get; set;
        }

        /// <summary>Localized backup mode label for display (Full/Incremental/Imported context).</summary>
        public string TypeLabel { get; set; } = string.Empty;
        public string ModeChipLabel { get; set; } = string.Empty;
        public string EncryptionChipLabel { get; set; } = string.Empty;
        public string RetentionDefaultLabel { get; set; } = "Retention: eligible for pruning";
        public string RetentionProtectedLabel { get; set; } = "Retention: kept (protected)";
        public string RetentionOutcomeLabel => IsProtected ? RetentionProtectedLabel : RetentionDefaultLabel;

        /// <summary>Optional project id this snapshot belongs to; null for global.</summary>
        public string? ProjectId
        {
            get; set;
        }

        /// <summary>Destination endpoint that stored this backup.</summary>
        public string DestinationDisplay { get; set; } = string.Empty;
        public string BackupRelativePath { get; set; } = string.Empty;
        public string DestinationRootPath { get; set; } = string.Empty;
        public string DestinationAlias { get; set; } = string.Empty;
        public string DiffSummaryDisplay { get; set; } = string.Empty;
        public string DiffTopPathsDisplay { get; set; } = string.Empty;
        public bool HasDiffTopPaths
        {
            get; set;
        }
        public bool CanOpenDiffDetails
        {
            get; set;
        }
        public int DiffAdded
        {
            get; set;
        }
        public int DiffModified
        {
            get; set;
        }
        public int DiffDeleted
        {
            get; set;
        }
        public long DiffNetBytes
        {
            get; set;
        }
        public string DiffTopPathsJson { get; set; } = "[]";

        public string SizeFormatted => FormatSize(SizeBytes);
        public string TimelineSelectionLabel =>
            $"{Timestamp:yyyy-MM-dd HH:mm} \u00b7 {TypeLabel} \u00b7 {SizeFormatted}";

        public bool IsImported
        {
            get; set;
        }
        public bool IsEncrypted
        {
            get; set;
        }

        /// <summary>Localized label for the imported tag.</summary>
        public string ImportedLabel { get; set; } = string.Empty;
        public string EncryptionLabel { get; set; } = string.Empty;
        public string OriginMachineName { get; set; } = string.Empty;

        public bool IsProtected
        {
            get => _isProtected;
            set
            {
                if (_isProtected != value)
                {
                    _isProtected = value;
                    OnPropertyChanged(nameof(IsProtected));
                    OnPropertyChanged(nameof(RetentionOutcomeLabel));
                }
            }
        }

        // ---------- Tag pill background color ----------

        private static readonly IBrush DefaultBrush =
            new ImmutableSolidColorBrush(Color.Parse("#22FFFFFF"));

        // Auto snapshots: blue-ish
        private static readonly IBrush AutoBrush =
            new ImmutableSolidColorBrush(Color.Parse("#333A7AFE"));

        // Manual snapshots: purple-ish
        private static readonly IBrush ManualBrush =
            new ImmutableSolidColorBrush(Color.Parse("#334568F2"));

        // Imported snapshots: teal-ish
        private static readonly IBrush ImportedBrush =
            new ImmutableSolidColorBrush(Color.Parse("#3346C6A1"));

        // Failed snapshots: red-ish
        private static readonly IBrush FailedBrush =
            new ImmutableSolidColorBrush(Color.Parse("#33FF4B4B"));

        public IBrush TagBackground
        {
            get
            {
                if (string.Equals(Status, "Failed", StringComparison.OrdinalIgnoreCase))
                    return FailedBrush;

                if (string.Equals(Type, "Auto", StringComparison.OrdinalIgnoreCase))
                    return AutoBrush;

                if (string.Equals(Type, "Manual", StringComparison.OrdinalIgnoreCase))
                    return ManualBrush;

                return DefaultBrush;
            }
        }

        public static IBrush ImportedTagBackground => ImportedBrush;

        internal static string FormatSize(long bytes) =>
            UiFormat.FormatBytes(bytes);
    }

    public class SnapshotProjectGroup : ViewModelBase
    {
        internal const int DefaultPageSize = 20;

        private readonly List<BackupSnapshotItem> _allSnapshots = [];
        private readonly RelayCommand _loadMoreSnapshotsCommand;

        public SnapshotProjectGroup()
        {
            _loadMoreSnapshotsCommand = new RelayCommand(
                _ => LoadMoreSnapshots(),
                _ => HasMoreSnapshots);
            LoadMoreSnapshotsCommand = _loadMoreSnapshotsCommand;
        }

        public string? ProjectId
        {
            get; set;
        }
        public string ProjectName { get; set; } = string.Empty;
        public string ProjectTagsDisplay { get; set; } = string.Empty;
        public ObservableCollection<ProjectTagChip> ProjectTagChips { get; } = [];
        public bool HasProjectTags => ProjectTagChips.Count > 0;
        public string Summary { get; set; } = string.Empty;
        public string TotalSizeFormatted { get; set; } = string.Empty;
        public string LatestBackupDisplay { get; set; } = string.Empty;
        public IBrush AccentBrush { get; set; } = new ImmutableSolidColorBrush(Color.Parse("#33405A"));
        public bool IsExpanded
        {
            get; set;
        }

        public ObservableCollection<BackupSnapshotItem> Snapshots
        {
            get;
        } =
            [];

        public ICommand LoadMoreSnapshotsCommand { get; }
        public int TotalSnapshotCount => _allSnapshots.Count;
        public int VisibleSnapshotCount => Snapshots.Count;
        public int RemainingSnapshotCount => Math.Max(0, TotalSnapshotCount - VisibleSnapshotCount);
        public bool HasMoreSnapshots => RemainingSnapshotCount > 0;

        internal void SetSnapshots(IEnumerable<BackupSnapshotItem> snapshots, int initialCount = DefaultPageSize)
        {
            ArgumentNullException.ThrowIfNull(snapshots);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCount);

            _allSnapshots.Clear();
            _allSnapshots.AddRange(snapshots);
            Snapshots.Clear();
            AppendSnapshots(initialCount);
        }

        public void LoadMoreSnapshots(int pageSize = DefaultPageSize)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

            AppendSnapshots(pageSize);
        }

        private void AppendSnapshots(int count)
        {
            int targetCount = Math.Min(TotalSnapshotCount, VisibleSnapshotCount + count);
            for (int index = VisibleSnapshotCount; index < targetCount; index++)
                Snapshots.Add(_allSnapshots[index]);

            OnPropertiesChanged(
                nameof(TotalSnapshotCount),
                nameof(VisibleSnapshotCount),
                nameof(RemainingSnapshotCount),
                nameof(HasMoreSnapshots));
            _loadMoreSnapshotsCommand.RaiseCanExecuteChanged();
        }
    }

    public sealed class BackupsProjectSortOption
    {
        public BackupsProjectSortOption(string id, string label)
        {
            Id = id;
            Label = label;
        }

        public string Id
        {
            get;
        }
        public string Label
        {
            get;
        }

        public override string ToString() => Label;
    }

    public class ProjectBackupItem : ViewModelBase
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ExternalId { get; set; } = string.Empty;
        private string _projectTagsCsv = string.Empty;
        public string ProjectTagsCsv
        {
            get => _projectTagsCsv;
            set
            {
                if (!SetField(ref _projectTagsCsv, value ?? string.Empty, nameof(ProjectTagsCsv)))
                    return;

                RebuildProjectTags();
                OnPropertyChanged(nameof(HasProjectTags));
                OnPropertyChanged(nameof(ProjectTagsDisplay));
                OnPropertyChanged(nameof(PrimaryTagSortKey));
            }
        }

        public ObservableCollection<ProjectTagChip> ProjectTagChips { get; } = [];
        public bool HasProjectTags => ProjectTagChips.Count > 0;
        public string ProjectTagsDisplay => string.Join(", ", ProjectTagChips.Select(tag => tag.Value));
        public string PrimaryTagSortKey => ProjectTagChips.FirstOrDefault()?.Value ?? string.Empty;

        public DateTime? LastBackupTime
        {
            get; set;
        }
        public int SnapshotCount
        {
            get; set;
        }
        public long TotalSizeBytes
        {
            get; set;
        }
        public long? StorageDeltaBytes
        {
            get; set;
        }
        private string _restoreReadinessLabel = string.Empty;
        public string RestoreReadinessLabel
        {
            get => _restoreReadinessLabel;
            set => SetField(ref _restoreReadinessLabel, value ?? string.Empty, nameof(RestoreReadinessLabel));
        }

        private string _restoreReadinessReason = string.Empty;
        public string RestoreReadinessReason
        {
            get => _restoreReadinessReason;
            set => SetField(ref _restoreReadinessReason, value ?? string.Empty, nameof(RestoreReadinessReason));
        }

        private IBrush _restoreReadinessBrush = new ImmutableSolidColorBrush(Color.Parse("#7F8FA8"));
        public IBrush RestoreReadinessBrush
        {
            get => _restoreReadinessBrush;
            set => SetField(ref _restoreReadinessBrush, value, nameof(RestoreReadinessBrush));
        }

        public bool AutoBackupEnabled
        {
            get => _autoBackupEnabled;
            set
            {
                if (!SetField(ref _autoBackupEnabled, value, nameof(AutoBackupEnabled)))
                    return;
                AutoBackupChanged?.Invoke(this);
            }
        }

        private bool _autoBackupEnabled = true;
        public Action<ProjectBackupItem>? AutoBackupChanged
        {
            get; set;
        }
        public Action<ProjectBackupItem>? PreferredDestinationChanged
        {
            get; set;
        }
        public Action<ProjectBackupItem>? EncryptionPolicyChanged
        {
            get; set;
        }
        public Action<ProjectBackupItem>? RestoreModeChanged
        {
            get; set;
        }
        public Action<ProjectBackupItem>? VerificationPolicyChanged
        {
            get; set;
        }

        private string _preferredDestinationId = string.Empty;
        public string PreferredDestinationId
        {
            get => _preferredDestinationId;
            set => SetField(ref _preferredDestinationId, value ?? string.Empty, nameof(PreferredDestinationId));
        }

        private DestinationOption? _preferredDestinationOption;
        public DestinationOption? PreferredDestinationOption
        {
            get => _preferredDestinationOption;
            set
            {
                // Ignore transient null selection events fired while destination options refresh.
                // Real "Auto" selection is represented by a non-null option with empty Id.
                if (value is null)
                    return;

                if (ReferenceEquals(_preferredDestinationOption, value))
                    return;

                string previousId = _preferredDestinationOption?.Id ?? string.Empty;
                _preferredDestinationOption = value;
                OnPropertyChanged(nameof(PreferredDestinationOption));

                string nextId = value.Id ?? string.Empty;
                if (string.Equals(previousId, nextId, StringComparison.OrdinalIgnoreCase))
                    return;

                PreferredDestinationId = nextId;
                PreferredDestinationChanged?.Invoke(this);
            }
        }

        private string _preferredDestinationDisplay = string.Empty;
        public string PreferredDestinationDisplay
        {
            get => _preferredDestinationDisplay;
            set => SetField(ref _preferredDestinationDisplay, value ?? string.Empty, nameof(PreferredDestinationDisplay));
        }

        public void SetPreferredDestinationOption(DestinationOption? option)
        {
            SetOptionSilently(ref _preferredDestinationOption, option, nameof(PreferredDestinationOption));
        }

        private string _encryptionPolicy = ProjectEncryptionPolicy.Inherit;
        public string EncryptionPolicy
        {
            get => _encryptionPolicy;
            set => SetField(ref _encryptionPolicy, ProjectEncryptionPolicy.Normalize(value), nameof(EncryptionPolicy));
        }

        private string _encryptionKeyRef = string.Empty;
        public string EncryptionKeyRef
        {
            get => _encryptionKeyRef;
            set => SetField(ref _encryptionKeyRef, value ?? string.Empty, nameof(EncryptionKeyRef));
        }

        private EncryptionPolicyOption? _encryptionPolicyOption;
        public EncryptionPolicyOption? EncryptionPolicyOption
        {
            get => _encryptionPolicyOption;
            set
            {
                if (!SetField(ref _encryptionPolicyOption, value, nameof(EncryptionPolicyOption)))
                    return;

                // Ignore transient null selection events fired while option sources refresh.
                // Real "inherit" selection is represented by a non-null option with Id="inherit".
                if (value is null)
                    return;

                EncryptionPolicy = value.Id;
                EncryptionPolicyChanged?.Invoke(this);
            }
        }

        public void SetEncryptionPolicyOption(EncryptionPolicyOption? option)
        {
            SetOptionSilently(ref _encryptionPolicyOption, option, nameof(EncryptionPolicyOption));
        }

        private string _restoreMode = ProjectRestoreMode.Direct;
        public string RestoreMode
        {
            get => _restoreMode;
            set => SetField(ref _restoreMode, ProjectRestoreMode.Normalize(value), nameof(RestoreMode));
        }

        private RestoreModeOption? _restoreModeOption;
        public RestoreModeOption? RestoreModeOption
        {
            get => _restoreModeOption;
            set
            {
                if (!SetField(ref _restoreModeOption, value, nameof(RestoreModeOption)))
                    return;

                if (value is null)
                    return;

                RestoreMode = value.Id;
                RestoreModeChanged?.Invoke(this);
            }
        }

        public void SetRestoreModeOption(RestoreModeOption? option)
        {
            SetOptionSilently(ref _restoreModeOption, option, nameof(RestoreModeOption));
        }

        private string _verificationPolicy = ProjectVerificationPolicy.Always;
        public string VerificationPolicy
        {
            get => _verificationPolicy;
            set => SetField(ref _verificationPolicy, ProjectVerificationPolicy.Normalize(value), nameof(VerificationPolicy));
        }

        private VerificationPolicyOption? _verificationPolicyOption;
        public VerificationPolicyOption? VerificationPolicyOption
        {
            get => _verificationPolicyOption;
            set
            {
                if (!SetField(ref _verificationPolicyOption, value, nameof(VerificationPolicyOption)))
                    return;

                if (value is null)
                    return;

                VerificationPolicy = value.Id;
                VerificationPolicyChanged?.Invoke(this);
            }
        }

        public void SetVerificationPolicyOption(VerificationPolicyOption? option)
        {
            SetOptionSilently(ref _verificationPolicyOption, option, nameof(VerificationPolicyOption));
        }

        private void SetOptionSilently<T>(ref T? field, T? option, string propertyName)
            where T : class
        {
            if (ReferenceEquals(field, option))
                return;

            field = option;
            OnPropertyChanged(propertyName);
        }

        private string _effectiveEncryptionDisplay = string.Empty;
        public string EffectiveEncryptionDisplay
        {
            get => _effectiveEncryptionDisplay;
            set => SetField(ref _effectiveEncryptionDisplay, value ?? string.Empty, nameof(EffectiveEncryptionDisplay));
        }

        private bool _hasEncryptionSecret;
        public bool HasEncryptionSecret
        {
            get => _hasEncryptionSecret;
            set => SetField(ref _hasEncryptionSecret, value, nameof(HasEncryptionSecret));
        }

        private string _encryptionSecretStatus = string.Empty;
        public string EncryptionSecretStatus
        {
            get => _encryptionSecretStatus;
            set => SetField(ref _encryptionSecretStatus, value ?? string.Empty, nameof(EncryptionSecretStatus));
        }

        // Avatar
        public string AvatarInitials { get; private set; } = string.Empty;
        public string AvatarColor { get; private set; } = "#33405A";
        public string? AvatarImagePath
        {
            get; private set;
        }
        public bool HasCustomAvatar => !string.IsNullOrWhiteSpace(AvatarImagePath);

        public void SetAvatarFromNameAndStore(string name, string projectPath, string? externalId)
        {
            AvatarInitials = ComputeInitials(name);
            AvatarColor = AvatarColorProvider.GetColor(name, projectPath, externalId);
            AvatarImagePath = AvatarStore.GetAvatarForProject(projectPath);
        }

        private static string ComputeInitials(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "?";

            string[] parts = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
                return $"{parts[0][0]}{parts[1][0]}".ToUpperInvariant();

            if (name.Length >= 2)
                return name.Substring(0, 2).ToUpperInvariant();

            return name.Substring(0, 1).ToUpperInvariant();
        }

        public string LastBackupDisplay =>
            LastBackupTime.HasValue
                ? LastBackupTime.Value.ToString("yyyy-MM-dd HH:mm")
                : LocalizationProvider.Service?.GetString("Backups.Summary.NoBackups") ?? "No backups yet";

        public string TotalSizeFormatted =>
            BackupSnapshotItem.FormatSize(TotalSizeBytes);

        public bool HasStorageDelta => StorageDeltaBytes.HasValue;

        public string StorageDeltaFormatted
        {
            get
            {
                if (!StorageDeltaBytes.HasValue)
                    return "Δ -";

                long value = StorageDeltaBytes.Value;
                if (Math.Abs(value) < 1024)
                    return "Δ ~0 B";

                string sign = value >= 0 ? "+" : "-";
                return $"Δ {sign}{BackupSnapshotItem.FormatSize(Math.Abs(value))}";
            }
        }

        private void RebuildProjectTags()
        {
            ProjectTagChips.Clear();
            foreach (ProjectTagChip chip in ProjectTagAppearance.CreateChips(_projectTagsCsv))
                ProjectTagChips.Add(chip);
        }
    }

    public class DestinationStatusItem : ViewModelBase
    {
        private static string L(string key, string fallback) =>
            LocalizationProvider.Service?.GetString(key) ?? fallback;

        private static readonly IBrush SuccessBrush = new ImmutableSolidColorBrush(Color.Parse("#22CC88"));
        private static readonly IBrush WarningBrush = new ImmutableSolidColorBrush(Color.Parse("#FFB84C"));
        private static readonly IBrush ErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#FF6B6B"));
        private static readonly IBrush InfoBrush = new ImmutableSolidColorBrush(Color.Parse("#8E9BAF"));

        public string Id { get; set; } = string.Empty;
        public string Alias { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;

        private BackupsViewModel.DestinationStatus _status = BackupsViewModel.DestinationStatus.None;
        public BackupsViewModel.DestinationStatus Status
        {
            get => _status;
            set
            {
                if (_status == value)
                    return;
                _status = value;

                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(IsChecking));
            }
        }

        public string StatusDisplay => _status switch
        {
            BackupsViewModel.DestinationStatus.Pending => L("Backups.Destinations.Pending", "Pending"),
            BackupsViewModel.DestinationStatus.Inactive => L("Backups.Destinations.Inactive", "Inactive"),
            BackupsViewModel.DestinationStatus.Reachable => L("Destinations.Test.Reachable", "Reachable"),
            BackupsViewModel.DestinationStatus.ReadOnly => L("Destinations.Test.ReadOnly", "Read-only"),
            BackupsViewModel.DestinationStatus.Unavailable => L("Destinations.Test.Unavailable", "Unavailable"),
            BackupsViewModel.DestinationStatus.None => string.Empty,

            _ => string.Empty
        };
        public void RefreshLocalization()
        {
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(LastCheckedDisplay));
        }

        public bool IsChecking => _status == BackupsViewModel.DestinationStatus.Pending;

        private bool _isActive = true;
        public bool IsActive
        {
            get => _isActive;
            set => SetField(ref _isActive, value);
        }

        private bool _isConfigurable = true;
        public bool IsConfigurable
        {
            get => _isConfigurable;
            set => SetField(ref _isConfigurable, value);
        }

        private BackupsViewModel.SeverityStatus _severity = BackupsViewModel.SeverityStatus.None;
        public BackupsViewModel.SeverityStatus Severity
        {
            get => _severity;
            set
            {
                if (SetField(ref _severity, value))
                {
                    OnPropertyChanged(nameof(ReachabilityBrush));
                }
            }
        }

        private IBrush _dotBrush = InfoBrush;
        public IBrush DotBrush
        {
            get => _dotBrush;
            set => SetField(ref _dotBrush, value);
        }

        public IBrush ReachabilityBrush
        {
            get
            {
                return Severity switch
                {
                    BackupsViewModel.SeverityStatus.Success => SuccessBrush,
                    BackupsViewModel.SeverityStatus.Warning => WarningBrush,
                    BackupsViewModel.SeverityStatus.Error => ErrorBrush,
                    BackupsViewModel.SeverityStatus.None => InfoBrush,
                    _ => InfoBrush
                };
            }
        }

        private DateTime? _lastCheckedUtc;
        public DateTime? LastCheckedUtc
        {
            get => _lastCheckedUtc;
            set
            {
                if (SetField(ref _lastCheckedUtc, value))
                {
                    OnPropertyChanged(nameof(LastCheckedDisplay));
                }
            }
        }

        public string LastCheckedDisplay
        {
            get
            {
                if (!LastCheckedUtc.HasValue)
                {
                    return LocalizationProvider.Service?.GetString("Destinations.Status.LastCheckedNever")
                           ?? "Last checked: never";
                }

                string label = LocalizationProvider.Service?.GetString("Destinations.Status.LastChecked")
                           ?? "Last checked: {0}";
                string local = LastCheckedUtc.Value.ToLocalTime().ToString("HH:mm:ss");
                return string.Format(CultureInfo.CurrentCulture, label, local);
            }
        }

        private string _storedBytesText = string.Empty;
        public string StoredBytesText
        {
            get => _storedBytesText;
            set
            {
                if (SetField(ref _storedBytesText, value))
                {
                    OnPropertyChanged(nameof(HasStoredBytesText));
                }
            }
        }

        public bool HasStoredBytesText => !string.IsNullOrWhiteSpace(StoredBytesText);

        private string _cleanupSuggestionText = string.Empty;
        public string CleanupSuggestionText
        {
            get => _cleanupSuggestionText;
            set
            {
                if (SetField(ref _cleanupSuggestionText, value))
                {
                    OnPropertyChanged(nameof(HasCleanupSuggestionText));
                }
            }
        }

        public bool HasCleanupSuggestionText => !string.IsNullOrWhiteSpace(CleanupSuggestionText);

        public static string GetId(BackupDestination dest) =>
            DestinationIdentityService.GetId(dest);
    }

    public class BackupProgressItem : ViewModelBase
    {
        private const string StageBackingUp = "BackingUp";
        private const string StageCancelling = "Cancelling";
        private const string StageCompleted = "Completed";
        private const string StageCompressing = "Compressing";
        private const string StageCopying = "Copying";
        private const string StageDeleting = "Deleting";
        private const string StageHashing = "Hashing";
        private const string StagePreparing = "Preparing";
        private const string StageRestoring = "Restoring";
        private const string StageUploading = "Uploading";
        private const string StageWorking = "Working";

        private static string L(string key, string fallback) =>
            LocalizationProvider.Service?.GetString(key) ?? fallback;

        public string ProjectId { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;

        public Action<BackupProgressItem?>? CancelRequested
        {
            get; set;
        }

        private bool _allowCancel = true;
        public bool AllowCancel
        {
            get => _allowCancel;
            set
            {
                if (_allowCancel == value)
                    return;

                _allowCancel = value;
                OnPropertyChanged(nameof(AllowCancel));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(IsIndeterminate));
                NotifyProgressPresentationChanged();
            }
        }

        public ICommand CancelCommand
        {
            get;
        }

        public BackupProgressItem()
        {
            CancelCommand = new RelayCommand(_ => CancelRequested?.Invoke(this));
        }

        private string _destinationLabel = string.Empty;
        public string DestinationLabel
        {
            get => _destinationLabel;
            set
            {
                if (_destinationLabel == value)
                    return;

                _destinationLabel = value ?? string.Empty;
                OnPropertyChanged(nameof(DestinationLabel));
                OnPropertyChanged(nameof(DestinationDisplay));
                OnPropertyChanged(nameof(HasDestinationDisplay));
            }
        }

        public string DestinationDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_destinationLabel))
                    return string.Empty;

                string prefix = L("Projects.List.DestinationPrefix", "Destination: ");
                return $"{prefix}{_destinationLabel}";
            }
        }

        public bool HasDestinationDisplay => !string.IsNullOrWhiteSpace(DestinationDisplay);

        private string _policyText = string.Empty;
        public string PolicyText
        {
            get => _policyText;
            set
            {
                string normalized = value ?? string.Empty;
                if (_policyText == normalized)
                    return;

                _policyText = normalized;
                OnPropertyChanged(nameof(PolicyText));
                OnPropertyChanged(nameof(HasPolicyText));
            }
        }

        public bool HasPolicyText => !string.IsNullOrWhiteSpace(PolicyText);

        private double _progress;
        private double _displayProgress;
        private string _lastStageKey = string.Empty;
        private DateTime _stageStartUtc = DateTime.UtcNow;
        public double Progress
        {
            get => _progress;
            set
            {
                if (Math.Abs(_progress - value) < 0.0001)
                    return;

                _progress = value;
                UpdateDisplayProgress();
                OnPropertyChanged(nameof(Progress));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(IsEstimate));
                NotifyProgressPresentationChanged();
            }
        }

        public double DisplayProgress => _displayProgress;

        public bool HasProgress => HasRawProgress;

        private bool HasRawProgress => _progress > 0.1d;

        private string _currentFile = string.Empty;
        public string CurrentFile
        {
            get => _currentFile;
            set
            {
                if (_currentFile == value)
                    return;

                _currentFile = value ?? string.Empty;
                OnPropertyChanged(nameof(CurrentFile));
                OnPropertyChanged(nameof(HasCurrentFile));
                OnPropertyChanged(nameof(CurrentFileDisplay));
                OnPropertyChanged(nameof(HasCurrentFileDisplay));
                OnPropertyChanged(nameof(StageLabel));
                OnPropertyChanged(nameof(IsEstimate));
                UpdateDisplayProgress();
                NotifyProgressPresentationChanged();
            }
        }

        public bool HasCurrentFile => !string.IsNullOrWhiteSpace(_currentFile);

        private string _etaText = string.Empty;
        private string _lastProgressDetail = string.Empty;
        public string EtaText
        {
            get => _etaText;
            set
            {
                if (_etaText == value)
                    return;

                _etaText = value ?? string.Empty;
                OnPropertyChanged(nameof(EtaText));
                OnPropertyChanged(nameof(HasEtaText));
                OnPropertyChanged(nameof(EtaDisplay));
                OnPropertyChanged(nameof(HasEtaDisplay));
                OnPropertyChanged(nameof(IsEstimate));
                string detail = ExtractProgressDetail(EtaDisplay);
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    _lastProgressDetail = detail;
                }
                OnPropertyChanged(nameof(CurrentFileDisplay));
                OnPropertyChanged(nameof(HasCurrentFileDisplay));
                OnPropertyChanged(nameof(StageLabel));
                UpdateDisplayProgress();
                NotifyProgressPresentationChanged();
            }
        }

        public bool HasEtaText => !string.IsNullOrWhiteSpace(_etaText);

        public string EtaDisplay => NormalizeEtaText(_etaText);

        public bool HasEtaDisplay => !string.IsNullOrWhiteSpace(EtaDisplay);

        public bool IsEstimate =>
            !HasRawProgress &&
            ContainsToken(_currentFile, L("Backups.Progress.Estimating", "Estimating...")) &&
            HasEtaText;

        public static string EstimateLabel => L("Backups.Preflight.Title", "Backup estimate");

        public string CurrentFileDisplay
        {
            get
            {
                if (TryExtractFileName(_currentFile, out string? fileName))
                    return fileName;

                if (!string.IsNullOrWhiteSpace(_lastProgressDetail) && !ContainsSpeedOrEta(_lastProgressDetail))
                    return _lastProgressDetail;

                return string.Empty;
            }
        }

        public bool HasCurrentFileDisplay => !string.IsNullOrWhiteSpace(CurrentFileDisplay);

        public bool IsCompleted => string.Equals(GetStageKey(), StageCompleted, StringComparison.OrdinalIgnoreCase);

        public bool ShowEta => Progress < 100d && HasEtaDisplay;

        public bool CanCancel => AllowCancel && !IsCompleted;

        public bool ShowPercent => AllowCancel && HasProgress && IsProgressReliable;

        public bool IsIndeterminate => !IsProgressReliable || IsStageIndeterminate;

        public string ProgressLabel =>
            IsProgressReliable && HasProgress
                ? string.Format(CultureInfo.CurrentCulture, "{0:0}%", DisplayProgress)
                : L("Backups.Progress.Estimating", "Estimating...");

        public string StageLabel
        {
            get
            {
                string stageKey = GetStageKey();
                return stageKey switch
                {
                    StageCompleted => L("Backups.Status.Completed", StageCompleted),
                    StageCancelling => L("Backups.Status.Cancelling", "Cancelling..."),
                    StageDeleting => L("Backups.Stage.Deleting", StageDeleting),
                    StageRestoring => L("Backups.Status.Restoring", "Restoring backup..."),
                    StageCompressing => L("Backups.Stage.Compressing", "Compressing archive"),
                    StageUploading => L("Backups.Stage.Uploading", "Uploading archive"),
                    StageHashing => L("Backups.Stage.Hashing", "Hashing files"),
                    StageCopying => L("Backups.Stage.Copying", "Copying files"),
                    StagePreparing => L("Backups.Stage.Preparing", StagePreparing),
                    StageBackingUp => L("Backups.Stage.BackingUp", "Backing up files"),
                    _ => L("Backups.Stage.Working", "Working...")
                };
            }
        }

        public string StageDisplay
            => IsCompleted ? StageLabel : $"{StageLabel} - {FormatElapsed(_stageStartUtc)}";

        public IBrush StageBrush => GetStageBrush();

        private bool IsProgressReliable
        {
            get
            {
                if (!HasRawProgress)
                    return false;

                return true;
            }
        }

        private bool IsStageIndeterminate
            => string.Equals(StageLabel, L("Backups.Stage.Preparing", StagePreparing), StringComparison.OrdinalIgnoreCase);

        private bool IsHashingStage => string.Equals(GetStageKey(), StageHashing, StringComparison.OrdinalIgnoreCase);

        private bool HasCompletionSignal()
        {
            if (ContainsToken(_etaText, StageCompleted))
                return true;

            return ContainsToken(_currentFile, L("Backups.Status.Completed", StageCompleted))
                || ContainsToken(_currentFile, L("Backups.Status.NoChanges", "No changes detected"))
                || ContainsToken(_currentFile, L("Backups.Status.Cancelled", "Cancelled"))
                || ContainsToken(_currentFile, L("Backups.Status.Deleted", "Deleted"));
        }

        private static bool ContainsToken(string? value, string token)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(token))
                return false;

            return value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ExtractFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string trimmed = value.Trim();

            // Drop destination prefix like "[Alias]" if present.
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                int end = trimmed.IndexOf(']');
                if (end >= 0 && end + 1 < trimmed.Length)
                {
                    trimmed = trimmed[(end + 1)..].Trim();
                }
            }

            string candidate = trimmed;
            if (candidate.Contains('\\') || candidate.Contains('/'))
            {
                candidate = Path.GetFileName(candidate);
            }

            return candidate;
        }

        private static bool TryExtractFileName(string value, out string fileName)
        {
            fileName = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (!value.Contains('\\') && !value.Contains('/'))
                return false;

            string extracted = ExtractFileName(value);
            if (string.IsNullOrWhiteSpace(extracted))
                return false;

            fileName = extracted;
            return true;
        }

        private static bool ContainsSpeedOrEta(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return value.Contains("ETA", StringComparison.OrdinalIgnoreCase)
                   || value.Contains("MB/s", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractProgressDetail(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string trimmed = value.Trim();
            if (string.Equals(trimmed, L("Backups.Progress.CopyingRobocopy", "Copying files (robocopy)..."), StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (trimmed.StartsWith("Copying ", StringComparison.OrdinalIgnoreCase))
                return L("Backups.Progress.MovedPrefix", "moved ") + trimmed["Copying ".Length..];
            if (trimmed.StartsWith("Compressing ", StringComparison.OrdinalIgnoreCase))
                return trimmed["Compressing ".Length..];
            if (trimmed.StartsWith("Uploading ", StringComparison.OrdinalIgnoreCase))
                return trimmed["Uploading ".Length..];
            if (trimmed.StartsWith("Hashing ", StringComparison.OrdinalIgnoreCase))
                return trimmed;

            return trimmed;
        }

        private static string NormalizeEtaText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string trimmed = value.Trim();
            if (trimmed.Contains("Waiting for first file", StringComparison.OrdinalIgnoreCase))
                return L("Backups.Progress.WaitingForFirstFile", "Waiting for first file...");

            if (trimmed.Contains("Copying files (robocopy)", StringComparison.OrdinalIgnoreCase))
                return L("Backups.Progress.CopyingRobocopy", "Copying files (robocopy)...");

            return trimmed;
        }

        private void NotifyProgressPresentationChanged()
        {
            OnPropertiesChanged(
                nameof(DisplayProgress),
                nameof(HasProgress),
                nameof(IsIndeterminate),
                nameof(ProgressLabel),
                nameof(ShowPercent),
                nameof(ShowEta),
                nameof(StageLabel),
                nameof(StageDisplay),
                nameof(StageBrush),
                nameof(EtaDisplay),
                nameof(HasEtaDisplay));
        }

        private void UpdateDisplayProgress()
        {
            string stageKey = GetStageKey();
            if (!string.Equals(stageKey, _lastStageKey, StringComparison.OrdinalIgnoreCase))
            {
                _displayProgress = 0d;
                _lastStageKey = stageKey;
                _stageStartUtc = DateTime.UtcNow;
                OnPropertyChanged(nameof(StageDisplay));
            }

            if (!HasRawProgress || !IsProgressReliable)
            {
                _displayProgress = 0d;
                return;
            }

            double next = Math.Clamp(_progress, 0d, 100d);
            if (!HasCompletionSignal() && !IsHashingStage)
            {
                next = Math.Min(next, 99d);
            }

            if (IsCopyingStage && (DateTime.UtcNow - _stageStartUtc) < TimeSpan.FromSeconds(2) && next >= 99d)
            {
                next = Math.Min(next, 5d);
            }

            if (next < _displayProgress)
            {
                if (!HasCompletionSignal())
                {
                    _displayProgress = next;
                }
                return;
            }

            _displayProgress = next;
        }

        public void TickStageClock()
        {
            if (!IsCompleted)
            {
                OnPropertyChanged(nameof(StageDisplay));
            }
        }

        private static string FormatElapsed(DateTime startUtc)
        {
            TimeSpan elapsed = DateTime.UtcNow - startUtc;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;

            return elapsed.TotalHours >= 1
                ? elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        }

        private string GetStageKey()
        {
            if (ContainsToken(_currentFile, L("Backups.Status.Completed", StageCompleted)) ||
                ContainsToken(_currentFile, L("Backups.Status.NoChanges", "No changes detected")) ||
                ContainsToken(_currentFile, L("Backups.Status.Cancelled", "Cancelled")) ||
                ContainsToken(_currentFile, L("Backups.Status.Deleted", "Deleted")) ||
                (ContainsToken(_etaText, StageCompleted) && !ContainsToken(_etaText, StageHashing)))
            {
                return StageCompleted;
            }

            if (ContainsToken(_currentFile, L("Backups.Status.Cancelling", "Cancelling...")))
                return StageCancelling;

            if (ContainsToken(_etaText, StageCompressing))
                return StageCompressing;

            if (ContainsToken(_etaText, StageUploading))
                return StageUploading;

            if (ContainsToken(_etaText, StageHashing))
                return StageHashing;

            if (ContainsToken(_etaText, StageCopying))
                return StageCopying;

            if (ContainsToken(_etaText, StageRestoring) ||
                ContainsToken(_currentFile, StageRestoring) ||
                ContainsToken(_currentFile, "Decrypting") ||
                ContainsToken(_currentFile, L("Backups.Status.Restoring", "Restoring backup...")))
            {
                return StageRestoring;
            }

            if (ContainsToken(_currentFile, L("Backups.Status.Deleting", "Deleting backup files...")) ||
                ContainsToken(_currentFile, L("Backups.Stage.Deleting", StageDeleting)))
            {
                return StageDeleting;
            }

            if (ContainsToken(_currentFile, L("Backups.Status.Preparing", "Preparing backup...")))
                return StagePreparing;

            if (ContainsToken(_currentFile, "Reusing existing snapshot") ||
                ContainsToken(_currentFile, "Creating snapshot") ||
                ContainsToken(_currentFile, StageHashing))
            {
                return StageHashing;
            }

            if (ContainsToken(_currentFile, L("Backups.Status.Running", "Running backup...")) ||
                ContainsToken(_currentFile, L("Backups.Status.RunningMultiple", "Running backups...")))
            {
                return StageBackingUp;
            }

            if (HasCurrentFile)
                return StageBackingUp;

            return StageWorking;
        }

        private bool IsCopyingStage => string.Equals(GetStageKey(), StageCopying, StringComparison.OrdinalIgnoreCase);

        private static readonly IBrush StageCompletedBrush = new ImmutableSolidColorBrush(Color.Parse("#22CC88"));
        private static readonly IBrush StageCancelBrush = new ImmutableSolidColorBrush(Color.Parse("#FF6B6B"));
        private static readonly IBrush StageCompressBrush = new ImmutableSolidColorBrush(Color.Parse("#FFB84C"));
        private static readonly IBrush StageUploadBrush = new ImmutableSolidColorBrush(Color.Parse("#22CCFF"));
        private static readonly IBrush StageHashBrush = new ImmutableSolidColorBrush(Color.Parse("#9B6BFF"));
        private static readonly IBrush StageCopyBrush = new ImmutableSolidColorBrush(Color.Parse("#4C8DFF"));
        private static readonly IBrush StageBackupBrush = new ImmutableSolidColorBrush(Color.Parse("#3A7AFE"));
        private static readonly IBrush StagePrepareBrush = new ImmutableSolidColorBrush(Color.Parse("#8E9BAF"));

        private IBrush GetStageBrush()
        {
            string stageKey = GetStageKey();
            return stageKey switch
            {
                StageCompleted => StageCompletedBrush,
                StageCancelling => StageCancelBrush,
                StageDeleting => StageCancelBrush,
                StageCompressing => StageCompressBrush,
                StageUploading => StageUploadBrush,
                StageHashing => StageHashBrush,
                StageCopying => StageCopyBrush,
                StageRestoring => StageCopyBrush,
                StageBackingUp => StageBackupBrush,
                StagePreparing => StagePrepareBrush,
                _ => StagePrepareBrush
            };
        }

    }

    public class SnapshotActivityPoint
    {
        private static readonly IBrush SnapshotAutoBrush = new ImmutableSolidColorBrush(Color.Parse("#3A7AFE"));
        private static readonly IBrush SnapshotManualBrush = new ImmutableSolidColorBrush(Color.Parse("#22CC88"));
        private static readonly IBrush SnapshotImportedBrush = new ImmutableSolidColorBrush(Color.Parse("#FFB84C"));
        private static readonly IBrush SnapshotEmptyBrush = new ImmutableSolidColorBrush(Color.Parse("#22FFFFFF"));
        public string DayLabel { get; set; } = string.Empty;
        public bool ShowLabel { get; set; } = true;
        public int AutoCount
        {
            get; set;
        }
        public int ManualCount
        {
            get; set;
        }
        public int ImportedCount
        {
            get; set;
        }
        public long TotalBytes
        {
            get; set;
        }
        public double AutoHeight
        {
            get; set;
        }
        public double ManualHeight
        {
            get; set;
        }
        public double ImportedHeight
        {
            get; set;
        }
        public bool IsEmpty => AutoCount + ManualCount + ImportedCount == 0;
        public double EmptyHeight => IsEmpty ? 8 : 0;
        public bool HasAuto => AutoCount > 0;
        public bool HasManual => ManualCount > 0;
        public bool HasImported => ImportedCount > 0;
        public IBrush AutoBrush { get; set; } = SnapshotAutoBrush;
        public IBrush ManualBrush { get; set; } = SnapshotManualBrush;
        public IBrush ImportedBrush { get; set; } = SnapshotImportedBrush;
        public IBrush EmptyBrush { get; set; } = SnapshotEmptyBrush;
        public string TooltipText { get; set; } = string.Empty;
    }

    public sealed class RestoreReadinessIssueItem
    {
        public RestoreReadinessIssueItem(string projectName, string stateLabel, string reason, IBrush stateBrush)
        {
            ProjectName = projectName;
            StateLabel = stateLabel;
            Reason = reason;
            StateBrush = stateBrush;
        }

        public string ProjectName
        {
            get;
        }
        public string StateLabel
        {
            get;
        }
        public string Reason
        {
            get;
        }
        public IBrush StateBrush
        {
            get;
        }
    }

}
