using System;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI;

public class BackupDestinationViewModel : ViewModelBase
{
    private string _alias = string.Empty;
    public string Alias
    {
        get => _alias;
        set
        {
            if (SetField(ref _alias, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    private string _path = string.Empty;
    public string Path
    {
        get => _path;
        set
        {
            if (SetField(ref _path, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    private NetworkCredentialViewModel? _selectedCredential;
    public NetworkCredentialViewModel? SelectedCredential
    {
        get => _selectedCredential;
        set
        {
            if (SetField(ref _selectedCredential, value))
            {
                CredentialName = value?.Name ?? string.Empty;
                OnPropertyChanged(nameof(NeedsCredentialWarning));
            }
        }
    }

    private string _credentialName = string.Empty;
    public string CredentialName
    {
        get => _credentialName;
        set
        {
            if (SetField(ref _credentialName, value))
            {
                // Keep SelectedCredential in sync when only the name changes.
                if (SelectedCredential is null || !string.Equals(SelectedCredential.Name, value, StringComparison.OrdinalIgnoreCase))
                {
                    // Selection will be resolved via SettingsViewModel handler.
                }
            }
        }
    }

    // Used by the Settings UI ComboBox. When the Settings page is unloaded,
    // Avalonia may temporarily clear SelectedItem and push a null/empty value
    // back into the binding; ignore that so the destination keeps its selection.
    public string SelectedCredentialName
    {
        get => CredentialName ?? string.Empty;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            CredentialName = value;
        }
    }

    private bool _active = true;
    public bool Active { get => _active; set => SetField(ref _active, value); }

    private bool _autoMount;
    public bool AutoMount
    {
        get => _autoMount;
        set
        {
            if (SetField(ref _autoMount, value))
            {
                OnPropertyChanged(nameof(NeedsCredentialWarning));
            }
        }
    }

    private bool _autoUnmount;
    public bool AutoUnmount { get => _autoUnmount; set => SetField(ref _autoUnmount, value); }

    private bool _preMounted = true;
    public bool PreMounted
    {
        get => _preMounted;
        set
        {
            if (SetField(ref _preMounted, value))
            {
                OnPropertyChanged(nameof(NeedsCredentialWarning));
            }
        }
    }

    private bool _isOffsite;
    public bool IsOffsite { get => _isOffsite; set => SetField(ref _isOffsite, value); }

    public bool NeedsCredentialWarning =>
        AutoMount && !PreMounted && SelectedCredential is null;

    private bool _enableMetadataSync = true;
    public bool EnableMetadataSync { get => _enableMetadataSync; set => SetField(ref _enableMetadataSync, value); }

    private bool _autoImportMetadata = true;
    public bool AutoImportMetadata { get => _autoImportMetadata; set => SetField(ref _autoImportMetadata, value); }

    private bool _forceMetadataBackfill;
    public bool ForceMetadataBackfill { get => _forceMetadataBackfill; set => SetField(ref _forceMetadataBackfill, value); }

    private int _retryMaxAttempts = 1;
    public int RetryMaxAttempts
    {
        get => _retryMaxAttempts;
        set => SetField(ref _retryMaxAttempts, Math.Clamp(value, 1, 10));
    }

    private int _retryBackoffSeconds = 10;
    public int RetryBackoffSeconds
    {
        get => _retryBackoffSeconds;
        set => SetField(ref _retryBackoffSeconds, Math.Clamp(value, 1, 300));
    }

    private bool _enableCheckpointResume = true;
    public bool EnableCheckpointResume
    {
        get => _enableCheckpointResume;
        set => SetField(ref _enableCheckpointResume, value);
    }

    private double _softQuotaGb;
    public double SoftQuotaGb
    {
        get => _softQuotaGb;
        set => SetField(ref _softQuotaGb, Math.Clamp(value, 0d, 1024d * 1024d));
    }

    private int _quotaWarningPercent = 85;
    public int QuotaWarningPercent
    {
        get => _quotaWarningPercent;
        set => SetField(ref _quotaWarningPercent, Math.Clamp(value, 50, 99));
    }

    private string _lastTestStatus = string.Empty;
    public string LastTestStatus
    {
        get => _lastTestStatus;
        set => SetField(ref _lastTestStatus, value);
    }

    private string _lastTestSeverity = "Info";
    public string LastTestSeverity
    {
        get => _lastTestSeverity;
        set => SetField(ref _lastTestSeverity, value);
    }

    private string _repositoryWriterStatus = "Not checked";
    public string RepositoryWriterStatus
    {
        get => _repositoryWriterStatus;
        set => SetField(ref _repositoryWriterStatus, value);
    }

    private string _repositoryWriterDetails = "Check before using this destination from more than one machine.";
    public string RepositoryWriterDetails
    {
        get => _repositoryWriterDetails;
        set => SetField(ref _repositoryWriterDetails, value);
    }

    private bool _isRepositoryWriterBusy;
    public bool IsRepositoryWriterBusy
    {
        get => _isRepositoryWriterBusy;
        set => SetField(ref _isRepositoryWriterBusy, value);
    }

    private bool _canReviewStaleWriter;
    public bool CanReviewStaleWriter
    {
        get => _canReviewStaleWriter;
        set => SetField(ref _canReviewStaleWriter, value);
    }

    private bool _isWriterTakeoverConfirmationVisible;
    public bool IsWriterTakeoverConfirmationVisible
    {
        get => _isWriterTakeoverConfirmationVisible;
        set => SetField(ref _isWriterTakeoverConfirmationVisible, value);
    }

    internal string InspectedRepositoryRoot { get; set; } = string.Empty;
    internal string StaleWriterNonce { get; set; } = string.Empty;

    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Path : Alias;
}
