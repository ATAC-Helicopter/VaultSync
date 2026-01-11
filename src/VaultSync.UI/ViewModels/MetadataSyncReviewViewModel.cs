using System;
using System.Windows.Input;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public sealed class MetadataSyncReviewViewModel : ViewModelBase
{
    private readonly LocalizationService _localization;
    private bool _confirmed;

    public MetadataSyncReviewViewModel(LocalizationService localization, MetadataSyncPreview preview, string sourceLabel)
    {
        _localization = localization;
        Preview = preview;
        SourceLabel = sourceLabel;

        ConfirmCommand = new RelayCommand(_ =>
        {
            _confirmed = true;
            RequestClose?.Invoke();
        });

        CancelCommand = new RelayCommand(_ =>
        {
            _confirmed = false;
            RequestClose?.Invoke();
        });
    }

    public MetadataSyncPreview Preview { get; }
    public string SourceLabel { get; }
    public bool HasDeletes => Preview.DeletedBackups > 0;
    public bool Confirmed => _confirmed;

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action? RequestClose;

    public string WarningDeletesText =>
        string.Format(
            _localization.GetString("MetadataSync.Review.WarningDeletes"),
            Preview.DeletedBackups);
}
