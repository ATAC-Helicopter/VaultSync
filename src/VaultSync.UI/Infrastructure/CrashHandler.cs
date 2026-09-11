using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.UI.Services;

namespace VaultSync.UI.Infrastructure;

internal static class CrashHandler
{
    private static int _handling;
    private static int _softHandling;

    public static void RegisterEarly()
    {
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public static void RegisterAvalonia()
    {
        Dispatcher.UIThread.UnhandledException += OnUiUnhandledException;
    }

    private static void OnUiUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticsLogger.Record($"UI unhandled exception: {e.Exception.GetType().Name}");
        HandleUiExceptionSoft(e.Exception);
        e.Handled = true;
    }

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        Exception ex = e.ExceptionObject as Exception
            ?? new Exception("Unhandled exception (non-Exception object).");
        DiagnosticsLogger.Record($"AppDomain unhandled exception: {ex.GetType().Name}");
        HandleException(ex, "AppDomain", e.IsTerminating);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (ExpectedDesktopNoise.IsExpectedUnobservedTaskException(e.Exception))
        {
            e.SetObserved();
            return;
        }

        DiagnosticsLogger.Record($"Unobserved task exception: {e.Exception.GetType().Name}");
        WriteCrashLog(e.Exception, "UnobservedTaskException", isTerminating: false);
        e.SetObserved();
    }

    private static void HandleException(Exception ex, string source, bool isTerminating)
    {
        if (Interlocked.Exchange(ref _handling, 1) != 0)
        {
            return;
        }

        DiagnosticsLogger.Record($"Crash handler invoked: source={source}, terminating={isTerminating}, error={ex.GetType().Name}");
        App.MarkCrashing();
        CrashArtifact? crash = WriteCrashLog(ex, source, isTerminating);
        TryShowCrashDialog(crash);
    }

    private static void HandleUiExceptionSoft(Exception ex)
    {
        if (Interlocked.Exchange(ref _softHandling, 1) != 0)
        {
            return;
        }

        try
        {
            DiagnosticsLogger.Record($"Soft UI crash handled: {ex.GetType().Name}");
            CrashArtifact? crash = WriteCrashLog(ex, "UI thread", isTerminating: false);
            TryShowSoftCrashBanner(crash?.Path);
        }
        finally
        {
            Interlocked.Exchange(ref _softHandling, 0);
        }
    }

    private static void TryShowSoftCrashBanner(string? logPath)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ShowSoftCrashBanner(logPath);
            return;
        }

        Dispatcher.UIThread.Post(() => ShowSoftCrashBanner(logPath), DispatcherPriority.Send);
    }

    private static void ShowSoftCrashBanner(string? logPath)
    {
        App.AppViewModelInstance?.NotifySoftCrashBanner(logPath);
    }

    private static CrashArtifact? WriteCrashLog(Exception ex, string source, bool isTerminating)
    {
        if (!IsCrashReportAssistanceEnabled())
            return null;

        return CreateCrashArtifact(ex, source, isTerminating);
    }

    private static CrashArtifact? CreateCrashArtifact(Exception ex, string source, bool isTerminating)
    {
        try
        {
            CrashReportDocument report = ShareableCrashReport.Create(
                ex,
                source,
                isTerminating,
                GetAppVersion());
            string path = ShareableCrashReport.Save(report);
            return new CrashArtifact(report, path);
        }
        catch
        {
            return null;
        }
    }

    private static void TryShowCrashDialog(CrashArtifact? crash)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ShowCrashDialog(crash);
            return;
        }

        Dispatcher.UIThread.Post(() => ShowCrashDialog(crash), DispatcherPriority.Send);
    }

    private static void ShowCrashDialog(CrashArtifact? crash)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            Environment.Exit(1);
            return;
        }

        bool assistanceEnabled = IsCrashReportAssistanceEnabled();
        StackPanel content = BuildCrashContent(crash, assistanceEnabled, out TextBox? reportPreview, out TextBlock? reportStatus);
        Window window = CreateCrashWindow(desktop, assistanceEnabled && crash is not null);
        AddCrashActions(content, desktop, window, crash, reportPreview, reportStatus);
        window.Content = BuildCrashWindowRoot(CreateCrashHeader(desktop, window), content);
        window.Closed += (_, _) => ShutdownAfterCrash();

        if (desktop.MainWindow is not null)
            _ = window.ShowDialog(desktop.MainWindow);
        else
            window.Show();
    }

    private static StackPanel BuildCrashContent(
        CrashArtifact? crash,
        bool assistanceEnabled,
        out TextBox? reportPreview,
        out TextBlock? reportStatus)
    {
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(CreateCrashHero());
        reportPreview = null;
        reportStatus = null;
        if (crash is not null)
            AddCrashReportDetails(content, crash, assistanceEnabled, out reportPreview, out reportStatus);
        return content;
    }

    private static Grid CreateCrashHero()
    {
        var title = new TextBlock
        {
            Text = L("Crash.Title", "VaultSync crashed"),
            FontSize = 24,
            FontWeight = FontWeight.SemiBold
        };
        title.Classes.Add("section-title");

        var message = new TextBlock
        {
            Text = L("Crash.Message", "VaultSync hit an unexpected error and must close."),
            TextWrapping = TextWrapping.Wrap
        };
        if (GetBrush("TextSecondary") is { } messageBrush)
            message.Foreground = messageBrush;

        var heroIcon = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(16),
            Background = GetBrush("AccentSoftBrush"),
            Child = new TextBlock
            {
                Text = "!",
                FontSize = 26,
                FontWeight = FontWeight.Bold,
                Foreground = GetBrush("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var heroText = new StackPanel { Spacing = 4 };
        heroText.Children.Add(title);
        heroText.Children.Add(message);
        var hero = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
        hero.Children.Add(heroIcon);
        hero.Children.Add(heroText);
        Grid.SetColumn(heroText, 1);
        return hero;
    }

    private static void AddCrashReportDetails(
        StackPanel content,
        CrashArtifact crash,
        bool assistanceEnabled,
        out TextBox? reportPreview,
        out TextBlock? reportStatus)
    {
        content.Children.Add(new TextBlock
        {
            Text = assistanceEnabled
                ? L("Crash.PrivacySummary", "Review the complete redacted report below. Nothing is sent until you press Send in your email app.")
                : L("Crash.AssistanceDisabled", "Crash report assistance is disabled. The redacted report remains only on this device."),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.Medium
        });
        reportPreview = null;
        reportStatus = null;
        if (!assistanceEnabled)
            return;

        var identityPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 8
        };
        identityPanel.Children.Add(CreateReportChip(crash.Document.OperatingSystemFamily));
        identityPanel.Children.Add(CreateReportChip(crash.Document.CrashCategory));
        identityPanel.Children.Add(CreateReportChip(crash.Document.CrashReason));

        var identityContent = new StackPanel { Spacing = 8 };
        identityContent.Children.Add(new TextBlock
        {
            Text = crash.Document.ReportId,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        identityContent.Children.Add(identityPanel);
        content.Children.Add(new Border
        {
            Padding = new Thickness(14, 12),
            CornerRadius = new CornerRadius(12),
            Background = GetBrush("Surface2"),
            BorderBrush = GetBrush("BorderSoft"),
            BorderThickness = new Thickness(1),
            Child = identityContent
        });

        string attachmentName = Path.GetFileName(crash.Path) ?? "vaultsync-crash-report.txt";
        var attachmentTitle = new TextBlock
        {
            Text = attachmentName,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var attachmentBadge = new Border
        {
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(8),
            Background = GetBrush("AccentSoftBrush"),
            Child = new TextBlock
            {
                Text = "TXT",
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = GetBrush("AccentBrush")
            }
        };
        var attachmentHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 10
        };
        attachmentHeader.Children.Add(attachmentBadge);
        attachmentHeader.Children.Add(attachmentTitle);
        Grid.SetColumn(attachmentTitle, 1);
        content.Children.Add(attachmentHeader);

        reportPreview = new TextBox
        {
            Text = crash.Document.Content,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Menlo, Consolas, monospace"),
            FontSize = 12.5,
            CaretIndex = 0,
            MinWidth = 560,
            MinHeight = 220,
            MaxHeight = 290,
            Background = GetBrush("InputBackgroundBrush"),
            BorderBrush = GetBrush("InputBorderBrush")
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(reportPreview, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(reportPreview, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        content.Children.Add(reportPreview);

        reportStatus = new TextBlock
        {
            Text = L("Crash.PreviewHint", "VaultSync locks the report ID, OS family, crash category, and crash reason. Add any optional context in the email draft."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        if (GetBrush("TextSecondary") is { } statusBrush)
            reportStatus.Foreground = statusBrush;
        content.Children.Add(new Border
        {
            Padding = new Thickness(12, 9),
            CornerRadius = new CornerRadius(10),
            Background = GetBrush("Surface2"),
            Child = reportStatus
        });
    }

    private static Border CreateCrashHeader(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window window)
    {
        var headerTitle = new TextBlock
        {
            Text = L("Crash.HeaderTitle", "VaultSync"),
            FontWeight = FontWeight.SemiBold,
            Foreground = GetBrush("TextPrimary")
        };
        var headerSubTitle = new TextBlock
        {
            Text = L("Crash.HeaderSubtitle", "Crash report"),
            FontSize = 12,
            Foreground = GetBrush("TextSecondary")
        };
        var headerText = new StackPanel { Spacing = 2 };
        headerText.Children.Add(headerTitle);
        headerText.Children.Add(headerSubTitle);
        var headerClose = new Button
        {
            Content = "×",
            FontSize = 22,
            MinWidth = 40,
            MinHeight = 36,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        headerClose.Classes.Add("action-ghost");
        headerClose.Click += (_, _) => desktop.Shutdown(1);
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            VerticalAlignment = VerticalAlignment.Center
        };
        headerGrid.Children.Add(headerText);
        headerGrid.Children.Add(headerClose);
        Grid.SetColumn(headerClose, 1);
        var header = new Border
        {
            Padding = new Thickness(16, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = GetBrush("Surface2"),
            BorderBrush = GetBrush("BorderSoft"),
            Child = headerGrid
        };
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
                window.BeginMoveDrag(e);
        };
        return header;
    }

    private static Grid BuildCrashWindowRoot(Border header, StackPanel content)
    {
        var card = new Border
        {
            Padding = new Thickness(24), Margin = new Thickness(20), Child = content
        };
        card.Classes.Add("card");
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(header);
        root.Children.Add(card);
        Grid.SetRow(card, 1);
        return root;
    }

    private static Window CreateCrashWindow(
        IClassicDesktopStyleApplicationLifetime desktop,
        bool showReport)
    {
        return new Window
        {
            Title = L("Crash.Title", "VaultSync crashed"),
            CanResize = true,
            Width = 900,
            Height = showReport ? 760 : 440,
            MinWidth = 700,
            MinHeight = 400,
            WindowDecorations = WindowDecorations.None,
            ExtendClientAreaToDecorationsHint = true,
            WindowStartupLocation = desktop.MainWindow is not null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            Background = GetBrush("WindowBackground"),
            Icon = desktop.MainWindow?.Icon
        };
    }

    private static void AddCrashActions(
        StackPanel content,
        IClassicDesktopStyleApplicationLifetime desktop,
        Window window,
        CrashArtifact? crash,
        TextBox? reportPreview,
        TextBlock? reportStatus)
    {
        var buttonRow = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 8
        };
        if (crash is not null && reportPreview is not null && reportStatus is not null)
            AddCrashReportActions(buttonRow, window, crash, reportPreview, reportStatus);
        var closeButton = new Button
        {
            Content = L("Crash.Close", "Close"),
            MinWidth = 90
        };
        closeButton.Click += (_, _) => desktop.Shutdown(1);
        buttonRow.Children.Add(closeButton);
        content.Children.Add(buttonRow);
    }

    private static void AddCrashReportActions(
        WrapPanel buttonRow,
        Window window,
        CrashArtifact crash,
        TextBox reportPreview,
        TextBlock reportStatus)
    {
        string? logPath = crash.Path;
        var copyButton = new Button { Content = L("Crash.CopyReport", "Copy report") };
        copyButton.Click += async (_, _) => await TryCopyToClipboardAsync(
            TopLevel.GetTopLevel(window), reportPreview.Text ?? string.Empty);
        var openFolderButton = new Button { Content = L("Crash.OpenFolder", "Open report folder") };
        openFolderButton.Click += (_, _) =>
        {
            logPath = ShareableCrashReport.Save(crash.Document);
            OpenLogFolder(logPath);
        };
        var deleteButton = new Button { Content = L("Crash.DeleteReport", "Delete report") };
        deleteButton.Click += (_, _) =>
        {
            if (ShareableCrashReport.DeleteSavedReport(logPath))
                reportStatus.Text = L("Crash.ReportDeleted", "The saved report was deleted. Nothing was sent.");
        };
        var prepareEmailButton = new Button
        {
            Content = L("Crash.PrepareEmail", "Prepare email"), MinWidth = 120
        };
        prepareEmailButton.Classes.Add("action-primary");
        prepareEmailButton.Click += async (_, _) =>
        {
            prepareEmailButton.IsEnabled = false;
            try
            {
                logPath = ShareableCrashReport.Save(crash.Document);
                bool prepared = await CrashReportEmailDraft.PrepareAsync(crash.Document, logPath);
                reportStatus.Text = prepared
                    ? L("Crash.EmailPrepared", "An email draft opened with the redacted report attached. Review it, add optional context, then press Send yourself.")
                    : L("Crash.EmailPrepareFailed", "VaultSync could not create an email draft with an attachment. The report folder is open so you can attach it manually.");
                if (!prepared)
                    OpenLogFolder(logPath);
            }
            catch
            {
                if (logPath is not null)
                    OpenLogFolder(logPath);
                reportStatus.Text = L("Crash.EmailPrepareFailed", "VaultSync could not create an email draft with an attachment. The report folder is open so you can attach it manually.");
            }
            finally
            {
                prepareEmailButton.IsEnabled = true;
            }
        };
        buttonRow.Children.Add(openFolderButton);
        buttonRow.Children.Add(copyButton);
        buttonRow.Children.Add(deleteButton);
        buttonRow.Children.Add(prepareEmailButton);
    }

    private static void ShutdownAfterCrash()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.Shutdown(1);
        else
            Environment.Exit(1);
    }

    private static async Task TryCopyToClipboardAsync(TopLevel? topLevel, string text)
    {
        try
        {
            IClipboard? clipboard = topLevel?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(text);
            }
        }
        catch
        {
            // Best effort: ignore clipboard failures.
        }
    }

    private static Border CreateReportChip(string text) => new()
    {
        Padding = new Thickness(9, 4),
        CornerRadius = new CornerRadius(9),
        Background = GetBrush("Surface3"),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = GetBrush("TextSecondaryBrush")
        }
    };

    private static IBrush? GetBrush(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out object? value) is true)
        {
            return value as IBrush;
        }

        return null;
    }

    private static string GetAppVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly();
            AssemblyName? name = asm?.GetName();
            return name?.Version is null
                ? "unknown"
                : $"{name.Name} {name.Version}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool IsCrashReportAssistanceEnabled()
    {
        try
        {
            return StaticAppConfigStore.Instance.Load().Advanced.CrashReportAssistanceEnabled;
        }
        catch
        {
            // If preferences cannot be read during a crash, do not expose a sharing action.
            return false;
        }
    }

    private static void OpenLogFolder(string logPath)
    {
        try
        {
            string? folder = Path.GetDirectoryName(logPath);
            if (string.IsNullOrWhiteSpace(folder))
                return;

            SystemFileLauncher.OpenPath(folder);
        }
        catch
        {
            // Best effort: ignore failures.
        }
    }

    private sealed record CrashArtifact(CrashReportDocument Document, string Path);
}
