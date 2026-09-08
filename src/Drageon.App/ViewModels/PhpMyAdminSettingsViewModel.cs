using Drageon.Core;
using Drageon.Infrastructure;

namespace Drageon.App.ViewModels;

public sealed class PhpMyAdminSettingsViewModel : ViewModelBase, IRefreshable
{
    private readonly AppServices _services;
    private readonly string? _requestedVersion;
    private string? _configuredVersion;
    private string _authType = "cookie";
    private string _databaseHost = "127.0.0.1";
    private string _databasePort = "3306";
    private bool _allowNoPassword = true;
    private string _loginCookieValidity = "1440";
    private string _maxRows = "50";
    private bool _showServerInfo = true;
    private string _customConfiguration = "";
    private string _blowfishSecret = "";
    private string _status = "";

    public PhpMyAdminSettingsViewModel(AppServices services, string? version = null)
    {
        _services = services;
        _requestedVersion = version;
        AuthTypes = [new("cookie", T("phpmyadmin.auth.cookie")), new("http", T("phpmyadmin.auth.http"))];
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
        _ = RefreshAsync();
    }

    public IReadOnlyList<AuthTypeOption> AuthTypes { get; }
    public string AuthType { get => _authType; set => Set(ref _authType, value); }
    public string DatabaseHost { get => _databaseHost; set => Set(ref _databaseHost, value); }
    public string DatabasePort { get => _databasePort; set => Set(ref _databasePort, value); }
    public bool AllowNoPassword { get => _allowNoPassword; set => Set(ref _allowNoPassword, value); }
    public string LoginCookieValidity { get => _loginCookieValidity; set => Set(ref _loginCookieValidity, value); }
    public string MaxRows { get => _maxRows; set => Set(ref _maxRows, value); }
    public bool ShowServerInfo { get => _showServerInfo; set => Set(ref _showServerInfo, value); }
    public string CustomConfiguration { get => _customConfiguration; set => Set(ref _customConfiguration, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string VersionLabel => _configuredVersion is null ? "" : $"· phpMyAdmin {_configuredVersion}";
    public AsyncRelayCommand SaveCommand { get; }

    public async Task RefreshAsync()
    {
        _configuredVersion = _requestedVersion ?? await VersionedComponentConfiguration.GetActiveVersionAsync(
            _services.Paths, _services.Store, "phpmyadmin");
        Raise(nameof(VersionLabel));
        if (_configuredVersion is null)
        {
            Status = T("phpmyadmin.status.activateFirst");
            return;
        }
        var settings = await VersionedComponentConfiguration.LoadAsync(
            _services.Paths, _services.Store, "phpmyadmin", _configuredVersion, static () => new PhpMyAdminSettings());
        AuthType = settings.AuthType;
        DatabaseHost = settings.DatabaseHost;
        DatabasePort = settings.DatabasePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AllowNoPassword = settings.AllowNoPassword;
        LoginCookieValidity = settings.LoginCookieValidity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MaxRows = settings.MaxRows.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ShowServerInfo = settings.ShowServerInfo;
        CustomConfiguration = settings.CustomConfiguration;
        _blowfishSecret = settings.BlowfishSecret;
    }

    private async Task SaveAsync()
    {
        if (!int.TryParse(DatabasePort, out var port) || port is < 1 or > 65535
            || !int.TryParse(LoginCookieValidity, out var validity) || validity is < 60 or > 86400
            || !int.TryParse(MaxRows, out var maxRows) || maxRows is < 10 or > 1000)
        {
            Status = T("phpmyadmin.error.values");
            return;
        }
        var version = _configuredVersion ?? _requestedVersion ?? await VersionedComponentConfiguration.GetActiveVersionAsync(
            _services.Paths, _services.Store, "phpmyadmin");
        if (version is null)
        {
            Status = T("phpmyadmin.status.activateFirst");
            return;
        }
        var settings = new PhpMyAdminSettings
        {
            AuthType = AuthType,
            DatabaseHost = DatabaseHost.Trim(),
            DatabasePort = port,
            AllowNoPassword = AllowNoPassword,
            LoginCookieValidity = validity,
            MaxRows = maxRows,
            ShowServerInfo = ShowServerInfo,
            BlowfishSecret = _blowfishSecret,
            CustomConfiguration = CustomConfiguration
        };
        await _services.Store.WriteAsync(
            VersionedComponentConfiguration.GetSettingsPath(_services.Paths, "phpmyadmin", version), settings);
        await _services.Generators.OfType<PhpMyAdminConfigGenerator>().Single().GenerateForVersionAsync(version);
        Status = TF("phpmyadmin.status.savedVersion", version);
    }
}

public sealed record AuthTypeOption(string Value, string Label)
{
    public override string ToString() => Label;
}
