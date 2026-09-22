using System.IO;
using Drageon.Core;
using Drageon.Infrastructure;

namespace Drageon.App.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private string _language;
    private string _webServer;
    private string _fastCgiPort;
    private bool _startComponentsWithApp;
    private bool _closeToTrayOnWindowClose;
    private string _httpPort;
    private string _httpsPort;
    private string _smtpPort;
    private bool _notifyOnMailReceived;
    private bool _playSoundOnMailReceived;
    private bool _notifyOnCronJobFinished;
    private bool _playSoundOnCronJobFinished;
    private bool _showQuickLocalhost;
    private bool _showQuickPhpMyAdmin;
    private bool _showQuickPowerShell;
    private bool _showQuickWww;
    private bool _showQuickAppRoot;
    private string _status = "";

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        AppRoot = services.Paths.AppRoot;
        _language = services.Settings.Current.Language;
        _webServer = services.Settings.Current.WebServer;
        _fastCgiPort = services.Settings.Current.FastCgiPort.ToString();
        _startComponentsWithApp = services.Settings.Current.StartComponentsWithApp;
        _closeToTrayOnWindowClose = services.Settings.Current.CloseToTrayOnWindowClose;
        _httpPort = services.Settings.Current.HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _httpsPort = services.Settings.Current.HttpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _smtpPort = services.Settings.Current.SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _notifyOnMailReceived = services.Settings.Current.NotifyOnMailReceived;
        _playSoundOnMailReceived = services.Settings.Current.PlaySoundOnMailReceived;
        _notifyOnCronJobFinished = services.Settings.Current.NotifyOnCronJobFinished;
        _playSoundOnCronJobFinished = services.Settings.Current.PlaySoundOnCronJobFinished;
        _showQuickLocalhost = services.Settings.Current.ShowQuickLocalhost;
        _showQuickPhpMyAdmin = services.Settings.Current.ShowQuickPhpMyAdmin;
        _showQuickPowerShell = services.Settings.Current.ShowQuickPowerShell;
        _showQuickWww = services.Settings.Current.ShowQuickWww;
        _showQuickAppRoot = services.Settings.Current.ShowQuickAppRoot;
        Languages = Directory.EnumerateFiles(services.Paths.Resolve("app", "languages"), "*.json")
            .Select(Path.GetFileNameWithoutExtension).Where(static item => item is not null).Cast<string>().Order().ToArray();
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
    }

    public IReadOnlyDictionary<string, string> WebServers => _services.WebServers.All
        .OrderBy(server => server.DisplayName)
        .ToDictionary(server => server.Id, server => server.DisplayName);
    public string WebServer
    {
        get => _webServer;
        set
        {
            if (Set(ref _webServer, value)) Raise(nameof(ShowFastCgiSettings));
        }
    }
    public bool ShowFastCgiSettings => _services.WebServers.TryGet(WebServer, out var server) && server.UsesFastCgiPort;
    public string FastCgiPort { get => _fastCgiPort; set => Set(ref _fastCgiPort, value); }
    public string AppRoot { get; }
    public IReadOnlyList<string> Languages { get; }
    public string Language { get => _language; set => Set(ref _language, value); }
    public bool StartComponentsWithApp { get => _startComponentsWithApp; set => Set(ref _startComponentsWithApp, value); }
    public bool CloseToTrayOnWindowClose { get => _closeToTrayOnWindowClose; set => Set(ref _closeToTrayOnWindowClose, value); }
    public string HttpPort { get => _httpPort; set => Set(ref _httpPort, value); }
    public string HttpsPort { get => _httpsPort; set => Set(ref _httpsPort, value); }
    public string SmtpPort { get => _smtpPort; set => Set(ref _smtpPort, value); }
    public bool NotifyOnMailReceived { get => _notifyOnMailReceived; set => Set(ref _notifyOnMailReceived, value); }
    public bool PlaySoundOnMailReceived { get => _playSoundOnMailReceived; set => Set(ref _playSoundOnMailReceived, value); }
    public bool NotifyOnCronJobFinished { get => _notifyOnCronJobFinished; set => Set(ref _notifyOnCronJobFinished, value); }
    public bool PlaySoundOnCronJobFinished { get => _playSoundOnCronJobFinished; set => Set(ref _playSoundOnCronJobFinished, value); }
    public bool ShowQuickLocalhost { get => _showQuickLocalhost; set => Set(ref _showQuickLocalhost, value); }
    public bool ShowQuickPhpMyAdmin { get => _showQuickPhpMyAdmin; set => Set(ref _showQuickPhpMyAdmin, value); }
    public bool ShowQuickPowerShell { get => _showQuickPowerShell; set => Set(ref _showQuickPowerShell, value); }
    public bool ShowQuickWww { get => _showQuickWww; set => Set(ref _showQuickWww, value); }
    public bool ShowQuickAppRoot { get => _showQuickAppRoot; set => Set(ref _showQuickAppRoot, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public AsyncRelayCommand SaveCommand { get; }

    private async Task SaveAsync()
    {
        try { await SaveCoreAsync(); }
        catch (Exception ex) { Status = ex.Message; _services.Logs.Write("Application", ex.Message, LogSeverity.Error); }
    }
    private async Task SaveCoreAsync()
    {
        var fastCgi = _services.Settings.Current.FastCgiPort;
        if (!TryPort(HttpPort, out var http) || !TryPort(HttpsPort, out var https) || !TryPort(SmtpPort, out var smtp)
            || !_services.WebServers.TryGet(WebServer, out var selectedWebServer)
            || (selectedWebServer.UsesFastCgiPort
                && (!TryPort(FastCgiPort, out fastCgi) || http == fastCgi)))
        {
            Status = T("settings.error.ports");
            return;
        }

        var settings = new AppSettings
        {
            Language = Language,
            WebServer = WebServer,
            FastCgiPort = fastCgi,
            StartComponentsWithApp = StartComponentsWithApp,
            CloseToTrayOnWindowClose = CloseToTrayOnWindowClose,
            HttpPort = http,
            HttpsPort = https,
            SmtpPort = smtp,
            NotifyOnMailReceived = NotifyOnMailReceived,
            PlaySoundOnMailReceived = PlaySoundOnMailReceived,
            NotifyOnCronJobFinished = NotifyOnCronJobFinished,
            PlaySoundOnCronJobFinished = PlaySoundOnCronJobFinished,
            ShowQuickLocalhost = ShowQuickLocalhost,
            ShowQuickPhpMyAdmin = ShowQuickPhpMyAdmin,
            ShowQuickPowerShell = ShowQuickPowerShell,
            ShowQuickWww = ShowQuickWww,
            ShowQuickAppRoot = ShowQuickAppRoot
        };
        var previous = _services.Settings.Current;
        var webSettingsChanged = previous.WebServer != settings.WebServer || previous.HttpPort != http || previous.FastCgiPort != fastCgi;
        var webServerIds = _services.WebServers.All.Select(server => server.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restartWebServer = webSettingsChanged && _services.Processes.GetStatuses().Any(s => webServerIds.Contains(s.Id)
            && s.State is ComponentState.Running or ComponentState.Starting);
        if (restartWebServer)
        {
            var installed = await _services.Packages.GetInstalledAsync();
            foreach (var id in selectedWebServer.RequiredPackageIds)
                if (!installed.Any(package => package.Id == id && package.IsActive))
                    throw new InvalidOperationException(TF("runtime.noActiveVersion", id));
            await _services.Runtime.StopAsync(previous.WebServer);
        }
        await _services.Settings.SaveAsync(settings);

        try
        {
            if (restartWebServer) await _services.Runtime.StartAsync(settings.WebServer);
            Status = T("settings.status.saved");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            _services.Logs.Write(settings.WebServer, ex.Message, LogSeverity.Error);
        }
    }

    private static bool TryPort(string value, out int port) =>
        int.TryParse(value, out port) && port is > 0 and <= 65535;
}


