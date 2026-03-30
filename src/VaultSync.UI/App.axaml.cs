using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using VaultSync.Core.Config;
using VaultSync.UI.Notifications;
using VaultSync.UI.ViewModels;
using VaultSync.UI.ViewModels.Notifications;
using VaultSync.UI.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Layout;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;
using System.Globalization;
using Avalonia.VisualTree;
using System.Collections.Specialized;

namespace VaultSync.UI;

public partial class App : Application
{
    // Test hook: enabled while onboarding UX is being validated every startup.
    private static bool ForceOnboardingAtStartupForTesting = false;
    private static readonly string OnboardingSentinelPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vaultsync", "onboarding.seen");

    public static bool IsShuttingDown { get; private set; }
    public static bool IsCrashing { get; private set; }

    public static AppViewModel? AppViewModelInstance { get; private set; }

    // Keep a reference to the tray/menu-bar icon so it stays alive.
    private TrayIcon? _trayIcon;
    private NativeMenu? _trayMenu;
    private TrayPanelService? _trayPanelService;
    private static App? _instance;
    private static bool _trayRecentLatestOnly = true;
    private const string DefaultDriveHealthLabel = "Storage health: tap Recheck";
    private static string _cachedDriveHealthLabel = DefaultDriveHealthLabel;
    private static DriveHealthStatus _cachedDriveHealthStatus = DriveHealthStatus.Unknown;
    private static bool _cachedDriveHealthIsNetwork;
    private const int MaxRecentBackupsPerProject = 3;
    private int _trayMenuRefreshInFlight;
    private int _trayMenuRefreshQueued;
    private DateTime _lastTrayMenuRefreshUtc = DateTime.MinValue;
    private DateTime _lastTrayMenuRefreshFailureUtc = DateTime.MinValue;
    private int _trayMenuRefreshFailureCount;
    private string? _trayMenuSignature;
    private DateTime _lastTrayMenuOpenUtc = DateTime.MinValue;
    private DateTime _trayMenuSuppressUntilUtc = DateTime.MinValue;
    private const string DefaultFontFallback =
        "Inter, Segoe UI, SF Pro Text, Helvetica Neue, Nirmala UI, Microsoft YaHei UI, Microsoft JhengHei UI, " +
        "Meiryo, Malgun Gothic, Geeza Pro, Al Nile, Al Bayan, Kohinoor Arabic, Noto Sans Arabic, " +
        "Noto Naskh Arabic, Arial Unicode MS, Arial, Tahoma";
    private FontFamily? _defaultFontFamily;
    private readonly HashSet<Window> _arabicFontHooked = new();
    private static readonly FontFamily ArabicFontFamily = new(
        $"avares://VaultSync.UI/Assets/Fonts/#Noto Sans Arabic, avares://VaultSync.UI/Assets/Fonts/#Noto Sans, {DefaultFontFallback}");
    private static readonly FontFamily ArabicMacFontFamily = new(
        $"avares://VaultSync.UI/Assets/Fonts/#Noto Sans Arabic, avares://VaultSync.UI/Assets/Fonts/#Noto Sans, " +
        "Geeza Pro, Al Nile, Al Bayan, Kohinoor Arabic, Noto Naskh Arabic, Arial Unicode MS, Arial");
    private static readonly TimeSpan EncryptedOpenTempRetention = TimeSpan.FromMinutes(30);
    private const int DefaultEncryptedOpenTimeoutMinutes = 10;
    private static int _encryptedOpenInFlight;
    private static long _uiHeartbeatTicks;
    private static int _uiHangReported;
    private static Timer? _uiWatchdogTimer;
    private static DispatcherTimer? _uiHeartbeatTimer;

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    internal static void MarkCrashing()
    {
        if (IsCrashing)
            return;

        IsCrashing = true;
        IsShuttingDown = true;
        DiagnosticsLogger.RecordWithStack("MarkCrashing called.");
        GlobalNotificationCenter.Instance.SuppressNotifications = true;
    }

    internal static void MarkShuttingDown()
    {
        IsShuttingDown = true;
        DiagnosticsLogger.RecordWithStack("MarkShuttingDown called.");
    }

    private static string Lf(string key, string fallback, params object[] args)
    {
        var fmt = L(key, fallback);
        return string.Format(CultureInfo.CurrentCulture, fmt, args);
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _instance = this;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            CrashHandler.RegisterAvalonia();
            WireGlobalExceptionHandlers();
            WireLifecycleBreadcrumbs(desktop);
            InitializeLocalizationProviderEarly();
            AppViewModelInstance = new AppViewModel();
            DiagnosticsLogger.Record($"App initialization completed. OS={Environment.OSVersion}, 64bit={Environment.Is64BitProcess}, App={AppViewModelInstance.CurrentVersionDisplay}");

            if (_defaultFontFamily is null && Resources.TryGetResource("AppFontFamily", ThemeVariant.Default, out var fontResource))
            {
                _defaultFontFamily = fontResource as FontFamily;
            }
            ApplyLanguageFontOverrides();
            if (LocalizationProvider.Service is { } locService)
            {
                locService.LanguageChanged += () =>
                {
                    _cachedDriveHealthLabel = L("Tray.Health.DefaultLabel", DefaultDriveHealthLabel);
                    if (_trayIcon is not null)
                    {
                        _trayIcon.ToolTipText = L("Tray.Tooltip", "VaultSync - snapshots & backups");
                    }
                    ApplyLanguageFontOverrides();
                    RefreshTrayMenu();
                };
            }

            var mainWindow = new MainWindow
            {
                DataContext = AppViewModelInstance
            };
            mainWindow.WindowState = WindowState.Maximized;
            desktop.MainWindow = mainWindow;
            ApplyArabicFontOverridesToWindow(desktop.MainWindow, IsArabicActive());
            if (desktop.Windows is INotifyCollectionChanged windowsChanged)
            {
                windowsChanged.CollectionChanged += (_, e) =>
                {
                    if (e.NewItems is null)
                        return;
                    foreach (var item in e.NewItems)
                    {
                        if (item is Window newWindow)
                        {
                            ApplyArabicFontOverridesToWindow(newWindow, IsArabicActive());
                        }
                    }
                };
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!TryShowOnboarding(desktop))
                {
                    TryShowWhatsNew(desktop);
                }
            });
            _ = Task.Run(CleanupStaleEncryptedOpenTempFolders);
            _ = HandleInitialActivationArgsAsync(desktop);

            // Small always-on-top widget that lights up for tray-started backups.
            var backupWidgetService = new BackupWidgetService(
                desktop,
                AppViewModelInstance.BackupsViewModel,
                () => BringMainWindowToFront(desktop));
            AppViewModelInstance.AttachBackupWidgetService(backupWidgetService);
            AppViewModelInstance.TrayMenuRefreshRequested += () =>
            {
                RefreshTrayMenu();
                _trayPanelService?.Refresh();
            };
            AppViewModelInstance.SettingsViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.ShowTrayIcon))
                {
                    UpdateTrayIconVisibility(desktop);
                }
            };

            // Wire a platform-aware system notification service; fall back to stub if unavailable.
            GlobalNotificationCenter.Instance.SystemNotificationService =
                CreateSystemNotificationService() ?? new StubSystemNotificationService();
            GlobalNotificationCenter.Instance.ShouldShowSystemNotification = request =>
            {
                var cfg = AppConfigStore.GetSnapshot();
                if (!cfg.Notifications.UseOsNotifications)
                    return false;
                if (!cfg.Notifications.OnBackupSuccess &&
                    !cfg.Notifications.OnBackupFailure &&
                    !cfg.Notifications.OnSnapshotSuccess &&
                    !cfg.Notifications.OnSnapshotFailure &&
                    !cfg.Notifications.OnLowDisk)
                    return false;

                if (cfg.Notifications.OnlyWhenInactive && MainWindow.IsForeground)
                    return false;

                return true;
            };

            // Read behavior config and, if enabled, create a tray/menu-bar icon.
            var config = AppConfigStore.GetSnapshot();
            if (config.Behavior?.ShowTrayIcon == true)
            {
                CreateTrayIcon(desktop);
            }

            StartUiWatchdog();
        }

        // Apply theme from stored config on startup
        ApplyThemeFromConfig();

        base.OnFrameworkInitializationCompleted();
    }

    private static void InitializeLocalizationProviderEarly()
    {
        if (LocalizationProvider.Service is not null)
            return;

        try
        {
            var localizationService = new LocalizationService();
            var cfg = AppConfigStore.GetSnapshot();
            if (!string.IsNullOrWhiteSpace(cfg.Advanced.Language))
            {
                localizationService.SetLanguage(cfg.Advanced.Language);
            }

            LocalizationProvider.Initialize(localizationService);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Localization] Early bootstrap failed: {ex.Message}");
        }
    }


    private static void StartUiWatchdog()
    {
        if (_uiHeartbeatTimer is not null)
            return;

        _uiHeartbeatTicks = DateTime.UtcNow.Ticks;
        _uiHeartbeatTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _uiHeartbeatTimer.Tick += (_, _) =>
        {
            Interlocked.Exchange(ref _uiHeartbeatTicks, DateTime.UtcNow.Ticks);
            if (Interlocked.Exchange(ref _uiHangReported, 0) == 1)
            {
                DiagnosticsLogger.Record("UI heartbeat resumed after stall.");
            }
        };
        _uiHeartbeatTimer.Start();

        _uiWatchdogTimer = new Timer(_ =>
        {
            var last = Interlocked.Read(ref _uiHeartbeatTicks);
            var ageMs = (DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc)).TotalMilliseconds;
            if (ageMs < 3000)
                return;

            if (Interlocked.Exchange(ref _uiHangReported, 1) == 1)
                return;

            var proc = System.Diagnostics.Process.GetCurrentProcess();
            DiagnosticsLogger.Record(
                $"UI hang suspected ({ageMs:0}ms). Threads={proc.Threads.Count}, " +
                $"WorkingSetMB={proc.WorkingSet64 / (1024 * 1024)}, GC0={GC.CollectionCount(0)}, GC1={GC.CollectionCount(1)}, GC2={GC.CollectionCount(2)}");
            DiagnosticsLogger.TryCollectDump($"ui_hang_{ageMs:0}ms");
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void ApplyLanguageFontOverrides()
    {
        if (IsArabicActive())
        {
            Resources["AppFontFamily"] = OperatingSystem.IsMacOS()
                ? ArabicMacFontFamily
                : ArabicFontFamily;
            ApplyArabicFontOverridesToWindows(true);
            return;
        }

        if (_defaultFontFamily is not null)
        {
            Resources["AppFontFamily"] = _defaultFontFamily;
        }
        ApplyArabicFontOverridesToWindows(false);
    }

    private static bool IsArabicActive()
    {
        return string.Equals(LocalizationProvider.Service?.CurrentLanguage, "ar", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyArabicFontOverridesToWindows(bool enable)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        foreach (var window in desktop.Windows)
        {
            ApplyArabicFontOverridesToWindow(window, enable);
        }
    }

    private void ApplyArabicFontOverridesToWindow(Window window, bool enable)
    {
        if (_arabicFontHooked.Add(window))
        {
            void Apply() => ApplyArabicFontOverridesToWindowCore(window, IsArabicActive());
            window.Opened += (_, __) => Apply();
            window.AttachedToVisualTree += (_, __) => Apply();
            window.Closed += (_, __) => _arabicFontHooked.Remove(window);
        }

        ApplyArabicFontOverridesToWindowCore(window, enable);
    }

    private void ApplyArabicFontOverridesToWindowCore(Window window, bool enable)
    {
        var fontFamily = OperatingSystem.IsMacOS() ? ArabicMacFontFamily : ArabicFontFamily;
        foreach (var textBlock in window.GetVisualDescendants().OfType<TextBlock>())
        {
            if (enable)
            {
                textBlock.FontFamily = fontFamily;
                if (textBlock.FontWeight >= FontWeight.SemiBold)
                {
                    textBlock.FontWeight = FontWeight.Normal;
                }
            }
            else
            {
                textBlock.ClearValue(TextBlock.FontFamilyProperty);
                textBlock.ClearValue(TextBlock.FontWeightProperty);
            }
        }
    }

    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Avoid creating multiple tray icons.
        if (_trayIcon != null)
            return;

        // Use a dedicated embedded tray icon resource.
        // Make sure Assets/vaultsync-tray.png exists and is marked as AvaloniaResource in the csproj.
        var uri = new Uri("avares://VaultSync.UI/Assets/vaultsync-tray.png");
        using var iconStream = AssetLoader.Open(uri);
        var trayIconSource = new WindowIcon(iconStream);

        _trayIcon = new TrayIcon
        {
            Icon = trayIconSource,
            ToolTipText = L("Tray.Tooltip", "VaultSync - snapshots & backups")
        };

        _trayMenu = new NativeMenu();
        PopulateTrayMenu(_trayMenu, desktop, policySummary: AppViewModelInstance?.GetBackupPolicyTraySummary());

        // macOS prefers the native menu; custom tray panels can fail to open.
        if (OperatingSystem.IsMacOS())
        {
            _trayIcon.Menu = _trayMenu;
            _trayIcon.Clicked += (_, _) =>
            {
                _lastTrayMenuOpenUtc = DateTime.UtcNow;
                _trayMenuSuppressUntilUtc = _lastTrayMenuOpenUtc.AddSeconds(10);
            };
        }
        else
        {
            // Allow right-click native menu while left-click opens the custom tray panel.
            _trayIcon.Menu = _trayMenu;
            _trayPanelService ??= new TrayPanelService(desktop, () => AppViewModelInstance);
            _trayIcon.Clicked += (_, _) => _trayPanelService?.Toggle();
        }
        _trayIcon.IsVisible = true;
    }

    private void TryShowWhatsNew(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (AppViewModelInstance is null)
            return;

        var cfg = AppConfigStore.Load();
        var currentVersion = AppViewModelInstance.CurrentVersionDisplay.TrimStart('v');
        if (string.IsNullOrWhiteSpace(currentVersion))
            return;

        if (string.Equals(cfg.Advanced.LastWhatsNewVersion, currentVersion, StringComparison.OrdinalIgnoreCase))
            return;

        var sections = LoadWhatsNewSections(currentVersion);
        if (sections.Count == 0)
        {
            sections.Add(new WhatsNewSection(L("WhatsNew.Section.General", "Highlights")));
            sections[0].Items.Add(L("WhatsNew.Fallback", "This update includes improvements and fixes across VaultSync."));
        }

        var vm = new WhatsNewViewModel($"v{currentVersion}");
        foreach (var section in sections)
        {
            vm.AddSection(section.Title, section.Items.ToArray());
        }

        var window = new WhatsNewWindow
        {
            DataContext = vm
        };

        vm.CloseRequested += () =>
        {
            cfg.Advanced.LastWhatsNewVersion = currentVersion;
            AppConfigStore.Save(cfg);
            window.Close();
        };

        window.ShowDialog(desktop.MainWindow);
    }

    private bool TryShowOnboarding(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (AppViewModelInstance is null)
            return false;

        var cfg = AppConfigStore.Load();
        var showForTesting = IsOnboardingAlwaysEnabledForTesting();
        if (!showForTesting)
        {
            var isFreshInstall = AppConfigStore.WasConfigMissingOnFirstLoad;
            var sentinelExists = File.Exists(OnboardingSentinelPath);

            if (!isFreshInstall || sentinelExists || cfg.Advanced.HasSeenOnboarding)
            {
                EnsureOnboardingSuppressed(cfg);
                return false;
            }

            MarkOnboardingSeen(cfg);
        }

        void Finish()
        {
            AppViewModelInstance.OnboardingTour.TourCompleted -= Finish;
            TryShowWhatsNew(desktop);
        }

        AppViewModelInstance.OnboardingTour.TourCompleted += Finish;
        AppViewModelInstance.OnboardingTour.Start();
        return true;
    }

    private static void EnsureOnboardingSuppressed(AppConfig cfg)
    {
        var needsConfigUpdate = !cfg.Advanced.HasSeenOnboarding;
        var needsSentinel = !File.Exists(OnboardingSentinelPath);
        if (!needsConfigUpdate && !needsSentinel)
            return;

        MarkOnboardingSeen(cfg);
    }

    private static void MarkOnboardingSeen(AppConfig cfg)
    {
        try
        {
            var dir = Path.GetDirectoryName(OnboardingSentinelPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            if (!File.Exists(OnboardingSentinelPath))
                File.WriteAllText(OnboardingSentinelPath, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch
        {
            // Best effort sentinel.
        }

        if (!cfg.Advanced.HasSeenOnboarding)
        {
            cfg.Advanced.HasSeenOnboarding = true;
            AppConfigStore.Save(cfg);
        }
    }

    private static bool IsOnboardingAlwaysEnabledForTesting()
    {
        if (ForceOnboardingAtStartupForTesting)
            return true;

        // Optional override for local/manual testing without code changes.
        var raw = Environment.GetEnvironmentVariable("VAULTSYNC_FORCE_ONBOARDING");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static List<WhatsNewSection> LoadWhatsNewSections(string currentVersion)
    {
        var sections = new List<WhatsNewSection>();
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "WHATS_NEW.md"),
            Path.Combine(baseDir, "docs", "WHATS_NEW.md"),
            Path.Combine(baseDir, "CHANGELOG.md"),
            Path.Combine(baseDir, "..", "CHANGELOG.md"),
            Path.Combine(baseDir, "..", "..", "CHANGELOG.md")
        };

        string? content = null;
        foreach (var path in candidates)
        {
            if (!File.Exists(path))
                continue;
            try
            {
                content = File.ReadAllText(path);
                break;
            }
            catch
            {
                content = null;
            }
        }

        if (string.IsNullOrWhiteSpace(content))
            return sections;

        var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var hasWhatsNewHeader = Array.Exists(lines, line => line.StartsWith("#", StringComparison.Ordinal) && line.Contains("What's New", StringComparison.OrdinalIgnoreCase));
        var startIndex = 0;
        if (!hasWhatsNewHeader)
        {
            var headerPrefix = $"## [{currentVersion}]";
            startIndex = Array.FindIndex(lines, line => line.StartsWith(headerPrefix, StringComparison.OrdinalIgnoreCase));
            if (startIndex < 0)
                return sections;
        }

        WhatsNewSection? currentSection = null;
        var lineStart = hasWhatsNewHeader ? 0 : startIndex + 1;
        for (var i = lineStart; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (!hasWhatsNewHeader && line.StartsWith("## [", StringComparison.Ordinal))
                break;

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                var title = line[4..].Trim();
                currentSection = new WhatsNewSection(title);
                sections.Add(currentSection);
                continue;
            }

            if (currentSection is null)
                continue;

            var trimmed = line.Trim();
            if (trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                var item = trimmed.TrimStart('-').Trim();
                if (!string.IsNullOrWhiteSpace(item))
                    currentSection.Items.Add(item);
            }
        }

        return sections;
    }

    private void DestroyTrayIcon()
    {
        if (_trayIcon is null)
            return;

        _trayIcon.IsVisible = false;
        _trayPanelService?.Hide();
        _trayPanelService?.Dispose();
        _trayPanelService = null;
        _trayIcon.Dispose();
        _trayIcon = null;
        _trayMenu = null;
    }

    private void UpdateTrayIconVisibility(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var cfg = AppConfigStore.Load();
        if (cfg.Behavior?.ShowTrayIcon == true)
        {
            if (_trayIcon is null)
            {
                CreateTrayIcon(desktop);
            }
            else
            {
                // Ensure the menu stays fresh when the icon is toggled on/off.
                RefreshTrayMenu();
            }
        }
        else
        {
            DestroyTrayIcon();
        }
    }

    private void PopulateTrayMenu(
        NativeMenu menu,
        IClassicDesktopStyleApplicationLifetime desktop,
        IReadOnlyList<AppViewModel.TrayProjectBackups>? recentBackups = null,
        string? policySummary = null)
    {
        // Build a small context menu: header / Open / Backup / Snapshot / Recent backups / Quit.
        menu.Items.Clear();

        AddTrayHeader(menu);

        var destinationSummaries = AppViewModelInstance?.GetDestinationProbeSummaries()
                                   ?? Array.Empty<AppViewModel.DestinationProbeSummary>();

        var cfg = AppConfigStore.Load();
        var configuredDestinations = GetConfiguredDestinations(cfg);

        var (destinationsTitle, destinationsStatus) =
            GetDestinationStatus(destinationSummaries, configuredDestinations);

        menu.Items.Add(new NativeMenuItem($"{destinationsTitle} - {destinationsStatus}") { IsEnabled = false });
        if (!string.IsNullOrWhiteSpace(policySummary))
        {
            menu.Items.Add(new NativeMenuItem(policySummary) { IsEnabled = false });
        }
        menu.Items.Add(new NativeMenuItemSeparator());

        var openItem = BuildOpenTrayItem(desktop);

        // ---------- Storage health ----------
        var healthItem = BuildDriveHealthItem(desktop);

        // ---------- Destinations submenu ----------
        var destinationRootItem = BuildDestinationMenu(destinationsTitle, destinationSummaries, configuredDestinations);

        // ---------- Backup submenu ----------
        var backupRootItem = BuildBackupMenu(desktop);

        // ---------- Snapshot submenu ----------
        var snapshotRootItem = BuildSnapshotMenu(desktop);

        // ---------- Recent backups (keep/delete) ----------
        var manageBackupsRoot = BuildRecentBackupsMenu(desktop, recentBackups);

        var separator1 = new NativeMenuItemSeparator();
        var separator2 = new NativeMenuItemSeparator();
        var settingsItem = BuildTraySettingsItem(desktop);
        var quitItem = BuildQuitTrayItem(desktop);

        menu.Items.Add(openItem);
        menu.Items.Add(backupRootItem);
        menu.Items.Add(snapshotRootItem);
        menu.Items.Add(manageBackupsRoot);
        menu.Items.Add(separator1);
        menu.Items.Add(destinationRootItem);
        if (healthItem is not null)
        {
            menu.Items.Add(healthItem);
        }
        menu.Items.Add(settingsItem);
        menu.Items.Add(separator2);
        menu.Items.Add(quitItem);

    }

    private void AddTrayHeader(NativeMenu menu)
    {
        var headerText = L("Tray.Header", "VaultSync");
        var versionLabel = AppViewModelInstance?.CurrentVersionDisplay;
        if (!string.IsNullOrWhiteSpace(versionLabel))
        {
            headerText = $"{headerText} {versionLabel}";
        }
        menu.Items.Add(new NativeMenuItem(headerText) { IsEnabled = false });
    }

    private static List<BackupDestination> GetConfiguredDestinations(AppConfig cfg)
    {
        var configuredDestinations = new List<BackupDestination>();

        if (cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 })
        {
            configuredDestinations = cfg.Backups.Destinations
                .Where(d => d.Active)
                .ToList();
        }
        else if (!string.IsNullOrWhiteSpace(cfg.Backups.BackupLocation))
        {
            configuredDestinations.Add(new BackupDestination
            {
                Alias       = "Primary",
                Path        = cfg.Backups.BackupLocation,
                Active      = true,
                PreMounted  = true,
                AutoMount   = false,
                AutoUnmount = false
            });
        }

        return configuredDestinations;
    }

    private (string Title, string Status) GetDestinationStatus(
        IReadOnlyList<AppViewModel.DestinationProbeSummary> destinationSummaries,
        List<BackupDestination> configuredDestinations)
    {
        var destinationsTitle = L("Tray.Destinations.Title", "Destinations");
        var destinationsStatus = string.Empty;
        if (destinationSummaries.Any())
        {
            var reachableCount = destinationSummaries.Count(d => d.Reachable);
            destinationsStatus = reachableCount == destinationSummaries.Count
                ? L("Tray.Destinations.Ready", "Ready")
                : L("Tray.Destinations.Unreachable", "Unreachable");
        }
        else if (configuredDestinations.Any())
        {
            destinationsStatus = L("Tray.Destinations.Ready", "Ready");
        }
        else
        {
            destinationsStatus = L("Tray.Destinations.None", "No destinations configured");
        }

        return (destinationsTitle, destinationsStatus);
    }

    private NativeMenuItem BuildOpenTrayItem(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var openItem = new NativeMenuItem(L("Tray.Open", "Open VaultSync"));
        openItem.Click += (_, _) =>
        {
            var window = desktop.MainWindow;
            if (window is null)
                return;

            // If the window was hidden (RunInBackground + X pressed), show it again.
            if (!window.IsVisible)
                window.Show();

            // If minimized, restore.
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.Activate();
        };

        return openItem;
    }

    private NativeMenuItem BuildDestinationMenu(
        string destinationsTitle,
        IReadOnlyList<AppViewModel.DestinationProbeSummary> destinationSummaries,
        List<BackupDestination> configuredDestinations)
    {
        var destinationRootItem = new NativeMenuItem(destinationsTitle);
        var destinationMenu = new NativeMenu();

        if (destinationSummaries.Any())
        {
            foreach (var dest in destinationSummaries)
            {
                var status = dest.Reachable
                    ? L("Tray.Destinations.Ready", "Ready")
                    : L("Tray.Destinations.Unreachable", "Unreachable");
                var text = string.IsNullOrWhiteSpace(dest.Alias)
                    ? $"{dest.Path} - {status}"
                    : $"{dest.Alias} - {status}";

                var detail = new NativeMenuItem(text) { IsEnabled = false };
                destinationMenu.Items.Add(detail);
            }
        }
        else
        {
            if (configuredDestinations.Any())
            {
                foreach (var dest in configuredDestinations)
                {
                    var label = string.IsNullOrWhiteSpace(dest.Alias)
                        ? dest.Path ?? string.Empty
                        : dest.Alias;
                    destinationMenu.Items.Add(new NativeMenuItem(label) { IsEnabled = false });
                }
            }
            else
            {
                destinationMenu.Items.Add(new NativeMenuItem(L("Tray.Destinations.None", "No destinations configured")) { IsEnabled = false });
            }
        }

        destinationRootItem.Menu = destinationMenu;
        return destinationRootItem;
    }

    private NativeMenuItem BuildBackupMenu(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var backupRootItem = new NativeMenuItem(L("Tray.Backup.Title", "Backup"));
        var backupMenu = new NativeMenu();

        var backupProjects = AppViewModelInstance?.GetProjectsForBackupTray()
                             ?? Array.Empty<ProjectBackupItem>();

        if (backupProjects.Any())
        {
            var backupAllItem = new NativeMenuItem(L("Tray.Backup.All", "Backup all projects"));
            backupAllItem.Click += (_, _) =>
            {
                BringWindowToFrontIfUserWants(desktop);
                AppViewModelInstance?.RequestBackupAllFromTray();
            };
            backupMenu.Items.Add(backupAllItem);
            backupMenu.Items.Add(new NativeMenuItemSeparator());

            foreach (var project in backupProjects)
            {
                var projectId = project.Id;
                var projectName = project.Name;

                var projectBackupItem = new NativeMenuItem(projectName);
                projectBackupItem.Click += (_, _) =>
                {
                    BringWindowToFrontIfUserWants(desktop);
                    AppViewModelInstance?.RequestBackupProjectFromTray(projectId);
                };

                backupMenu.Items.Add(projectBackupItem);
            }
        }
        else
        {
            backupMenu.Items.Add(new NativeMenuItem(L("Tray.Common.NoProjects", "No projects available")) { IsEnabled = false });
        }

        backupRootItem.Menu = backupMenu;
        return backupRootItem;
    }

    private NativeMenuItem BuildSnapshotMenu(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var snapshotRootItem = new NativeMenuItem(L("Tray.Snapshot.Title", "Snapshot"));
        var snapshotMenu = new NativeMenu();

        var snapshotProjects = AppViewModelInstance?.GetProjectsForSnapshotTray()
                               ?? Array.Empty<ProjectItemViewModel>();

        if (snapshotProjects.Any())
        {
            var snapshotAllItem = new NativeMenuItem(L("Tray.Snapshot.All", "Snapshot all projects"));
            snapshotAllItem.Click += async (_, _) =>
            {
                BringWindowToFrontIfUserWants(desktop);

                if (AppViewModelInstance is not null)
                {
                    await AppViewModelInstance.TakeSnapshotAllFromTrayAsync();
                }
            };
            snapshotMenu.Items.Add(snapshotAllItem);
            snapshotMenu.Items.Add(new NativeMenuItemSeparator());

            foreach (var project in snapshotProjects)
            {
                var projectName = project.Name;

                var projectSnapshotItem = new NativeMenuItem(projectName);
                projectSnapshotItem.Click += async (_, _) =>
                {
                    BringWindowToFrontIfUserWants(desktop);

                    if (AppViewModelInstance is not null)
                    {
                        await AppViewModelInstance.TakeSnapshotForProjectFromTrayAsync(projectName);
                    }
                };

                snapshotMenu.Items.Add(projectSnapshotItem);
            }
        }
        else
        {
            snapshotMenu.Items.Add(new NativeMenuItem(L("Tray.Common.NoProjects", "No projects available")) { IsEnabled = false });
        }

        snapshotRootItem.Menu = snapshotMenu;
        return snapshotRootItem;
    }

    private NativeMenuItem BuildRecentBackupsMenu(
        IClassicDesktopStyleApplicationLifetime desktop,
        IReadOnlyList<AppViewModel.TrayProjectBackups>? recentBackups)
    {
        var manageBackupsRoot = new NativeMenuItem(L("Tray.Recent.Title", "Recent backups"));
        var manageBackupsMenu = new NativeMenu();

        var recentByProject = recentBackups
                              ?? AppViewModelInstance?.GetRecentBackupsForTray(MaxRecentBackupsPerProject)
                              ?? Array.Empty<AppViewModel.TrayProjectBackups>();

        var latestOnlyToggle = new NativeMenuItem(L("Tray.Recent.LatestOnly", "Show only latest per project"))
        {
            IsChecked = _trayRecentLatestOnly
        };
        latestOnlyToggle.Click += (_, _) =>
        {
            _trayRecentLatestOnly = !_trayRecentLatestOnly;
            RefreshTrayMenu();
        };
        manageBackupsMenu.Items.Add(latestOnlyToggle);
        manageBackupsMenu.Items.Add(new NativeMenuItemSeparator());

        var anyBackups = false;
        foreach (var project in recentByProject)
        {
            if (!project.Backups.Any())
                continue;

            anyBackups = true;

            var projectMenuItem = new NativeMenuItem(project.ProjectName);
            var projectMenu = new NativeMenu();

            var backupsToShow = _trayRecentLatestOnly
                ? project.Backups.Take(1)
                : project.Backups;

            foreach (var backup in backupsToShow)
            {
                var backupItem = new NativeMenuItem(backup.Label);
                var recentBackupMenu = new NativeMenu();
                var keepLabel = backup.IsProtected ? L("Tray.Recent.Unkeep", "Unkeep") : L("Tray.Recent.Keep", "Keep");
                var keepItem = new NativeMenuItem(keepLabel);
                keepItem.Click += (_, _) => AppViewModelInstance?.ToggleBackupProtectionFromTray(backup.Id);

                var deleteItem = new NativeMenuItem(L("Tray.Recent.Delete", "Delete"));
                deleteItem.Click += (_, _) => AppViewModelInstance?.DeleteBackupFromTray(backup.Id);

                var openFolderItem = new NativeMenuItem(L("Tray.Recent.OpenFolder", "Open folder"));
                openFolderItem.Click += (_, _) => AppViewModelInstance?.OpenBackupFolderFromTray(backup.Id);

                var viewInAppItem = new NativeMenuItem(L("Tray.Recent.ViewInApp", "View in VaultSync"));
                viewInAppItem.Click += (_, _) => AppViewModelInstance?.ShowBackupInAppFromTray(backup.ProjectId);

                recentBackupMenu.Items.Add(openFolderItem);
                recentBackupMenu.Items.Add(viewInAppItem);
                recentBackupMenu.Items.Add(new NativeMenuItemSeparator());
                recentBackupMenu.Items.Add(keepItem);
                recentBackupMenu.Items.Add(deleteItem);

                backupItem.Menu = recentBackupMenu;
                projectMenu.Items.Add(backupItem);
            }

            projectMenuItem.Menu = projectMenu;
            manageBackupsMenu.Items.Add(projectMenuItem);
            manageBackupsMenu.Items.Add(new NativeMenuItemSeparator());
        }

        if (anyBackups)
        {
            // Trim trailing separator after the last project.
            if (manageBackupsMenu.Items.LastOrDefault() is NativeMenuItemSeparator)
                manageBackupsMenu.Items.RemoveAt(manageBackupsMenu.Items.Count - 1);
            manageBackupsMenu.Items.Add(new NativeMenuItemSeparator());

            var openBackups = new NativeMenuItem(L("Tray.Recent.OpenInApp", "Open in VaultSync"));
            openBackups.Click += (_, _) =>
            {
                BringMainWindowToFront(desktop);
                AppViewModelInstance?.NavigateBackups?.Execute(null);
            };
            manageBackupsMenu.Items.Add(openBackups);
        }
        else
        {
            manageBackupsMenu.Items.Add(new NativeMenuItem(L("Tray.Recent.None", "No backups yet")) { IsEnabled = false });
        }

        manageBackupsRoot.Menu = manageBackupsMenu;
        return manageBackupsRoot;
    }

    private NativeMenuItem BuildTraySettingsItem(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var settingsItem = new NativeMenuItem(L("Nav.Settings", "Settings"));
        settingsItem.Click += (_, _) =>
        {
            BringMainWindowToFront(desktop);
            AppViewModelInstance?.NavigateSettings?.Execute(null);
        };
        return settingsItem;
    }

    private static NativeMenuItem BuildQuitTrayItem(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var quitItem = new NativeMenuItem(L("Tray.Quit", "Quit VaultSync"));
        quitItem.Click += (_, _) =>
        {
            // Tell the window we're intentionally shutting down so it doesn't hijack the close.
            DiagnosticsLogger.RecordWithStack("Quit tray menu clicked.");
            IsShuttingDown = true;
            desktop.Shutdown();
        };
        return quitItem;
    }

    /// <summary>
    /// Global crash/exception hooks that log anonymised details before the process exits.
    /// </summary>
    private static void WireGlobalExceptionHandlers()
    {
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                if (ex is not null)
                {
                    DiagnosticsLogger.RecordException("Global unhandled exception", ex, includeStack: true);
                    Telemetry.Log("app_crash", b => b
                        .WithException(ex)
                        .WithCode("source", "unhandled"));
                }
                else
                {
                    DiagnosticsLogger.Record("Global unhandled exception: non-Exception object.");
                    Telemetry.Log("app_crash", b => b
                        .WithCode("source", "unhandled")
                        .WithCode("detail", "non_exception"));
                }
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try
                {
                    DiagnosticsLogger.RecordException("Global unobserved task exception", e.Exception, includeStack: true);
                    Telemetry.Log("app_crash", b => b
                        .WithException(e.Exception)
                        .WithCode("source", "unobserved_task"));
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    e.SetObserved();
                }
            };
        }
        catch
        {
            // Swallow to avoid startup failures; telemetry must never break the app.
        }
    }

    /// <summary>
    /// Lifecycle breadcrumbs for start/exit to correlate crash sessions.
    /// </summary>
    private static void WireLifecycleBreadcrumbs(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            Telemetry.SetSessionId(Guid.NewGuid());

            Telemetry.Log("app_start");

            desktop.Exit += (_, _) =>
            {
                DiagnosticsLogger.Record($"Desktop exit event. IsShuttingDown={IsShuttingDown}, IsCrashing={IsCrashing}.");
                CleanupAllEncryptedOpenTempFolders();
                Telemetry.Log("app_exit", b => b.WithCode("source", "desktop_exit"));
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                DiagnosticsLogger.Record($"ProcessExit event. IsShuttingDown={IsShuttingDown}, IsCrashing={IsCrashing}.");
                CleanupAllEncryptedOpenTempFolders();
                Telemetry.Log("app_exit", b => b.WithCode("source", "process_exit"));
            };
        }
        catch
        {
            // Never throw from telemetry wiring.
        }
    }

    private NativeMenuItem? BuildDriveHealthItem(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var cfg        = AppViewModelInstance?.GetConfigSnapshot() ?? AppConfigStore.Load();
            var backupRoot = cfg.Backups.BackupLocation ?? string.Empty;
            var driveLabel = FormatDriveLabel(backupRoot);
            if (_cachedDriveHealthIsNetwork)
            {
                return null;
            }
            if (_cachedDriveHealthLabel == DefaultDriveHealthLabel)
            {
                _cachedDriveHealthLabel = L("Tray.Health.DefaultLabel", DefaultDriveHealthLabel);
            }

            var healthTitle = L("Tray.Health.Title", "Storage health");
            if (!string.IsNullOrWhiteSpace(backupRoot) && !string.IsNullOrWhiteSpace(driveLabel))
            {
                healthTitle = $"{healthTitle} ({driveLabel})";
            }
            var healthMenu = new NativeMenuItem(healthTitle);
            var statusMenu = new NativeMenu();

            var statusLabel = string.IsNullOrWhiteSpace(backupRoot)
                ? L("Tray.Health.NoPath", "Backup path not set")
                : _cachedDriveHealthLabel;

            statusMenu.Items.Add(new NativeMenuItem(statusLabel) { IsEnabled = false });
            statusMenu.Items.Add(new NativeMenuItemSeparator());

            var recheck = new NativeMenuItem(L("Tray.Health.Recheck", "Recheck now"));
            recheck.Click += async (_, _) => await RecheckDriveHealthAsync(desktop);
            statusMenu.Items.Add(recheck);

            healthMenu.Menu = statusMenu;
            return healthMenu;
        }
        catch
        {
            return new NativeMenuItem(L("Tray.Health.Unavailable", "Storage health: unavailable")) { IsEnabled = false };
        }
    }

    public void RefreshTrayMenu()
    {
        _ = RefreshTrayMenuAsync();
    }

    public async Task RefreshTrayMenuAsync()
    {
        if (_trayIcon is null)
            return;

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var now = DateTime.UtcNow;
        var minRefreshInterval = OperatingSystem.IsMacOS()
            ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromSeconds(1);
        if (now - _lastTrayMenuRefreshUtc < minRefreshInterval)
            return;
        if (OperatingSystem.IsMacOS() && now < _trayMenuSuppressUntilUtc)
            return;
        if (_trayMenuRefreshFailureCount >= 3 &&
            now - _lastTrayMenuRefreshFailureUtc < TimeSpan.FromSeconds(10))
            return;

        if (Interlocked.Exchange(ref _trayMenuRefreshInFlight, 1) == 1)
        {
            Interlocked.Exchange(ref _trayMenuRefreshQueued, 1);
            return;
        }

        _lastTrayMenuRefreshUtc = now;
        var trayResult = await Task.Run(() =>
        {
            var viewModel = AppViewModelInstance;
            var recentBackups = viewModel?.GetRecentBackupsForTray(MaxRecentBackupsPerProject)
                                ?? Array.Empty<AppViewModel.TrayProjectBackups>();
            var destinations = viewModel?.GetDestinationProbeSummaries()
                               ?? Array.Empty<AppViewModel.DestinationProbeSummary>();
            var policySummary = viewModel?.GetBackupPolicyTraySummary() ?? string.Empty;
            var policySignature = viewModel?.GetBackupPolicySignatureForTray() ?? string.Empty;
            var signatureValue = BuildTrayMenuSignature(recentBackups, destinations, policySignature, policySummary);
            return (Recent: recentBackups, Signature: signatureValue, PolicySummary: policySummary);
        });
        var recent = trayResult.Recent;
        var signature = trayResult.Signature;
        var policySummary = trayResult.PolicySummary;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_trayMenuSignature == signature && _trayMenu is not null)
                {
                    return;
                }

                var newMenu = new NativeMenu();
                PopulateTrayMenu(newMenu, desktop, recent, policySummary);
                _trayMenu = newMenu;
                _trayIcon.Menu = newMenu;
                _trayMenuSignature = signature;

                _trayMenuRefreshFailureCount = 0;
            }
            catch (Exception ex)
            {
                // Best-effort: avoid crashing the app if tray menu rebuild fails.
                if (OperatingSystem.IsMacOS() &&
                    ex.Message.Contains("menu being updated does not match", StringComparison.OrdinalIgnoreCase))
                {
                    _trayMenuSuppressUntilUtc = DateTime.UtcNow.AddSeconds(10);
                }

                Console.WriteLine($"[Tray] Failed to refresh tray menu: {ex.Message}");
                _trayMenuRefreshFailureCount++;
                _lastTrayMenuRefreshFailureUtc = DateTime.UtcNow;
            }
            finally
            {
                Interlocked.Exchange(ref _trayMenuRefreshInFlight, 0);
                if (Interlocked.Exchange(ref _trayMenuRefreshQueued, 0) == 1)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(200).ConfigureAwait(false);
                        await RefreshTrayMenuAsync().ConfigureAwait(false);
                    });
                }
            }
        });
    }

    private static string BuildTrayMenuSignature(
        IReadOnlyList<AppViewModel.TrayProjectBackups> recent,
        IReadOnlyList<AppViewModel.DestinationProbeSummary> destinations,
        string policySignature,
        string policySummary)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(_trayRecentLatestOnly ? "latest=1;" : "latest=0;");
        sb.Append(_cachedDriveHealthStatus).Append(';')
          .Append(_cachedDriveHealthLabel).Append(';')
          .Append(_cachedDriveHealthIsNetwork).Append(';')
          .Append(policySignature ?? string.Empty).Append(';')
          .Append(policySummary ?? string.Empty).Append(';');

        foreach (var dest in destinations.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(dest.Id).Append('|')
              .Append(dest.Reachable).Append('|')
              .Append(dest.LastChecked.ToString("O")).Append(';');
        }

        foreach (var project in recent)
        {
            sb.Append(project.ProjectId).Append('|').Append(project.ProjectName).Append(';');
            foreach (var backup in project.Backups)
            {
                sb.Append(backup.Id).Append('|')
                  .Append(backup.Label).Append('|')
                  .Append(backup.IsProtected).Append('|')
                  .Append(backup.ProjectId).Append(';');
            }
        }

        return sb.ToString();
    }


    private static void BringWindowToFrontIfUserWants(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var window = desktop.MainWindow;
        if (window is null)
            return;

        var config = AppConfigStore.Load();
        if (config.Behavior?.ShowWindowOnTrayActions != true)
            return;

        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }

    public static void ActivateMainWindowFromSignal()
    {
        ActivateFromSignal("activate");
    }

    public static void ActivateFromSignal(string? payload)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var payloadKind = payload is { Length: > 0 } && payload.StartsWith("open-vse|", StringComparison.Ordinal)
            ? "open-vse"
            : "activate";
        DiagnosticsLogger.Record($"Activation signal received; payloadKind='{payloadKind}'.");
        Dispatcher.UIThread.Post(async () =>
        {
            BringMainWindowToFront(desktop);
            await HandleActivationPayloadAsync(desktop, payload).ConfigureAwait(false);
        });
    }

    private static async Task HandleInitialActivationArgsAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        if (args.Length == 0)
            return;

        var encryptedArchivePath = args.FirstOrDefault(IsEncryptedArchivePath);
        if (string.IsNullOrWhiteSpace(encryptedArchivePath))
            return;

        await HandleEncryptedArchiveOpenRequestAsync(desktop, encryptedArchivePath).ConfigureAwait(false);
    }

    private static async Task HandleActivationPayloadAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || string.Equals(payload, "activate", StringComparison.OrdinalIgnoreCase))
            return;

        if (!payload.StartsWith("open-vse|", StringComparison.Ordinal))
            return;

        var encodedPath = payload["open-vse|".Length..];
        if (string.IsNullOrWhiteSpace(encodedPath))
            return;

        string archivePath;
        try
        {
            archivePath = Encoding.UTF8.GetString(Convert.FromBase64String(encodedPath));
        }
        catch
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(archivePath))
            await HandleEncryptedArchiveOpenRequestAsync(desktop, archivePath).ConfigureAwait(false);
    }

    private static async Task HandleEncryptedArchiveOpenRequestAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        string archivePath)
    {
        if (Interlocked.Exchange(ref _encryptedOpenInFlight, 1) == 1)
            return;

        try
        {
        if (!File.Exists(archivePath))
        {
            await ShowInfoDialogAsync(
                desktop,
                L("Backups.OpenEncrypted.Title", "Open encrypted backup"),
                Lf("Backups.OpenEncrypted.MissingFile", "The selected encrypted backup was not found: {0}", archivePath))
                .ConfigureAwait(false);
            return;
        }

        while (true)
        {
            var prompt = await PromptEncryptedArchivePasswordAsync(desktop, archivePath).ConfigureAwait(false);
            if (!prompt.Confirmed)
                return;

            if (string.IsNullOrWhiteSpace(prompt.Password))
            {
                await ShowInfoDialogAsync(
                    desktop,
                    L("Backups.OpenEncrypted.Title", "Open encrypted backup"),
                    L("Backups.Restore.EncryptedPasswordRequired", "A password is required to restore encrypted backups."))
                    .ConfigureAwait(false);
                continue;
            }

            try
            {
                var extractedDir = await Task.Run(() => ExtractEncryptedArchive(archivePath, prompt.Password)).ConfigureAwait(false);
                Process.Start(new ProcessStartInfo
                {
                    FileName = extractedDir,
                    UseShellExecute = true
                });
                ScheduleEncryptedOpenTempCleanup(extractedDir);
                return;
            }
            catch (Exception ex) when (string.Equals(ex.Message, BackupArchiveCryptoService.InvalidPasswordOrCorruptedMessage, StringComparison.Ordinal))
            {
                await ShowInfoDialogAsync(
                    desktop,
                    L("Backups.OpenEncrypted.Title", "Open encrypted backup"),
                    L("Backups.Status.RestoreWrongPassword", "Restore failed: invalid password or encrypted backup is corrupted."))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ShowInfoDialogAsync(
                    desktop,
                    L("Backups.OpenEncrypted.Title", "Open encrypted backup"),
                    ex.Message)
                    .ConfigureAwait(false);
                return;
            }
        }
        }
        finally
        {
            Interlocked.Exchange(ref _encryptedOpenInFlight, 0);
        }
    }

    private static async Task<(bool Confirmed, string Password)> PromptEncryptedArchivePasswordAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        string archivePath)
    {
        var owner = desktop.MainWindow;
        if (owner is null)
            return (false, string.Empty);

        var tcs = new TaskCompletionSource<(bool Confirmed, string Password)>();
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var title = new TextBlock
            {
                Text = L("Backups.Restore.EncryptedPasswordTitle", "Encrypted backup password"),
                FontSize = 18,
                FontWeight = FontWeight.SemiBold
            };

            var prompt = new TextBlock
            {
                Text = Lf("Backups.OpenEncrypted.PasswordPrompt", "Enter the password to open '{0}'.", Path.GetFileName(archivePath)),
                TextWrapping = TextWrapping.Wrap
            };

            var passwordLabel = new TextBlock
            {
                Text = L("Backups.Restore.EncryptedPasswordLabel", "Password"),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var passwordBox = new TextBox
            {
                Width = 320,
                PasswordChar = '●'
            };

            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10
            };

            var cancelButton = new Button
            {
                Content = L("Common.Cancel", "Cancel"),
                MinWidth = 120
            };
            cancelButton.Classes.Add("action-ghost");

            var openButton = new Button
            {
                Content = L("Common.Open", "Open"),
                MinWidth = 140
            };
            openButton.Classes.Add("action-primary");

            Window? window = null;
            var confirmed = false;
            cancelButton.Click += (_, _) => window?.Close();
            openButton.Click += (_, _) =>
            {
                confirmed = true;
                window?.Close();
            };

            buttonRow.Children.Add(cancelButton);
            buttonRow.Children.Add(openButton);

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(title);
            content.Children.Add(prompt);
            content.Children.Add(passwordLabel);
            content.Children.Add(passwordBox);
            content.Children.Add(buttonRow);

            var card = new Border
            {
                Padding = new Thickness(18),
                Margin = new Thickness(16)
            };
            card.Classes.Add("card");
            card.Child = content;

            window = new Window
            {
                Title = L("Backups.OpenEncrypted.Title", "Open encrypted backup"),
                Content = card,
                CanResize = false,
                Width = 540,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Icon = owner.Icon
            };

            await window.ShowDialog(owner);
            tcs.TrySetResult((confirmed, passwordBox.Text ?? string.Empty));
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    private static async Task ShowInfoDialogAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        string title,
        string message)
    {
        var owner = desktop.MainWindow;
        if (owner is null)
            return;

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var messageBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var okButton = new Button
            {
                Content = L("Common.Ok", "OK"),
                MinWidth = 120,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            okButton.Classes.Add("action-primary");

            Window? window = null;
            okButton.Click += (_, _) => window?.Close();

            var stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(messageBlock);
            stack.Children.Add(okButton);

            var card = new Border
            {
                Padding = new Thickness(18),
                Margin = new Thickness(16)
            };
            card.Classes.Add("card");
            card.Child = stack;

            window = new Window
            {
                Title = title,
                Content = card,
                CanResize = false,
                Width = 520,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Icon = owner.Icon
            };

            await window.ShowDialog(owner);
        });
    }

    private static string ExtractEncryptedArchive(string archivePath, string password)
    {
        var sourceArchivePath = archivePath;
        var sourceDir = Path.GetDirectoryName(archivePath);
        if (string.IsNullOrWhiteSpace(sourceDir))
            throw new InvalidOperationException("Unable to resolve archive source directory.");

        string? copiedSourceRoot = null;
        if (!string.Equals(Path.GetFileName(archivePath), BackupArchiveCryptoService.EncryptedArchiveFileName, StringComparison.OrdinalIgnoreCase))
        {
            copiedSourceRoot = Path.Combine(Path.GetTempPath(), $"vaultsync-open-src-{Guid.NewGuid():N}");
            Directory.CreateDirectory(copiedSourceRoot);
            var copiedSourcePath = Path.Combine(copiedSourceRoot, BackupArchiveCryptoService.EncryptedArchiveFileName);
            File.Copy(archivePath, copiedSourcePath, overwrite: true);
            sourceArchivePath = copiedSourcePath;
            sourceDir = copiedSourceRoot;
        }

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"vaultsync-open-{Guid.NewGuid():N}");
        var stagingArchive = Path.Combine(stagingRoot, BackupArchiveCryptoService.PlainArchiveFileName);
        var extractDir = Path.Combine(stagingRoot, "content");

        try
        {
            Directory.CreateDirectory(extractDir);
            if (!File.Exists(sourceArchivePath))
                throw new FileNotFoundException("Encrypted backup archive not found.", sourceArchivePath);

            var cryptoService = new BackupArchiveCryptoService();
            cryptoService.DecryptArchiveToPlainZip(sourceDir, password, stagingArchive);
            ZipFile.ExtractToDirectory(stagingArchive, extractDir, overwriteFiles: true);
            return extractDir;
        }
        catch
        {
            if (Directory.Exists(stagingRoot))
            {
                try { Directory.Delete(stagingRoot, recursive: true); }
                catch { }
            }
            throw;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(copiedSourceRoot) && Directory.Exists(copiedSourceRoot))
            {
                try { Directory.Delete(copiedSourceRoot, recursive: true); }
                catch { }
            }
        }
    }

    private static void CleanupStaleEncryptedOpenTempFolders()
    {
        try
        {
            var tempRoot = Path.GetTempPath();
            var now = DateTime.UtcNow;
            var dirs = Directory.GetDirectories(tempRoot, "vaultsync-open-*", SearchOption.TopDirectoryOnly);
            foreach (var dir in dirs)
            {
                try
                {
                    var createdUtc = Directory.GetCreationTimeUtc(dir);
                    var modifiedUtc = Directory.GetLastWriteTimeUtc(dir);
                    var referenceUtc = createdUtc > modifiedUtc ? createdUtc : modifiedUtc;
                    if ((now - referenceUtc) < EncryptedOpenTempRetention)
                        continue;

                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // Best effort cleanup; skip locked folders.
                }
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static void CleanupAllEncryptedOpenTempFolders()
    {
        try
        {
            var tempRoot = Path.GetTempPath();
            var dirs = Directory.GetDirectories(tempRoot, "vaultsync-open-*", SearchOption.TopDirectoryOnly);
            foreach (var dir in dirs)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // Best effort cleanup; skip locked folders.
                }
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static void ScheduleEncryptedOpenTempCleanup(string extractedDir)
    {
        var stagingRoot = ResolveEncryptedOpenStagingRoot(extractedDir);
        if (string.IsNullOrWhiteSpace(stagingRoot))
            return;
        var delay = GetEncryptedOpenAutoCleanupDelay();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        });
    }

    private static string? ResolveEncryptedOpenStagingRoot(string extractedDir)
    {
        if (string.IsNullOrWhiteSpace(extractedDir))
            return null;

        try
        {
            var full = Path.GetFullPath(extractedDir);
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            if (!full.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                return null;

            var current = new DirectoryInfo(full);
            while (current is not null)
            {
                if (current.Name.StartsWith("vaultsync-open-", StringComparison.OrdinalIgnoreCase))
                    return current.FullName;
                current = current.Parent;
            }
        }
        catch
        {
            // Best effort path validation.
        }

        return null;
    }

    private static TimeSpan GetEncryptedOpenAutoCleanupDelay()
    {
        try
        {
            var cfg = AppConfigStore.Load();
            var minutes = Math.Clamp(
                cfg?.Backups?.Encryption?.OpenUnlockTimeoutMinutes ?? DefaultEncryptedOpenTimeoutMinutes,
                1,
                240);
            return TimeSpan.FromMinutes(minutes);
        }
        catch
        {
            return TimeSpan.FromMinutes(DefaultEncryptedOpenTimeoutMinutes);
        }
    }

    private static bool IsEncryptedArchivePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.StartsWith("-", StringComparison.Ordinal))
            return false;

        return value.EndsWith(".vse", StringComparison.OrdinalIgnoreCase);
    }

    private static void BringMainWindowToFront(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var window = desktop.MainWindow;
        if (window is null)
            return;

        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }

    private static ISystemNotificationService? CreateSystemNotificationService()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                // macOS Notification Center implementation.
                return new MacSystemNotificationService();
            }

            if (OperatingSystem.IsWindows())
            {
                // Windows toast/notification implementation.
                return new WindowsSystemNotificationService();
            }
        }
        catch (Exception ex)
        {
        }

        // On unsupported platforms (or on failure), return null
        // so the caller can fall back to a stub implementation.
        return null;
    }

    private void ApplyThemeFromConfig()
    {
        var config = AppConfigStore.Load();
        ThemeManager.ApplyAppearance(config.Appearance);
        ThemeManager.ApplyCompactLayout(config.Appearance.CompactLayout);
    }

    public void ApplyTheme(string themeOption)
    {
        var config = AppConfigStore.Load();
        config.Appearance.Theme = themeOption;
        ThemeManager.ApplyAppearance(config.Appearance);
        AppConfigStore.Save(config);
    }

    private static string FormatDriveLabel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return L("DriveHealth.UnknownDrive", "drive");

        try
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrWhiteSpace(root))
            {
                return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        catch
        {
            // ignore and fall back
        }

        // UNC paths: try to take \\server\share
        if (path.StartsWith("\\\\") || path.StartsWith("//"))
        {
            var parts = path.Trim('\\', '/').Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return $"\\\\{parts[0]}\\{parts[1]}";
        }

        return path;
    }

    private static string DescribeHealth(DriveHealthResult health, string driveLabel)
    {
        return health.Status switch
        {
            DriveHealthStatus.Healthy => Lf("DriveHealth.OkMessage", "Storage health ({0}): OK ({1})", driveLabel, health.Message),
            DriveHealthStatus.Warning => Lf("DriveHealth.WarningMessage", "Drive health warning on {0}: {1}.", driveLabel, health.Message),
            DriveHealthStatus.Failing => Lf("DriveHealth.FailingMessage", "Storage health failing ({0}): {1}", driveLabel, health.Message),
            _ => Lf("DriveHealth.GenericMessage", "Storage health ({0}): {1}", driveLabel, health.Message)
        };
    }

    private static async Task RecheckDriveHealthAsync(IClassicDesktopStyleApplicationLifetime? desktop)
    {
        await Task.Run(() =>
        {
            try
            {
                var cfg        = AppViewModelInstance?.GetConfigSnapshot() ?? AppConfigStore.Load();
                var backupRoot = cfg.Backups.BackupRoot ?? string.Empty;
                var driveLabel = FormatDriveLabel(backupRoot);

                if (string.IsNullOrWhiteSpace(backupRoot))
                {
                    GlobalNotificationCenter.Instance.Show(
                        L("Tray.Health.NoPathDetail", "Backup path not set. Set a backup location to check drive health."),
                        NotificationSeverity.Warning,
                        L("Tray.Health.Title", "Storage health"));
                    return;
                }

                var health = new DriveHealthService().CheckPath(backupRoot);
                _cachedDriveHealthLabel  = DescribeHealth(health, driveLabel);
                _cachedDriveHealthStatus = health.Status;
                _cachedDriveHealthIsNetwork = IsNetworkHealthResult(health);

                var severity = health.Status switch
                {
                    DriveHealthStatus.Failing => NotificationSeverity.Error,
                    DriveHealthStatus.Warning => NotificationSeverity.Warning,
                    _ => NotificationSeverity.Info
                };

                GlobalNotificationCenter.Instance.Show(_cachedDriveHealthLabel, severity, L("Tray.Health.Title", "Storage health"));

                _instance?.RefreshTrayMenu();
            }
            catch
            {
                GlobalNotificationCenter.Instance.Show(
                    L("Tray.Health.Error", "Unable to check drive health."),
                    NotificationSeverity.Warning,
                    L("Tray.Health.Title", "Storage health"));
            }
        });
    }

    private static bool IsNetworkHealthResult(DriveHealthResult health)
    {
        var id = health.DriveId ?? string.Empty;
        if (id.StartsWith("//", StringComparison.OrdinalIgnoreCase))
            return true;
        if (id.Contains("://", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!id.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase) && id.Contains(':'))
            return true;

        var path = health.Path ?? string.Empty;
        return path.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("nfs://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("//", StringComparison.OrdinalIgnoreCase);
    }
}
