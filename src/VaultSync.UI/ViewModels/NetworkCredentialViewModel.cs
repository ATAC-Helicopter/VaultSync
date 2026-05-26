using VaultSync.UI.ViewModels;

namespace VaultSync.UI;

public class NetworkCredentialViewModel : ViewModelBase
{
    private string _name = string.Empty;
    public string Name { get => _name; set => SetField(ref _name, value); }

    private string _username = string.Empty;
    public string Username { get => _username; set => SetField(ref _username, value); }

    private string _domain = string.Empty;
    public string Domain { get => _domain; set => SetField(ref _domain, value); }

    private string _keyRef = string.Empty;
    public string KeyRef { get => _keyRef; set => SetField(ref _keyRef, value); }

    private bool _useKeychain = true;
    public bool UseKeychain { get => _useKeychain; set => SetField(ref _useKeychain, value); }

    private string _password = string.Empty;
    public string Password { get => _password; set => SetField(ref _password, value); }

    private bool _showPassword;
    public bool ShowPassword { get => _showPassword; set => SetField(ref _showPassword, value); }
}
