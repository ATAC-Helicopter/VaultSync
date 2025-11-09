using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace VaultSync.UI.Services;

public sealed class UiEventBus
{

    public void Clear()
{
    Dispatcher.UIThread.Post(() => Lines.Clear());
}
    public ObservableCollection<string> Lines { get; } = new();

    void Add(string prefix, string msg)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Lines.Add($"{prefix} {msg}");
            if (Lines.Count > 2000) Lines.RemoveAt(0);
        });
    }

    public void Info(string msg)    => Add("•", msg);
    public void Success(string msg) => Add("✓", msg);
    public void Warn(string msg)    => Add("!", msg);
    public void Error(string msg)   => Add("✗", msg);
}