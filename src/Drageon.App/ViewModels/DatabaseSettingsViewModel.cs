using Drageon.Core;
using Drageon.Infrastructure;
using System.IO;

namespace Drageon.App.ViewModels;

public sealed class DatabaseSettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly string _componentId;
    private readonly string _version;
    private string _databaseUser = "root", _newPassword = "", _port = "3306", _characterSet = "utf8mb4", _maxConnections = "100", _bufferPoolSize = "64M", _customConfiguration = "", _status = "";
    private string _existingProtectedPassword = "";
    private bool _clearStoredPassword;

    public DatabaseSettingsViewModel(AppServices services, string componentId, string version)
    {
        _services = services; _componentId = componentId; _version = version;
        SaveCommand = new(_ => SaveAsync());
        _ = LoadAsync();
    }

    public string Title => _componentId.Equals("mysql", StringComparison.OrdinalIgnoreCase) ? "MySQL" : "MariaDB";
    public string VersionLabel => TF("component.versionFormat", _version);
    public string DatabaseUser { get => _databaseUser; set => Set(ref _databaseUser, value); }
    public string NewPassword { get => _newPassword; set => Set(ref _newPassword, value); }
    public bool ClearStoredPassword { get => _clearStoredPassword; set => Set(ref _clearStoredPassword, value); }
    public bool HasStoredPassword => !string.IsNullOrWhiteSpace(_existingProtectedPassword);
    public string StoredPasswordStatus => HasStoredPassword ? T("database.settings.passwordStored") : T("database.settings.noPasswordStored");
    public string Port { get => _port; set => Set(ref _port, value); }
    public string CharacterSet { get => _characterSet; set => Set(ref _characterSet, value); }
    public string MaxConnections { get => _maxConnections; set => Set(ref _maxConnections, value); }
    public string BufferPoolSize { get => _bufferPoolSize; set => Set(ref _bufferPoolSize, value); }
    public string CustomConfiguration { get => _customConfiguration; set => Set(ref _customConfiguration, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public AsyncRelayCommand SaveCommand { get; }

    private async Task LoadAsync()
    {
        try
        {
            if (_componentId == "mysql")
            {
                var settings = await VersionedComponentConfiguration.LoadAsync(_services.Paths, _services.Store, _componentId, _version, static () => new MySqlSettings());
                Apply(settings.Port, settings.CharacterSet, settings.MaxConnections, settings.BufferPoolSize, settings.CustomConfiguration, settings.DatabaseUser, settings.ProtectedPassword);
            }
            else
            {
                var settings = await VersionedComponentConfiguration.LoadAsync(_services.Paths, _services.Store, _componentId, _version, static () => new MariaDbSettings());
                Apply(settings.Port, settings.CharacterSet, settings.MaxConnections, settings.BufferPoolSize, settings.CustomConfiguration, settings.DatabaseUser, settings.ProtectedPassword);
            }
            Status = T("database.settings.ready");
        }
        catch (Exception exception) { Status = exception.Message; }
    }

    private void Apply(int port, string characterSet, int maxConnections, string bufferPoolSize, string customConfiguration, string user, string protectedPassword)
    {
        Port = port.ToString(System.Globalization.CultureInfo.InvariantCulture); CharacterSet = characterSet;
        MaxConnections = maxConnections.ToString(System.Globalization.CultureInfo.InvariantCulture); BufferPoolSize = bufferPoolSize;
        CustomConfiguration = customConfiguration; DatabaseUser = string.IsNullOrWhiteSpace(user) ? "root" : user;
        _existingProtectedPassword = protectedPassword; Raise(nameof(HasStoredPassword)); Raise(nameof(StoredPasswordStatus));
    }

    private async Task SaveAsync()
    {
        try
        {
            if (!int.TryParse(Port, out var port) || port is < 1 or > 65535 || !int.TryParse(MaxConnections, out var maximum) || maximum < 1 || string.IsNullOrWhiteSpace(DatabaseUser))
                throw new InvalidDataException(T("database.settings.invalid"));
            var protectedPassword = ClearStoredPassword ? "" : string.IsNullOrEmpty(NewPassword)
                ? MigrateLegacyPassword(_existingProtectedPassword)
                : DatabaseCredentialProtector.Protect(NewPassword, _services.Paths);
            if (_componentId == "mysql")
                await _services.Store.WriteAsync(VersionedComponentConfiguration.GetSettingsPath(_services.Paths, _componentId, _version), new MySqlSettings { Port = port, CharacterSet = CharacterSet.Trim(), MaxConnections = maximum, BufferPoolSize = BufferPoolSize.Trim(), CustomConfiguration = CustomConfiguration, DatabaseUser = DatabaseUser.Trim(), ProtectedPassword = protectedPassword });
            else
                await _services.Store.WriteAsync(VersionedComponentConfiguration.GetSettingsPath(_services.Paths, _componentId, _version), new MariaDbSettings { Port = port, CharacterSet = CharacterSet.Trim(), MaxConnections = maximum, BufferPoolSize = BufferPoolSize.Trim(), CustomConfiguration = CustomConfiguration, DatabaseUser = DatabaseUser.Trim(), ProtectedPassword = protectedPassword });
            _existingProtectedPassword = protectedPassword; NewPassword = ""; ClearStoredPassword = false;
            Raise(nameof(HasStoredPassword)); Raise(nameof(StoredPasswordStatus));
            Status = T("database.settings.saved");
        }
        catch (Exception exception) { Status = exception.Message; _services.Logs.Write(Title, exception.Message, LogSeverity.Error); }
    }

    private string MigrateLegacyPassword(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || DatabaseCredentialProtector.IsPortable(value)) return value;
        var password = DatabaseCredentialProtector.Unprotect(value, _services.Paths);
        return string.IsNullOrEmpty(password) ? "" : DatabaseCredentialProtector.Protect(password, _services.Paths);
    }
}
