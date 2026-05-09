using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public sealed class WhatsNewViewModel : ViewModelBase
{
    public string Title { get; }
    public string VersionLabel { get; }
    public ObservableCollection<WhatsNewSection> Sections { get; } = [];

    public ICommand CloseCommand { get; }

    public event Action? CloseRequested;

    public WhatsNewViewModel(string versionLabel)
    {
        Title = L("WhatsNew.Title", "What's new");
        VersionLabel = versionLabel;
        CloseCommand = new RelayCommand(_ => CloseRequested?.Invoke());
    }

    public void AddSection(string title, params string[] items)
    {
        if (string.IsNullOrWhiteSpace(title) || items.Length == 0)
            return;

        var section = new WhatsNewSection(title);
        foreach (string item in items)
        {
            if (!string.IsNullOrWhiteSpace(item))
                section.Items.Add(item.Trim());
        }
        if (section.Items.Count > 0)
            Sections.Add(section);
    }

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;
}

public sealed class WhatsNewSection
{
    public string Title { get; }
    public ObservableCollection<string> Items { get; } = [];

    public WhatsNewSection(string title)
    {
        Title = title;
    }
}
