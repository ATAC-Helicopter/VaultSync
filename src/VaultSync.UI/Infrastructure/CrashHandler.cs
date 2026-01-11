using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VaultSync.UI.Services;

namespace VaultSync.UI.Infrastructure;

internal static class CrashHandler
{
    private static int _handling;

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
        HandleException(e.Exception, "UI thread", isTerminating: false);
        e.Handled = true;
    }

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    private static void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception
            ?? new Exception("Unhandled exception (non-Exception object).");
        HandleException(ex, "AppDomain", e.IsTerminating);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception, "UnobservedTaskException", isTerminating: false);
        e.SetObserved();
    }

    private static void HandleException(Exception ex, string source, bool isTerminating)
    {
        if (Interlocked.Exchange(ref _handling, 1) != 0)
        {
            return;
        }

        App.MarkCrashing();
        var logPath = WriteCrashLog(ex, source, isTerminating);
        TryShowCrashDialog(logPath);
    }

    private static string? WriteCrashLog(Exception ex, string source, bool isTerminating)
    {
        try
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VaultSync",
                "crash");

            Directory.CreateDirectory(baseDir);

            var fileName = $"crash-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log";
            var path = Path.Combine(baseDir, fileName);

            var sb = new StringBuilder();
            sb.AppendLine("VaultSync crash report");
            sb.AppendLine($"UTC: {DateTimeOffset.UtcNow:O}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"Terminating: {isTerminating}");
            sb.AppendLine($"App: {GetAppVersion()}");
            sb.AppendLine($".NET: {Environment.Version}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($"Process: {Environment.ProcessId}");
            sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
            sb.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
            sb.AppendLine($"Culture: {CultureInfo.CurrentCulture.Name} / {CultureInfo.CurrentUICulture.Name}");
            sb.AppendLine($"CommandLine: {Environment.CommandLine}");
            sb.AppendLine($"CurrentDirectory: {Environment.CurrentDirectory}");
            sb.AppendLine();
            sb.AppendLine(ex.ToString());

            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void TryShowCrashDialog(string? logPath)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ShowCrashDialog(logPath);
            return;
        }

        Dispatcher.UIThread.Post(() => ShowCrashDialog(logPath), DispatcherPriority.Send);
    }

    private static void ShowCrashDialog(string? logPath)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            Environment.Exit(1);
            return;
        }

        var title = new TextBlock
        {
            Text = "VaultSync crashed",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold
        };
        title.Classes.Add("section-title");

        var message = new TextBlock
        {
            Text = "VaultSync hit an unexpected error and must close.",
            TextWrapping = TextWrapping.Wrap
        };
        if (GetBrush("TextSecondary") is { } messageBrush)
        {
            message.Foreground = messageBrush;
        }


        var content = new StackPanel
        {
            Spacing = 12
        };

        content.Children.Add(title);
        content.Children.Add(message);

        TextBox? pathBox = null;
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            content.Children.Add(new TextBlock
            {
                Text = "Crash log path:",
                FontWeight = FontWeight.Medium
            });

            pathBox = new TextBox
            {
                Text = logPath,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                MinWidth = 420
            };
            content.Children.Add(pathBox);
        }

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var headerTitle = new TextBlock
        {
            Text = "VaultSync",
            FontWeight = FontWeight.SemiBold
        };
        if (GetBrush("TextPrimary") is { } headerBrush)
        {
            headerTitle.Foreground = headerBrush;
        }

        var headerSubTitle = new TextBlock
        {
            Text = "Crash report",
            FontSize = 12
        };
        if (GetBrush("TextSecondary") is { } headerSubBrush)
        {
            headerSubTitle.Foreground = headerSubBrush;
        }

        var headerText = new StackPanel
        {
            Spacing = 2
        };
        headerText.Children.Add(headerTitle);
        headerText.Children.Add(headerSubTitle);

        var headerClose = new Button
        {
            Content = "X",
            MinWidth = 36,
            MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Right
        };
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
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        if (GetBrush("Surface2") is { } headerBackground)
        {
            header.Background = headerBackground;
        }
        if (GetBrush("BorderSoft") is { } headerBorder)
        {
            header.BorderBrush = headerBorder;
        }
        header.Child = headerGrid;

        Window? window = null;
        header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            {
                window?.BeginMoveDrag(e);
            }
        };

        var card = new Border
        {
            Padding = new Thickness(20),
            Margin = new Thickness(16)
        };
        card.Classes.Add("card");
        card.Child = content;

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        root.Children.Add(header);
        root.Children.Add(card);
        Grid.SetRow(card, 1);

        window = new Window
        {
            Title = L("Crash.Title", "VaultSync crashed"),
            Content = root,
            CanResize = false,
            Width = 720,
            SizeToContent = SizeToContent.Height,
            SystemDecorations = SystemDecorations.None,
            ExtendClientAreaToDecorationsHint = true,
            WindowStartupLocation = desktop.MainWindow != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen
        };
        if (GetBrush("WindowBackground") is { } backgroundBrush)
        {
            window.Background = backgroundBrush;
        }
        window.Icon = desktop.MainWindow?.Icon;

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            var copyButton = new Button
            {
                Content = L("Crash.CopyLogPath", "Copy log path")
            };
            copyButton.Click += async (_, _) =>
            {
                await TryCopyToClipboardAsync(
                    TopLevel.GetTopLevel(window),
                    logPath ?? string.Empty);
            };

            var openFolderButton = new Button
            {
                Content = L("Crash.OpenFolder", "Open folder")
            };
            openFolderButton.Click += (_, _) =>
            {
                if (logPath is not null)
                {
                    OpenLogFolder(logPath);
                }
            };

            buttonRow.Children.Add(openFolderButton);
            buttonRow.Children.Add(copyButton);
        }

        var closeButton = new Button
        {
            Content = L("Crash.Close", "Close"),
            MinWidth = 90
        };
        closeButton.Click += (_, _) => desktop.Shutdown(1);
        buttonRow.Children.Add(closeButton);

        content.Children.Add(buttonRow);

        window.Closed += (_, _) =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.Shutdown(1);
            }
            else
            {
                Environment.Exit(1);
            }
        };

        if (desktop.MainWindow != null)
        {
            _ = window.ShowDialog(desktop.MainWindow);
        }
        else
        {
            window.Show();
        }
    }

    private static async Task TryCopyToClipboardAsync(TopLevel? topLevel, string text)
    {
        try
        {
            var clipboard = topLevel?.Clipboard;
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

    private static IBrush? GetBrush(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true)
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
            var name = asm?.GetName();
            return name?.Version is null
                ? "unknown"
                : $"{name.Name} {name.Version}";
        }
        catch
        {
            return "unknown";
        }
    }

    private static void OpenLogFolder(string logPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(logPath);
            if (string.IsNullOrWhiteSpace(folder))
                return;

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "open",
                    UseShellExecute = false
                };
                psi.ArgumentList.Add(folder);
                Process.Start(psi);
            }
            else if (OperatingSystem.IsLinux())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    UseShellExecute = false
                };
                psi.ArgumentList.Add(folder);
                Process.Start(psi);
            }
            else
            {
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
        }
        catch
        {
            // Best effort: ignore failures.
        }
    }
}
