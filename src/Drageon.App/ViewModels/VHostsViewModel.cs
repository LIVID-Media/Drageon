using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Drageon.Core;
using Drageon.Infrastructure;
using Microsoft.Win32;

namespace Drageon.App.ViewModels;

public sealed class VHostsViewModel : ViewModelBase, IRefreshable
{
    private readonly AppServices _services;
    private VHostListItem? _selectedHost;
    private string _hostName = "", _serverName = "localhost", _documentRoot = "", _port = "", _status = "";
    private bool _enabled = true;

    public VHostsViewModel(AppServices services)
    {
        _services = services;
        ServerConfigurations = new(services.WebServers.All.Select(server => new VHostServerConfigurationViewModel(server.Id, server.DisplayName)));
        NewCommand = new(_ => NewAsync()); SaveCommand = new(_ => SaveAsync(false)); SaveAndRestartCommand = new(_ => SaveAsync(true));
        DeleteCommand = new(_ => DeleteAsync()); RefreshCommand = new(_ => RefreshAsync());
        BrowseCommand = new(_ => Browse());
        OpenVhostFolderCommand = new RelayCommand(_ => OpenVhostFolder());
        _ = RefreshAsync();
    }

    public ObservableCollection<VHostListItem> Hosts { get; } = [];
    public ObservableCollection<VHostServerConfigurationViewModel> ServerConfigurations { get; }
    public AsyncRelayCommand NewCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand SaveAndRestartCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public VHostListItem? SelectedHost { get => _selectedHost; set { if (Set(ref _selectedHost, value) && value is not null) _ = LoadSelectedAsync(value); } }
    public RelayCommand OpenVhostFolderCommand { get; }
    public string HostName { get => _hostName; set => Set(ref _hostName, value); }
    public string ServerName { get => _serverName; set => Set(ref _serverName, value); }
    public string DocumentRoot { get => _documentRoot; set => Set(ref _documentRoot, value); }
    public string Port { get => _port; set => Set(ref _port, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public async Task RefreshAsync()
    {
        Directory.CreateDirectory(DirectoryPath);
        var previous = SelectedHost?.Path;
        Hosts.Clear();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json").Order(StringComparer.CurrentCultureIgnoreCase))
            try { var host = await _services.Store.ReadAsync<VirtualHostDefinition>(path); if (host is not null) Hosts.Add(new(host.Host, path, host.Enabled)); }
            catch (Exception exception) { _services.Logs.Write("vhosts", $"{Path.GetFileName(path)}: {exception.Message}", LogSeverity.Warning); }
        SelectedHost = Hosts.FirstOrDefault(item => item.Path.Equals(previous, StringComparison.OrdinalIgnoreCase)) ?? Hosts.FirstOrDefault();
        if (SelectedHost is null) await NewAsync();
    }

    private Task NewAsync()
    {
        _selectedHost = null;
        Raise(nameof(SelectedHost));
        HostName = "";
        ServerName = "localhost";
        DocumentRoot = "";
        Port = _services.Settings.Current.HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Enabled = true;
        foreach (var configuration in ServerConfigurations) configuration.Content = "";
        Status = T("vhosts.status.new");
        return Task.CompletedTask;
    }

    private async Task LoadSelectedAsync(VHostListItem item)
    {
        try
        {
            var host = await _services.Store.ReadAsync<VirtualHostDefinition>(item.Path) ?? throw new InvalidDataException(T("vhosts.error.data"));
            SplitHost(host.Host);
            DocumentRoot = ToPortablePath(host.DocumentRoot);
            Port = (host.Port ?? _services.Settings.Current.HttpPort).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Enabled = host.Enabled;
            foreach (var configuration in ServerConfigurations) configuration.Content = host.GetServerConfiguration(configuration.ServerId);
            Status = TF("vhosts.status.loaded", Path.GetFileName(item.Path));
        }
        catch (Exception exception) { Status = exception.Message; }
    }

    private async Task SaveAsync(bool restart)
    {
        try
        {
            var fileName = await SaveCoreAsync(); await GenerateAllAsync(); await RefreshAsync();
            var activeServer = _services.Settings.Current.WebServer;
            var running = _services.Processes.GetStatuses().Any(status => status.Id.Equals(activeServer, StringComparison.OrdinalIgnoreCase) && status.State == ComponentState.Running);
            if (restart && running) { await _services.Runtime.StopAsync(activeServer); await _services.Runtime.StartAsync(activeServer); Status = TF("vhosts.status.savedRestarted", fileName); }
            else Status = TF("vhosts.status.saved", fileName);
        }
        catch (Exception exception) { Status = exception.Message; _services.Logs.Write(_services.Settings.Current.WebServer, exception.Message, LogSeverity.Error); }
    }

    private async Task<string> SaveCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(HostName)
            || string.IsNullOrWhiteSpace(ServerName)
            || string.IsNullOrWhiteSpace(DocumentRoot))
            throw new InvalidDataException(T("vhosts.error.required"));
        if (!int.TryParse(Port, out var parsedPort) || parsedPort is < 1 or > 65535) throw new InvalidDataException(T("vhosts.error.data"));
        var fullHost = CreateFullHost();
        var data = ServerConfigurations.ToDictionary(item => item.ServerId, item => JsonSerializer.SerializeToElement(item.Content ?? ""), StringComparer.OrdinalIgnoreCase);
        var host = new VirtualHostDefinition(fullHost, DocumentRoot.Trim(), Enabled,
            parsedPort == _services.Settings.Current.HttpPort ? null : parsedPort) { ServerConfiguration = data };
        new VirtualHostStore(_services.Paths, _services.Store).Resolve(host);
        var oldPath = SelectedHost?.Path;
        var path = Path.Combine(DirectoryPath, CreateFileName(host.Host));
        if (File.Exists(path) && !path.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(TF("vhosts.error.hostExists", host.Host));
        await _services.Store.WriteAsync(path, host);
        if (oldPath is not null && !oldPath.Equals(path, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath))
            File.Delete(oldPath);
        _selectedHost = new(host.Host, path, host.Enabled);
        return Path.GetFileName(path);
    }

    private async Task DeleteAsync()
    {
        if (SelectedHost is null) return;
        if (System.Windows.MessageBox.Show(TF("vhosts.confirm.delete", SelectedHost.HostName), T("vhosts.deleteTitle"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
        var fileName = Path.GetFileName(SelectedHost.Path); File.Delete(SelectedHost.Path);
        await GenerateAllAsync(); await RefreshAsync(); Status = TF("vhosts.status.deleted", fileName);
    }

    private async Task GenerateAllAsync()
    {
        foreach (var server in _services.WebServers.All)
        {
            if (await VersionedComponentConfiguration.GetActiveVersionAsync(_services.Paths, _services.Store, server.Id) is null) continue;
            var generator = _services.Generators.FirstOrDefault(item => item.ComponentId.Equals(server.Id, StringComparison.OrdinalIgnoreCase));
            if (generator is not null) await generator.GenerateAsync();
        }
    }

    private void OpenVhostFolder()
    {
        var vhostRoot = _services.Paths.Resolve("app", "config", "vhosts");
        try
        {
            Directory.CreateDirectory(vhostRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{vhostRoot}\"") { UseShellExecute = true });
            Status = T("vhosts.status.folderOpened");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Status = TF("vhosts.status.folderFailed", exception.Message);
        }
    }

    private void Browse()
    {
        var www = _services.Paths.Resolve("www"); Directory.CreateDirectory(www);
        var initialDirectory = GetDocumentRootDirectory() ?? www;
        var dialog = new OpenFolderDialog { InitialDirectory = initialDirectory, Multiselect = false, Title = T("vhosts.browseTitle") };
        if (dialog.ShowDialog() == true) DocumentRoot = ToPortablePath(dialog.FolderName);
    }

    private string? GetDocumentRootDirectory()
    {
        if (string.IsNullOrWhiteSpace(DocumentRoot)) return null;
        try
        {
            var candidate = Path.GetFullPath(Path.IsPathRooted(DocumentRoot)
                ? DocumentRoot
                : Path.Combine(_services.Paths.AppRoot, DocumentRoot.Replace('/', Path.DirectorySeparatorChar)));
            return Directory.Exists(candidate) ? candidate : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private string ToPortablePath(string path)
    {
        if (!Path.IsPathRooted(path)) return path.Replace('\\', '/');
        var relative = Path.GetRelativePath(_services.Paths.AppRoot, Path.GetFullPath(path));
        return relative != ".." && !Path.IsPathRooted(relative) && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? relative.Replace('\\', '/') : Path.GetFullPath(path);
    }
    private string CreateFullHost()
    {
        var hostName = HostName.Trim().Trim('.');
        var serverName = ServerName.Trim().Trim('.');
        if (hostName.Length == 0 || serverName.Length == 0)
            throw new InvalidDataException(T("vhosts.error.hostParts"));
        return $"{hostName}.{serverName}".ToLowerInvariant();
    }
    private void SplitHost(string fullHost)
    {
        var separator = fullHost.IndexOf('.');
        if (separator > 0 && separator < fullHost.Length - 1)
        {
            HostName = fullHost[..separator];
            ServerName = fullHost[(separator + 1)..];
        }
        else
        {
            HostName = fullHost;
            ServerName = "localhost";
        }
    }
    private string DirectoryPath => _services.Paths.Resolve("app", "config", "vhosts");
    private static string CreateFileName(string host) { var invalid = Path.GetInvalidFileNameChars(); var safe = new string(host.Trim().ToLowerInvariant().Select(c => invalid.Contains(c) ? '-' : c).ToArray()); return (string.IsNullOrWhiteSpace(safe) ? "vhost" : safe) + ".json"; }
}

public sealed record VHostListItem(string HostName, string Path, bool Enabled)
{
    public string DisplayName => Enabled ? HostName : $"{HostName} ({Localization.Get("state.inactive")})";
}

public sealed class VHostServerConfigurationViewModel(string serverId, string displayName) : ViewModelBase
{
    private string _content = "";
    public string ServerId { get; } = serverId;
    public string DisplayName { get; } = displayName;
    public string Content { get => _content; set => Set(ref _content, value); }
}
