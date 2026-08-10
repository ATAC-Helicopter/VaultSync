using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VaultSync.UI.Infrastructure;

internal static class ObservableCollectionExtensions
{
    /// <summary>
    /// Reconciles a bound collection without raising a Reset notification. Existing items are
    /// moved into place where possible, which preserves selection, scroll position, and realized
    /// controls during background refreshes.
    /// </summary>
    public static void SyncWith<T>(this ObservableCollection<T> collection, IReadOnlyList<T> items)
    {
        for (int targetIndex = 0; targetIndex < items.Count; targetIndex++)
        {
            T item = items[targetIndex];
            if (targetIndex < collection.Count && EqualityComparer<T>.Default.Equals(collection[targetIndex], item))
                continue;

            int existingIndex = collection.IndexOf(item);
            if (existingIndex >= 0)
            {
                collection.Move(existingIndex, targetIndex);
            }
            else if (targetIndex < collection.Count)
            {
                collection[targetIndex] = item;
            }
            else
            {
                collection.Add(item);
            }
        }

        while (collection.Count > items.Count)
            collection.RemoveAt(collection.Count - 1);
    }
}
