using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace VaultSync.UI.ViewModels
{
    public partial class HistoryViewModel : ObservableObject
    {
        public ObservableCollection<string> Entries { get; } = new()
        {
            "2025-11-08 22:10  Sync OK  (12 files)",
            "2025-11-07 09:31  Sync OK  (3 files)"
        };
    }
}