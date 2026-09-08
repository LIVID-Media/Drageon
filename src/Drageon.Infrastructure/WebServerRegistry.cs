using Drageon.Core;

namespace Drageon.Infrastructure;

public sealed class WebServerRegistry(IEnumerable<IWebServer> webServers) : IWebServerRegistry
{
    private readonly IReadOnlyDictionary<string, IWebServer> _webServers = webServers
        .ToDictionary(server => server.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IWebServer> All => _webServers.Values.ToArray();

    public bool TryGet(string id, out IWebServer webServer) => _webServers.TryGetValue(id, out webServer!);

    public IWebServer Get(string id) => TryGet(id, out var webServer)
        ? webServer
        : throw new InvalidOperationException($"Unknown web server: {id}");
}
