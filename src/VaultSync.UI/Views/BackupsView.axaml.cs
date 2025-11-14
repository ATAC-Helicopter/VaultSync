using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views
{
    public partial class BackupsView : UserControl
    {
        public BackupsView()
        {
            InitializeComponent();

            // Make sure the compare area is sane when the view first appears.
            this.AttachedToVisualTree += (_, __) => UpdateCompareSummary();
        }

        private void HistoryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (DataContext is not BackupsViewModel vm || HistoryList is null)
                return;

            // Normalize selected items into snapshot list
            var items = HistoryList.SelectedItems
                                   .OfType<BackupSnapshotItem>()
                                   .Distinct()
                                   .ToList();

            if (items.Count == 0)
            {
                vm.SelectedSnapshotA = null;
                vm.SelectedSnapshotB = null;
            }
            else if (items.Count == 1)
            {
                // If only one is selected, treat it as both A and B
                vm.SelectedSnapshotA = items[0];
                vm.SelectedSnapshotB = items[0];
            }
            else
            {
                // Pick newest two as B (newer) and A (older) by timestamp
                var ordered = items
                    .OrderByDescending(x => x.Timestamp)
                    .ToList();

                if (ordered.Count >= 2)
                {
                    vm.SelectedSnapshotA = ordered[1];
                    vm.SelectedSnapshotB = ordered[0];
                }
                else
                {
                    vm.SelectedSnapshotA = ordered[0];
                    vm.SelectedSnapshotB = ordered[0];
                }
            }

            UpdateCompareSummary();
        }

        private void CompareCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            // SelectedSnapshotA / B are already bound TwoWay from XAML,
            // we just recompute the summary / bars.
            UpdateCompareSummary();
        }

        private void UpdateCompareSummary()
        {
            // Ensure we have the VM
            if (DataContext is not BackupsViewModel vm)
            {
                if (CompareSizeText != null)    CompareSizeText.Text    = string.Empty;
                if (CompareSummaryText != null) CompareSummaryText.Text = string.Empty;
                if (CompareBarA != null)        CompareBarA.Width       = 80;
                if (CompareBarB != null)        CompareBarB.Width       = 80;
                return;
            }

            var a = vm.SelectedSnapshotA;
            var b = vm.SelectedSnapshotB;

            // Need two different snapshots to compare
            if (a == null || b == null || ReferenceEquals(a, b) || a.Id == b.Id)
            {
                if (CompareSizeText != null)
                    CompareSizeText.Text = "Select two different snapshots to see a comparison.";

                if (CompareSummaryText != null)
                    CompareSummaryText.Text = string.Empty;

                if (CompareBarA != null) CompareBarA.Width = 80;
                if (CompareBarB != null) CompareBarB.Width = 80;

                return;
            }

            // Ensure A is always the OLDER snapshot and B the NEWER one
            if (b.Timestamp < a.Timestamp)
            {
                // Swap in the VM so the combo boxes also reflect this order
                vm.SelectedSnapshotA = b;
                vm.SelectedSnapshotB = a;

                a = vm.SelectedSnapshotA;
                b = vm.SelectedSnapshotB;

                if (a == null || b == null)
                    return;
            }

            long sizeA        = a.SizeBytes;
            long sizeB        = b.SizeBytes;
            long deltaSize    = sizeB - sizeA;
            long absDeltaSize = Math.Abs(deltaSize);

            string sizeAFormatted     = BackupSnapshotItem.FormatSize(sizeA);
            string sizeBFormatted     = BackupSnapshotItem.FormatSize(sizeB);
            string deltaSizeFormatted = BackupSnapshotItem.FormatSize(absDeltaSize);

            double percent = 0;
            if (sizeA > 0)
                percent = (double)deltaSize / sizeA * 100.0;

            string sign = deltaSize > 0 ? "+" :
                          deltaSize < 0 ? "−" : string.Empty;

            string percentPart = (sizeA > 0 && deltaSize != 0)
                ? $", {sign}{Math.Abs(percent):0.#}%"
                : string.Empty;

            // Time difference (B is newer than A by design here)
            TimeSpan span = b.Timestamp - a.Timestamp;
            string timeDiffText;
            if (span <= TimeSpan.Zero)
            {
                timeDiffText = "the same time";
            }
            else if (span.TotalMinutes < 1)
            {
                timeDiffText = "less than a minute";
            }
            else if (span.TotalHours < 1)
            {
                timeDiffText = $"{(int)span.TotalMinutes} min";
            }
            else if (span.TotalDays < 1)
            {
                timeDiffText = $"{(int)span.TotalHours} h {(int)(span.TotalMinutes % 60)} min";
            }
            else
            {
                timeDiffText = $"{(int)span.TotalDays} days";
            }

            // --- Update the size line ---

            if (CompareSizeText != null)
            {
                if (deltaSize == 0)
                {
                    CompareSizeText.Text =
                        $"Size: {sizeAFormatted} → {sizeBFormatted} (no change)";
                }
                else
                {
                    CompareSizeText.Text =
                        $"Size: {sizeAFormatted} → {sizeBFormatted} ({sign}{deltaSizeFormatted}{percentPart})";
                }
            }

            // --- Update the bars (relative widths) ---

            if (CompareBarA != null && CompareBarB != null)
            {
                const double minWidth   = 40;
                const double extraWidth = 140; // max extra width for the larger snapshot

                double total = sizeA + sizeB;
                if (total <= 0)
                {
                    CompareBarA.Width = 80;
                    CompareBarB.Width = 80;
                }
                else
                {
                    double ratioA = sizeA / total;
                    double ratioB = sizeB / total;

                    CompareBarA.Width = minWidth + extraWidth * ratioA;
                    CompareBarB.Width = minWidth + extraWidth * ratioB;
                }
            }

            // --- Summary sentence: which one is bigger + how much newer B is ---

            if (CompareSummaryText != null)
            {
                string sizeTrend;
                if (deltaSize > 0)
                {
                    sizeTrend = $"Snapshot B is larger ({sign}{deltaSizeFormatted}{percentPart})";
                }
                else if (deltaSize < 0)
                {
                    sizeTrend = $"Snapshot B is smaller ({sign}{deltaSizeFormatted}{percentPart})";
                }
                else
                {
                    sizeTrend = "Snapshots are the same size";
                }

                string timeTrend;
                if (span <= TimeSpan.Zero)
                {
                    timeTrend = " at the same time as A.";
                }
                else
                {
                    timeTrend = $" and newer than A by {timeDiffText}.";
                }

                CompareSummaryText.Text = sizeTrend + timeTrend;
            }
        }
    }
}