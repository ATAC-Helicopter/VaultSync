using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;
using VaultSync.Core.Config;
using VaultSync.UI.Services;

namespace VaultSync.UI
{
    public sealed partial class SettingsViewModel
    {
        public sealed class ThemeColorSlotViewModel : INotifyPropertyChanged
        {
            private string _hex;
            private bool _isSelected;

            public ThemeColorSlotViewModel(string id, string label, string hex)
            {
                Id = id;
                Label = label;
                _hex = hex;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public string Id { get; }
            public string Label { get; }

            public string Hex
            {
                get => _hex;
                set
                {
                    var normalized = NormalizeHex(value, _hex);
                    if (_hex == normalized)
                        return;

                    _hex = normalized;
                    RaiseProperty(nameof(Hex));
                    RaiseProperty(nameof(SwatchColor));
                }
            }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value)
                        return;

                    _isSelected = value;
                    RaiseProperty(nameof(IsSelected));
                }
            }

            public Color SwatchColor => Color.Parse(_hex);

            private static string NormalizeHex(string? value, string fallback)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return fallback;

                var candidate = value.Trim();
                if (!candidate.StartsWith("#", StringComparison.Ordinal))
                    candidate = "#" + candidate;

                return Color.TryParse(candidate, out var color)
                    ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                    : fallback;
            }

            private void RaiseProperty(string propertyName)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        public sealed class ThemePresetOptionViewModel
        {
            public required string Id { get; init; }
            public required string Name { get; init; }
            public required string Description { get; init; }
            public required ThemePaletteConfig Palette { get; init; }
        }

        public sealed class ThemePaletteSwatchViewModel : INotifyPropertyChanged
        {
            private bool _isSelected;

            public ThemePaletteSwatchViewModel(string hex)
            {
                Hex = hex;
                SwatchColor = Color.Parse(hex);
                SwatchBrush = new SolidColorBrush(SwatchColor);
                OutlineBrush = CreateOutlineBrush(SwatchColor);
            }

            public event PropertyChangedEventHandler? PropertyChanged;
            public string Hex { get; }
            public Color SwatchColor { get; }
            public IBrush SwatchBrush { get; }
            public IBrush OutlineBrush { get; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value)
                        return;

                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            private static IBrush CreateOutlineBrush(Color color)
            {
                var luminance = ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255d;
                return new SolidColorBrush(luminance > 0.62 ? Color.Parse("#24344A") : Color.Parse("#E2E8F0"));
            }
        }

        public ObservableCollection<ThemeColorSlotViewModel> ThemeColorSlots { get; } = new();
        public ObservableCollection<ThemePresetOptionViewModel> ThemePresets { get; } = new();
        public ObservableCollection<ThemePaletteSwatchViewModel> ThemePaletteSwatches { get; } = new();
        public ObservableCollection<ThemePaletteSwatchViewModel> SelectedThemePaletteSwatches { get; } = new();
        public ObservableCollection<string> CustomThemeBaseOptions { get; } = new() { "Dark", "Light" };

        private void OnThemeColorSlotsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
            {
                foreach (ThemeColorSlotViewModel slot in e.NewItems)
                    slot.PropertyChanged += OnThemeColorSlotPropertyChanged;
            }

            if (e.OldItems is not null)
            {
                foreach (ThemeColorSlotViewModel slot in e.OldItems)
                    slot.PropertyChanged -= OnThemeColorSlotPropertyChanged;
            }
        }

        private void OnThemeColorSlotPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not ThemeColorSlotViewModel slot)
                return;

            if (string.Equals(e.PropertyName, nameof(ThemeColorSlotViewModel.IsSelected), StringComparison.Ordinal))
            {
                if (slot.IsSelected)
                    SelectedThemeColorSlot = slot;
                return;
            }

            if (!string.Equals(e.PropertyName, nameof(ThemeColorSlotViewModel.Hex), StringComparison.Ordinal))
                return;

            RefreshThemeEditorPreview();
            if (_isInitialized && IsCustomThemeSelected)
                ApplyThemePreview();

            TriggerAutoSave();
        }

        private void InitializeThemeEditor()
        {
            ThemeColorSlots.Clear();
            ThemeColorSlots.Add(new ThemeColorSlotViewModel("Background", L("Settings.Appearance.ThemeSlots.Background", "Background"), "#101218"));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel("Surface", L("Settings.Appearance.ThemeSlots.Surface", "Cards"), "#181B24"));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel("SurfaceAlt", L("Settings.Appearance.ThemeSlots.SurfaceAlt", "Raised surfaces"), "#222635"));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel("Accent", L("Settings.Appearance.ThemeSlots.Accent", "Accent"), "#4F8DFF"));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel("TextPrimary", L("Settings.Appearance.ThemeSlots.TextPrimary", "Primary text"), "#FFFFFF"));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel("TextSecondary", L("Settings.Appearance.ThemeSlots.TextSecondary", "Secondary text"), "#B3B8C7"));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel("Success", L("Settings.Appearance.ThemeSlots.Success", "Success"), "#4FF2B6"));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel("Warning", L("Settings.Appearance.ThemeSlots.Warning", "Warning"), "#FFC766"));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel("Danger", L("Settings.Appearance.ThemeSlots.Danger", "Danger"), "#FF7676"));

            ThemePresets.Clear();
            foreach (var preset in ThemeManager.GetThemePresets())
            {
                ThemePresets.Add(new ThemePresetOptionViewModel
                {
                    Id = preset.Id,
                    Name = L($"Settings.Appearance.ThemePresets.{preset.Id}.Name", preset.Palette.Name),
                    Description = L($"Settings.Appearance.ThemePresets.{preset.Id}.Description", preset.Description),
                    Palette = preset.Palette.Clone()
                });
            }

            ThemePaletteSwatches.Clear();
            foreach (var hex in GetAllThemePaletteHexes())
                ThemePaletteSwatches.Add(new ThemePaletteSwatchViewModel(hex));

            SelectedThemeColorSlot = ThemeColorSlots.FirstOrDefault();
            RefreshSelectedThemePaletteSwatches();
        }

        private static IReadOnlyList<string> GetAllThemePaletteHexes()
        {
            return new[]
            {
                "#111827", "#334155", "#64748B", "#E2E8F0",
                "#DC2626", "#F97316", "#F59E0B", "#EAB308",
                "#84CC16", "#22C55E", "#14B8A6", "#06B6D4",
                "#0EA5E9", "#2563EB", "#4F8DFF", "#6366F1",
                "#7C3AED", "#A855F7", "#EC4899", "#F43F5E"
            };
        }

        private static IReadOnlyList<string> GetThemePaletteForSlot(string? slotId)
        {
            return slotId switch
            {
                "Background" or "Surface" or "SurfaceAlt" => new[]
                {
                    "#111827", "#334155", "#64748B", "#E2E8F0",
                    "#0F172A", "#1E293B", "#475569", "#94A3B8",
                    "#CBD5E1", "#F8FAFC"
                },
                "TextPrimary" => new[]
                {
                    "#111827", "#334155", "#64748B", "#E2E8F0", "#F8FAFC"
                },
                "TextSecondary" => new[]
                {
                    "#334155", "#475569", "#64748B", "#94A3B8", "#CBD5E1", "#E2E8F0"
                },
                "Success" => new[]
                {
                    "#166534", "#15803D", "#16A34A", "#22C55E", "#4ADE80", "#86EFAC"
                },
                "Warning" => new[]
                {
                    "#92400E", "#B45309", "#D97706", "#F59E0B", "#FBBF24", "#FCD34D"
                },
                "Danger" => new[]
                {
                    "#991B1B", "#B91C1C", "#DC2626", "#EF4444", "#F87171", "#FCA5A5"
                },
                _ => GetAllThemePaletteHexes()
            };
        }

        private void RefreshSelectedThemePaletteSwatches()
        {
            SelectedThemePaletteSwatches.Clear();
            foreach (var hex in GetThemePaletteForSlot(SelectedThemeColorSlot?.Id))
                SelectedThemePaletteSwatches.Add(new ThemePaletteSwatchViewModel(hex));

            UpdateSelectedThemePaletteSwatchState();
        }

        private void ApplyThemePreset(ThemePresetOptionViewModel? preset)
        {
            if (preset is null)
                return;

            SelectedTheme = "Custom";
            LoadCustomTheme(preset.Palette.Clone());
            SaveStatus = string.Format(L("Settings.Appearance.ThemePresetApplied", "Theme preset applied: {0}."), preset.Name);
        }

        private void ApplyThemePaletteSwatch(ThemePaletteSwatchViewModel? swatch)
        {
            if (swatch is null || SelectedThemeColorSlot is null)
                return;

            SelectedThemeColorSlot.Hex = swatch.Hex;
            UpdateSelectedThemePaletteSwatchState();
        }

        private void ResetCustomTheme()
        {
            LoadCustomTheme(ThemeManager.GetDefaultCustomTheme());
            SaveStatus = L("Settings.Appearance.ThemeReset", "Custom theme reset.");
        }

        private void LoadCustomTheme(ThemePaletteConfig? palette)
        {
            var theme = palette?.Clone() ?? ThemeManager.GetDefaultCustomTheme();
            _customThemeName = string.IsNullOrWhiteSpace(theme.Name)
                ? L("Settings.Appearance.ThemePresets.vaultsync-midnight.Name", "VaultSync Midnight")
                : theme.Name.Trim();
            _customThemeBase = string.Equals(theme.BaseTheme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";

            SetThemeSlotHex("Background", theme.Background);
            SetThemeSlotHex("Surface", theme.Surface);
            SetThemeSlotHex("SurfaceAlt", theme.SurfaceAlt);
            SetThemeSlotHex("Accent", theme.Accent);
            SetThemeSlotHex("TextPrimary", theme.TextPrimary);
            SetThemeSlotHex("TextSecondary", theme.TextSecondary);
            SetThemeSlotHex("Success", theme.Success);
            SetThemeSlotHex("Warning", theme.Warning);
            SetThemeSlotHex("Danger", theme.Danger);

            OnPropertyChanged(nameof(CustomThemeName));
            OnPropertyChanged(nameof(CustomThemeBase));
            RefreshThemeEditorPreview();

            if (_isInitialized && IsCustomThemeSelected)
                ApplyThemePreview();
        }

        private void SetThemeSlotHex(string slotId, string hex)
        {
            var slot = ThemeColorSlots.FirstOrDefault(x => string.Equals(x.Id, slotId, StringComparison.Ordinal));
            if (slot is not null)
                slot.Hex = hex;
        }

        private ThemePaletteConfig BuildCustomThemeConfig()
        {
            string Get(string slotId, string fallback)
            {
                return ThemeColorSlots.FirstOrDefault(x => string.Equals(x.Id, slotId, StringComparison.Ordinal))?.Hex ?? fallback;
            }

            return new ThemePaletteConfig
            {
                Name = string.IsNullOrWhiteSpace(CustomThemeName) ? L("Settings.Appearance.ThemeNameDefault", "Custom theme") : CustomThemeName.Trim(),
                BaseTheme = string.Equals(CustomThemeBase, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark",
                Background = Get("Background", "#101218"),
                Surface = Get("Surface", "#181B24"),
                SurfaceAlt = Get("SurfaceAlt", "#222635"),
                Accent = Get("Accent", "#4F8DFF"),
                TextPrimary = Get("TextPrimary", "#FFFFFF"),
                TextSecondary = Get("TextSecondary", "#B3B8C7"),
                Success = Get("Success", "#4FF2B6"),
                Warning = Get("Warning", "#FFC766"),
                Danger = Get("Danger", "#FF7676")
            };
        }

        private void ApplyThemePreview()
        {
            var appearance = new AppearanceConfig
            {
                Theme = NormalizeThemeOption(SelectedTheme),
                CustomTheme = BuildCustomThemeConfig()
            };

            ThemeManager.ApplyAppearance(appearance);
        }

        private void RefreshThemeEditorPreview()
        {
            OnPropertyChanged(nameof(SelectedThemeColor));
            OnPropertyChanged(nameof(SelectedThemeColorHex));
            OnPropertyChanged(nameof(ThemePreviewBackground));
            OnPropertyChanged(nameof(ThemePreviewSurface));
            OnPropertyChanged(nameof(ThemePreviewSurfaceAlt));
            OnPropertyChanged(nameof(ThemePreviewAccent));
            OnPropertyChanged(nameof(ThemePreviewTextPrimary));
            OnPropertyChanged(nameof(ThemePreviewTextSecondary));
            UpdateSelectedThemePaletteSwatchState();
        }

        private void UpdateSelectedThemePaletteSwatchState()
        {
            var selectedHex = SelectedThemeColorHex;
            foreach (var swatch in SelectedThemePaletteSwatches)
                swatch.IsSelected = string.Equals(swatch.Hex, selectedHex, StringComparison.OrdinalIgnoreCase);
        }

        public ThemeColorSlotViewModel? SelectedThemeColorSlot
        {
            get => _selectedThemeColorSlot;
            set
            {
                if (ReferenceEquals(_selectedThemeColorSlot, value) || value is null)
                    return;

                if (_selectedThemeColorSlot is not null)
                    _selectedThemeColorSlot.IsSelected = false;

                _selectedThemeColorSlot = value;
                if (!_selectedThemeColorSlot.IsSelected)
                    _selectedThemeColorSlot.IsSelected = true;

                RefreshSelectedThemePaletteSwatches();
                RefreshThemeEditorPreview();
                _applyThemePaletteSwatchCommand?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(SelectedThemeColorSlot));
            }
        }

        public Color SelectedThemeColor
        {
            get => SelectedThemeColorSlot?.SwatchColor ?? Color.Parse("#4F8DFF");
            set
            {
                if (SelectedThemeColorSlot is null)
                    return;

                SelectedThemeColorSlot.Hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            }
        }

        public string SelectedThemeColorHex => SelectedThemeColorSlot?.Hex ?? "#4F8DFF";
        public string ThemePreviewBackground => ThemeColorSlots.FirstOrDefault(x => x.Id == "Background")?.Hex ?? "#101218";
        public string ThemePreviewSurface => ThemeColorSlots.FirstOrDefault(x => x.Id == "Surface")?.Hex ?? "#181B24";
        public string ThemePreviewSurfaceAlt => ThemeColorSlots.FirstOrDefault(x => x.Id == "SurfaceAlt")?.Hex ?? "#222635";
        public string ThemePreviewAccent => ThemeColorSlots.FirstOrDefault(x => x.Id == "Accent")?.Hex ?? "#4F8DFF";
        public string ThemePreviewTextPrimary => ThemeColorSlots.FirstOrDefault(x => x.Id == "TextPrimary")?.Hex ?? "#FFFFFF";
        public string ThemePreviewTextSecondary => ThemeColorSlots.FirstOrDefault(x => x.Id == "TextSecondary")?.Hex ?? "#B3B8C7";

        public ICommand ApplyThemePresetCommand => _applyThemePresetCommand!;
        public ICommand ApplyThemePaletteSwatchCommand => _applyThemePaletteSwatchCommand!;
        public ICommand ResetCustomThemeCommand => _resetCustomThemeCommand!;

        public string ThemeEditorLabel => L("Settings.Appearance.ThemeEditor.Label", "Custom theme");
        public string ThemeEditorDescription => L("Settings.Appearance.ThemeEditor.Description", "Build a theme from stable app colors, apply a preset, then fine-tune it with the visual picker.");
        public string ThemePresetsLabel => L("Settings.Appearance.ThemeEditor.Presets", "Starter themes");
        public string ThemePaletteLabel => L("Settings.Appearance.ThemeEditor.Palette", "Quick palette");
        public string ThemePaletteHint => L("Settings.Appearance.ThemeEditor.PaletteHint", "Saved colors apply to the selected custom-theme section.");
        public string ThemeBaseLabel => L("Settings.Appearance.ThemeEditor.BaseTheme", "Base");
        public string ThemeNameLabel => L("Settings.Appearance.ThemeEditor.Name", "Theme name");
        public string ThemePickerLabel => L("Settings.Appearance.ThemeEditor.Picker", "Edit selected color");
        public string ThemePreviewLabel => L("Settings.Appearance.ThemeEditor.Preview", "Preview");
        public string ThemeAdvancedLabel => L("Settings.Appearance.ThemeEditor.AdvancedPanel", "Advanced tuning");
        public string ThemeAdvancedDescription => L("Settings.Appearance.ThemeEditor.AdvancedDescription", "Fine-tune the selected color with direct component sliders.");
        public string ThemePreviewAccentLabel => L("Settings.Appearance.ThemeEditor.PreviewAccent", "Accent");
        public string ThemePreviewSurfaceLabel => L("Settings.Appearance.ThemeEditor.PreviewSurface", "Surface");
        public string ThemePreviewPrimaryLabel => L("Settings.Appearance.ThemeEditor.PreviewPrimary", "Primary text");
        public string ThemePreviewSecondaryLabel => L("Settings.Appearance.ThemeEditor.PreviewSecondary", "Secondary text stays readable while you tune the palette.");
    }
}
