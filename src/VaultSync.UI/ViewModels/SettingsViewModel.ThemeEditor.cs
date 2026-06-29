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
using VaultSync.UI.ViewModels;

namespace VaultSync.UI
{
    public sealed partial class SettingsViewModel
    {
        private const string ThemeSlotAccent = "Accent";
        private const string ThemeSlotBackground = "Background";
        private const string ThemeSlotDanger = "Danger";
        private const string ThemeSlotSuccess = "Success";
        private const string ThemeSlotSurface = "Surface";
        private const string ThemeSlotSurfaceAlt = "SurfaceAlt";
        private const string ThemeSlotTextPrimary = "TextPrimary";
        private const string ThemeSlotTextSecondary = "TextSecondary";
        private const string ThemeSlotWarning = "Warning";

        private const string ThemeDefaultAccent = "#4F8DFF";
        private const string ThemeDefaultBackground = "#101218";
        private const string ThemeDefaultDanger = "#FF7676";
        private const string ThemeDefaultSuccess = "#4FF2B6";
        private const string ThemeDefaultSurface = "#181B24";
        private const string ThemeDefaultSurfaceAlt = "#222635";
        private const string ThemeDefaultTextPrimary = "#FFFFFF";
        private const string ThemeDefaultTextSecondary = "#B3B8C7";
        private const string ThemeDefaultWarning = "#FFC766";

        public sealed class ThemeColorSlotViewModel : ViewModelBase
        {
            private string _hex;
            private bool _isSelected;
            private IBrush _swatchBrush;

            public ThemeColorSlotViewModel(string id, string label, string hex)
            {
                Id = id;
                Label = label;
                _hex = hex;
                _swatchBrush = new SolidColorBrush(Color.Parse(_hex));
            }

            public string Id { get; }
            public string Label { get; }

            public string Hex
            {
                get => _hex;
                set
                {
                    string normalized = NormalizeHex(value, _hex);
                    if (_hex == normalized)
                        return;

                    _hex = normalized;
                    _swatchBrush = new SolidColorBrush(Color.Parse(_hex));
                    OnPropertyChanged(nameof(Hex));
                    OnPropertyChanged(nameof(SwatchColor));
                    OnPropertyChanged(nameof(SwatchBrush));
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
                    OnPropertyChanged(nameof(IsSelected));
                }
            }

            public Color SwatchColor => Color.Parse(_hex);
            public IBrush SwatchBrush => _swatchBrush;

            private static string NormalizeHex(string? value, string fallback)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return fallback;

                string candidate = value.Trim();
                if (!candidate.StartsWith("#", StringComparison.Ordinal))
                    candidate = "#" + candidate;

                return Color.TryParse(candidate, out Color color)
                    ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                    : fallback;
            }

        }

        public sealed class ThemePresetOptionViewModel
        {
            public required string Id { get; init; }
            public required string Name { get; init; }
            public required string Description { get; init; }
            public required ThemePaletteConfig Palette { get; init; }
        }

        public sealed class ThemePaletteSwatchViewModel : ViewModelBase
        {
            private bool _isSelected;

            public ThemePaletteSwatchViewModel(string hex)
            {
                Hex = hex;
                SwatchColor = Color.Parse(hex);
                SwatchBrush = new SolidColorBrush(SwatchColor);
                OutlineBrush = CreateOutlineBrush(SwatchColor);
            }

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
                    OnPropertyChanged(nameof(IsSelected));
                }
            }

            private static IBrush CreateOutlineBrush(Color color)
            {
                double luminance = ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255d;
                return new SolidColorBrush(luminance > 0.62 ? Color.Parse("#24344A") : Color.Parse("#E2E8F0"));
            }
        }

        public ObservableCollection<ThemeColorSlotViewModel> ThemeColorSlots { get; } = [];
        public ObservableCollection<ThemePresetOptionViewModel> ThemePresets { get; } = [];
        public ObservableCollection<ThemePaletteSwatchViewModel> ThemePaletteSwatches { get; } = [];
        public ObservableCollection<string> CustomThemeBaseOptions { get; } = [];

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
                {
                    SelectedThemeColorSlot = slot;
                }
                else if (ReferenceEquals(slot, _selectedThemeColorSlot))
                {
                    // Keep one active target so palette clicks always apply to the visibly selected section.
                    slot.IsSelected = true;
                }
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
            ThemeColorSlots.Add(new ThemeColorSlotViewModel(ThemeSlotBackground, L("Settings.Appearance.ThemeSlots.Background", ThemeSlotBackground), ThemeDefaultBackground));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel(ThemeSlotSurface, L("Settings.Appearance.ThemeSlots.Surface", "Cards"), ThemeDefaultSurface));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel(ThemeSlotSurfaceAlt, L("Settings.Appearance.ThemeSlots.SurfaceAlt", "Raised surfaces"), ThemeDefaultSurfaceAlt));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel(ThemeSlotAccent, L("Settings.Appearance.ThemeSlots.Accent", ThemeSlotAccent), ThemeDefaultAccent));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel(ThemeSlotTextPrimary, L("Settings.Appearance.ThemeSlots.TextPrimary", "Primary text"), ThemeDefaultTextPrimary));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel(ThemeSlotTextSecondary, L("Settings.Appearance.ThemeSlots.TextSecondary", "Secondary text"), ThemeDefaultTextSecondary));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel(ThemeSlotSuccess, L("Settings.Appearance.ThemeSlots.Success", ThemeSlotSuccess), ThemeDefaultSuccess));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel(ThemeSlotWarning, L("Settings.Appearance.ThemeSlots.Warning", ThemeSlotWarning), ThemeDefaultWarning));
            ThemeColorSlots.Add(new ThemeColorSlotViewModel(ThemeSlotDanger, L("Settings.Appearance.ThemeSlots.Danger", ThemeSlotDanger), ThemeDefaultDanger));

            ThemePresets.Clear();
            foreach ((string Id, string Description, ThemePaletteConfig Palette) preset in ThemeManager.GetThemePresets())
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
            foreach (string hex in GetAllThemePaletteHexes())
                ThemePaletteSwatches.Add(new ThemePaletteSwatchViewModel(hex));

            SelectedThemeColorSlot = ThemeColorSlots.FirstOrDefault();
            UpdateSelectedThemePaletteSwatchState();
        }

        private static IReadOnlyList<string> GetAllThemePaletteHexes()
        {
            return new[]
            {
                "#111827", "#334155", "#64748B", "#E2E8F0",
                "#DC2626", "#F97316", "#F59E0B", "#EAB308",
                "#84CC16", "#22C55E", "#14B8A6", "#06B6D4",
                "#0EA5E9", "#2563EB", ThemeDefaultAccent, "#6366F1",
                "#7C3AED", "#A855F7", "#EC4899", "#F43F5E"
            };
        }

        private void ApplyThemePreset(ThemePresetOptionViewModel? preset)
        {
            if (preset is null)
                return;

            SelectedTheme = ThemeOptionCustomLabel;
            LoadCustomTheme(preset.Palette.Clone());
            SaveStatus = string.Format(L("Settings.Appearance.ThemePresetApplied", "Theme preset applied: {0}."), preset.Name);
        }

        private void ApplyThemePaletteSwatch(ThemePaletteSwatchViewModel? swatch)
        {
            if (swatch is null || SelectedThemeColorSlot is null)
                return;

            SelectedThemeColorSlot.Hex = swatch.Hex;
            RefreshThemeEditorPreview();

            if (_isInitialized && IsCustomThemeSelected)
                ApplyThemePreview();

            SaveStatus = string.Format(
                L("Settings.Appearance.ThemePaletteApplied", "Applied {0} to {1}."),
                swatch.Hex,
                SelectedThemeColorSlot.Label);

            TriggerAutoSave();
            UpdateSelectedThemePaletteSwatchState();
        }

        private void SelectThemeColorSlot(ThemeColorSlotViewModel? slot)
        {
            if (slot is null)
                return;

            SelectedThemeColorSlot = slot;
        }

        private void ResetCustomTheme()
        {
            LoadCustomTheme(ThemeManager.GetDefaultCustomTheme());
            SaveStatus = L("Settings.Appearance.ThemeReset", "Custom theme reset.");
        }

        private void LoadCustomTheme(ThemePaletteConfig? palette)
        {
            ThemePaletteConfig theme = palette?.Clone() ?? ThemeManager.GetDefaultCustomTheme();
            _customThemeName = string.IsNullOrWhiteSpace(theme.Name)
                ? L("Settings.Appearance.ThemePresets.vaultsync-midnight.Name", "VaultSync Midnight")
                : theme.Name.Trim();
            _customThemeBase = string.Equals(theme.BaseTheme, "Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeBaseLightLabel
                : ThemeBaseDarkLabel;

            SetThemeSlotHex(ThemeSlotBackground, theme.Background);
            SetThemeSlotHex(ThemeSlotSurface, theme.Surface);
            SetThemeSlotHex(ThemeSlotSurfaceAlt, theme.SurfaceAlt);
            SetThemeSlotHex(ThemeSlotAccent, theme.Accent);
            SetThemeSlotHex(ThemeSlotTextPrimary, theme.TextPrimary);
            SetThemeSlotHex(ThemeSlotTextSecondary, theme.TextSecondary);
            SetThemeSlotHex(ThemeSlotSuccess, theme.Success);
            SetThemeSlotHex(ThemeSlotWarning, theme.Warning);
            SetThemeSlotHex(ThemeSlotDanger, theme.Danger);

            OnPropertyChanged(nameof(CustomThemeName));
            OnPropertyChanged(nameof(CustomThemeBase));
            RefreshThemeEditorPreview();

            if (_isInitialized && IsCustomThemeSelected)
                ApplyThemePreview();
        }

        private void SetThemeSlotHex(string slotId, string hex)
        {
            ThemeColorSlotViewModel? slot = ThemeColorSlots.FirstOrDefault(x => string.Equals(x.Id, slotId, StringComparison.Ordinal));
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
                BaseTheme = IsLightThemeBaseOption(CustomThemeBase) ? "Light" : "Dark",
                Background = Get(ThemeSlotBackground, ThemeDefaultBackground),
                Surface = Get(ThemeSlotSurface, ThemeDefaultSurface),
                SurfaceAlt = Get(ThemeSlotSurfaceAlt, ThemeDefaultSurfaceAlt),
                Accent = Get(ThemeSlotAccent, ThemeDefaultAccent),
                TextPrimary = Get(ThemeSlotTextPrimary, ThemeDefaultTextPrimary),
                TextSecondary = Get(ThemeSlotTextSecondary, ThemeDefaultTextSecondary),
                Success = Get(ThemeSlotSuccess, ThemeDefaultSuccess),
                Warning = Get(ThemeSlotWarning, ThemeDefaultWarning),
                Danger = Get(ThemeSlotDanger, ThemeDefaultDanger)
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
            string selectedHex = SelectedThemeColorHex;
            foreach (ThemePaletteSwatchViewModel swatch in ThemePaletteSwatches)
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

                RefreshThemeEditorPreview();
                _applyThemePaletteSwatchCommand?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(SelectedThemeColorSlot));
            }
        }

        public Color SelectedThemeColor
        {
            get => SelectedThemeColorSlot?.SwatchColor ?? Color.Parse(ThemeDefaultAccent);
            set
            {
                if (SelectedThemeColorSlot is null)
                    return;

                SelectedThemeColorSlot.Hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            }
        }

        public string SelectedThemeColorHex => SelectedThemeColorSlot?.Hex ?? ThemeDefaultAccent;
        public string ThemePreviewBackground => ThemeColorSlots.FirstOrDefault(x => x.Id == ThemeSlotBackground)?.Hex ?? ThemeDefaultBackground;
        public string ThemePreviewSurface => ThemeColorSlots.FirstOrDefault(x => x.Id == ThemeSlotSurface)?.Hex ?? ThemeDefaultSurface;
        public string ThemePreviewSurfaceAlt => ThemeColorSlots.FirstOrDefault(x => x.Id == ThemeSlotSurfaceAlt)?.Hex ?? ThemeDefaultSurfaceAlt;
        public string ThemePreviewAccent => ThemeColorSlots.FirstOrDefault(x => x.Id == ThemeSlotAccent)?.Hex ?? ThemeDefaultAccent;
        public string ThemePreviewTextPrimary => ThemeColorSlots.FirstOrDefault(x => x.Id == ThemeSlotTextPrimary)?.Hex ?? ThemeDefaultTextPrimary;
        public string ThemePreviewTextSecondary => ThemeColorSlots.FirstOrDefault(x => x.Id == ThemeSlotTextSecondary)?.Hex ?? ThemeDefaultTextSecondary;

        public ICommand ApplyThemePresetCommand => _applyThemePresetCommand!;
        public ICommand ApplyThemePaletteSwatchCommand => _applyThemePaletteSwatchCommand!;
        public ICommand SelectThemeColorSlotCommand => _selectThemeColorSlotCommand!;
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
        public string ThemePreviewAccentLabel => L("Settings.Appearance.ThemeEditor.PreviewAccent", ThemeSlotAccent);
        public string ThemePreviewSurfaceLabel => L("Settings.Appearance.ThemeEditor.PreviewSurface", ThemeSlotSurface);
        public string ThemePreviewPrimaryLabel => L("Settings.Appearance.ThemeEditor.PreviewPrimary", "Primary text");
        public string ThemePreviewSecondaryLabel => L("Settings.Appearance.ThemeEditor.PreviewSecondary", "Secondary text stays readable while you tune the palette.");
    }
}
