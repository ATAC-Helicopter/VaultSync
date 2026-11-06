using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using VaultSync.Core.Services;
using VaultSync.UI.Services;

namespace VaultSync.UI.Infrastructure;

internal static class ProductDiagnosticWindows
{
    public static void ShowConsent(Window? owner)
    {
        if (owner is null || ProductDiagnostics.HasDecision) return;
        var text = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var accept = new Button();
        var decline = new Button();
        var review = new Button();
        var notice = new Button();
        var privacy = new SelectableTextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var window = new Window { Width = 620, Height = 480, MinWidth = 400, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24), Spacing = 18,
            Children = { text, privacy, notice, review, new WrapPanel { ItemSpacing = 12, LineSpacing = 8, Children = { accept, decline } }, status } } };
        bool saveFailed = false;
        void Refresh()
        {
            status.Text = saveFailed ? T("Diagnostics.SaveFailed") : string.Empty;
            window.Title = T("Diagnostics.Title");
            text.Text = T(ProductDiagnostics.IsBeta ? "Diagnostics.BetaDescription" : "Diagnostics.Description");
            privacy.Text = T("Diagnostics.Privacy");
            accept.Content = T(ProductDiagnostics.IsBeta && ProductDiagnostics.Enabled ? "Common.Ok" : "Diagnostics.Accept");
            decline.Content = T("Diagnostics.Decline");
            review.Content = T("Diagnostics.Review");
            notice.Content = T("Diagnostics.PrivacyLink");
        }
        Refresh();
        var language = LocalizationProvider.Service;
        if (language is not null) language.LanguageChanged += Refresh;
        window.Closed += (_, _) => { if (language is not null) language.LanguageChanged -= Refresh; };
        accept.Click += (_, _) =>
        {
            if (ProductDiagnostics.SetConsent(true)) window.Close();
            else { saveFailed = true; Refresh(); }
        };
        decline.Click += (_, _) =>
        {
            if (ProductDiagnostics.SetConsent(false)) window.Close();
            else { saveFailed = true; Refresh(); }
        };
        review.Click += (_, _) => ShowReview(window);
        notice.Click += (_, _) => OpenPrivacy();
        window.Show(owner);
    }

    public static void ShowReview(Window? owner)
    {
        var window = new Window { Width = 760, Height = 600, MinWidth = 400, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var text = new TextBox { Text = ProductDiagnostics.Review(), IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap, Margin = new Thickness(12),
            [ScrollViewer.HorizontalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            [ScrollViewer.VerticalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var delete = new Button { Margin = new Thickness(12) };
        var panel = new DockPanel();
        DockPanel.SetDock(delete, Dock.Bottom);
        panel.Children.Add(delete); panel.Children.Add(text); window.Content = panel;
        void Refresh() { window.Title = T("Diagnostics.Review"); delete.Content = T("Diagnostics.Delete"); }
        Refresh();
        var language = LocalizationProvider.Service;
        if (language is not null) language.LanguageChanged += Refresh;
        window.Closed += (_, _) => { if (language is not null) language.LanguageChanged -= Refresh; };
        delete.Click += (_, _) => { ProductDiagnostics.DeleteReports(); text.Text = ProductDiagnostics.Review(); };
        if (owner is null) window.Show(); else window.Show(owner);
    }

    public static void OpenPrivacy()
    {
        try { SystemFileLauncher.OpenUri("https://fglabs.dev/privacy"); }
        catch { /* The notice URL remains selectable in the disclosure. */ }
    }

    private static string T(string key) => LocalizationProvider.Service?.GetString(key) ?? key;
}
