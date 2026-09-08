using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Drageon.Core;

namespace Drageon.Infrastructure;

public sealed class SmtpCatcher(
    IPortablePathService paths,
    JsonFileStore store,
    ISettingsService settings,
    IAppLogService logs,
    ILanguageService? language = null) : ISmtpCatcher
{
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptLoop;

    public event EventHandler<CapturedMailMessage>? MessageReceived;
    public bool IsRunning => _listener is not null;
    public int Port { get; private set; } = settings.Current.SmtpPort;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return Task.CompletedTask;
        Port = settings.Current.SmtpPort;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_lifetime.Token);
        logs.Write("Mail", TF("smtp.listening", "SMTP-Catcher lauscht auf {0}.", $"127.0.0.1:{Port}"));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is null) return;
        _lifetime?.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (SocketException) { }
        }
        _listener = null;
        _lifetime?.Dispose();
        _lifetime = null;
        logs.Write("Mail", T("smtp.stopped", "SMTP-Catcher beendet."));
    }

    public async Task<IReadOnlyList<CapturedMailMessage>> GetMessagesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<CapturedMailMessage>();
        foreach (var file in Directory.EnumerateFiles(paths.Resolve("data", "mail"), "message.json", SearchOption.AllDirectories))
        {
            try
            {
                var message = await store.ReadAsync<CapturedMailMessage>(file, cancellationToken).ConfigureAwait(false);
                if (message is not null) result.Add(message);
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
            {
                logs.Write("Mail", TF("smtp.readFailed", "Nachricht konnte nicht gelesen werden: {0}: {1}", file, exception.Message), LogSeverity.Warning);
            }
        }
        return result.OrderByDescending(static item => item.ReceivedAt).ToArray();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { NewLine = "\r\n", AutoFlush = true })
        {
            var from = "";
            var recipients = new List<string>();
            await writer.WriteLineAsync("220 localhost Drageon SMTP-Catcher").ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                var command = line.Split(' ', 2)[0].ToUpperInvariant();
                switch (command)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-localhost").ConfigureAwait(false);
                        await writer.WriteLineAsync("250-8BITMIME").ConfigureAwait(false);
                        await writer.WriteLineAsync("250 SIZE 52428800").ConfigureAwait(false);
                        break;
                    case "HELO":
                        await writer.WriteLineAsync("250 localhost").ConfigureAwait(false);
                        break;
                    case "MAIL":
                        from = ExtractAddress(line);
                        recipients.Clear();
                        await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
                        break;
                    case "RCPT":
                        recipients.Add(ExtractAddress(line));
                        await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>").ConfigureAwait(false);
                        var raw = new StringBuilder();
                        while (true)
                        {
                            var dataLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                            if (dataLine is null || dataLine == ".") break;
                            if (dataLine.StartsWith("..", StringComparison.Ordinal)) dataLine = dataLine[1..];
                            raw.Append(dataLine).Append("\r\n");
                        }
                        try
                        {
                            await CaptureAsync(raw.ToString(), from, recipients, cancellationToken).ConfigureAwait(false);
                            await writer.WriteLineAsync("250 Message accepted").ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            logs.Write("Mail", exception.Message, LogSeverity.Error);
                            await writer.WriteLineAsync("451 Message could not be stored").ConfigureAwait(false);
                        }
                        break;
                    case "RSET":
                        from = "";
                        recipients.Clear();
                        await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
                        break;
                    case "NOOP":
                        await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 Bye").ConfigureAwait(false);
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK").ConfigureAwait(false);
                        break;
                }
            }
        }
    }

    private async Task CaptureAsync(string raw, string envelopeFrom, IReadOnlyList<string> envelopeTo, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var directory = paths.Resolve("data", "mail", id.ToString("N"));
        var attachmentDirectory = Path.Combine(directory, "attachments");
        Directory.CreateDirectory(attachmentDirectory);
        var rawFile = Path.Combine(directory, "raw.eml");
        await File.WriteAllTextAsync(rawFile, raw, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        var split = SplitHeaders(raw);
        var headers = ParseHeaders(split.Headers);
        var parsed = new ParsedMail();
        ParseMimePart(headers, split.Body, attachmentDirectory, parsed);
        var to = GetHeader(headers, "To").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var message = new CapturedMailMessage
        {
            Id = id,
            ReceivedAt = DateTimeOffset.Now,
            From = DecodeHeader(GetHeader(headers, "From")) is { Length: > 0 } headerFrom ? headerFrom : envelopeFrom,
            To = to.Length > 0 ? to.Select(DecodeHeader).ToArray() : envelopeTo.ToArray(),
            Subject = DecodeHeader(GetHeader(headers, "Subject")),
            TextBody = parsed.TextBody,
            HtmlBody = parsed.HtmlBody,
            Headers = split.Headers,
            Attachments = parsed.Attachments,
            RawFile = "raw.eml"
        };
        await store.WriteAsync(Path.Combine(directory, "message.json"), message, cancellationToken).ConfigureAwait(false);
        logs.Write("Mail", TF("smtp.received", "Testmail empfangen: {0} ({1})", message.Subject, message.From));
        MessageReceived?.Invoke(this, message);
    }

    private static void ParseMimePart(Dictionary<string, string> headers, string body, string attachmentDirectory, ParsedMail parsed)
    {
        var contentType = GetHeader(headers, "Content-Type");
        if (contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            var boundary = GetParameter(contentType, "boundary");
            if (boundary.Length == 0) return;
            foreach (var part in SplitMultipart(body, boundary))
            {
                var split = SplitHeaders(part);
                ParseMimePart(ParseHeaders(split.Headers), split.Body, attachmentDirectory, parsed);
            }
            return;
        }

        var transferEncoding = GetHeader(headers, "Content-Transfer-Encoding");
        var bytes = DecodeBody(body, transferEncoding);
        var disposition = GetHeader(headers, "Content-Disposition");
        var fileName = GetParameter(disposition, "filename");
        if (fileName.Length == 0) fileName = GetParameter(contentType, "name");
        if (fileName.Length > 0 || disposition.StartsWith("attachment", StringComparison.OrdinalIgnoreCase))
        {
            fileName = SafeFileName(DecodeHeader(fileName.Length > 0 ? fileName : "anhang.bin"));
            var path = UniqueFile(Path.Combine(attachmentDirectory, fileName));
            File.WriteAllBytes(path, bytes);
            parsed.Attachments.Add(Path.GetFileName(path));
            return;
        }

        var charset = GetParameter(contentType, "charset");
        var text = DecodeText(bytes, charset);
        if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) parsed.HtmlBody += text;
        else parsed.TextBody += text;
    }

    private static (string Headers, string Body) SplitHeaders(string value)
    {
        var index = value.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var length = 4;
        if (index < 0) { index = value.IndexOf("\n\n", StringComparison.Ordinal); length = 2; }
        return index < 0 ? (value, "") : (value[..index], value[(index + length)..]);
    }

    private static Dictionary<string, string> ParseHeaders(string rawHeaders)
    {
        var unfolded = Regex.Replace(rawHeaders, "\\r?\\n[ \\t]+", " ");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in unfolded.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            result[name] = result.TryGetValue(name, out var existing) ? $"{existing}, {value}" : value;
        }
        return result;
    }

    private static IEnumerable<string> SplitMultipart(string body, string boundary)
    {
        var marker = "--" + boundary;
        return body.Split(marker, StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Trim('\r', '\n'))
            .Where(static part => part.Length > 0 && part != "--")
            .Select(static part => part.EndsWith("--", StringComparison.Ordinal) ? part[..^2].TrimEnd('\r', '\n') : part);
    }

    private static byte[] DecodeBody(string body, string encoding)
    {
        if (encoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
        {
            try { return Convert.FromBase64String(Regex.Replace(body, @"\s", "")); } catch (FormatException) { }
        }
        if (encoding.Equals("quoted-printable", StringComparison.OrdinalIgnoreCase)) return DecodeQuotedPrintable(body, false);
        return Encoding.UTF8.GetBytes(body);
    }

    private static byte[] DecodeQuotedPrintable(string value, bool header)
    {
        value = value.Replace("=\r\n", "", StringComparison.Ordinal).Replace("=\n", "", StringComparison.Ordinal);
        using var output = new MemoryStream();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '=' && index + 2 < value.Length && byte.TryParse(value.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var parsed))
            {
                output.WriteByte(parsed);
                index += 2;
            }
            else
            {
                var character = header && value[index] == '_' ? ' ' : value[index];
                foreach (var item in Encoding.UTF8.GetBytes(character.ToString())) output.WriteByte(item);
            }
        }
        return output.ToArray();
    }

    private static string DecodeHeader(string value) => Regex.Replace(value, @"=\?(?<charset>[^?]+)\?(?<kind>[BbQq])\?(?<data>[^?]+)\?=", match =>
    {
        try
        {
            var bytes = match.Groups["kind"].Value.Equals("B", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(match.Groups["data"].Value)
                : DecodeQuotedPrintable(match.Groups["data"].Value, true);
            return DecodeText(bytes, match.Groups["charset"].Value);
        }
        catch (Exception) { return match.Value; }
    });

    private static string DecodeText(byte[] bytes, string charset)
    {
        try
        {
            if (charset.Contains("8859-1", StringComparison.OrdinalIgnoreCase) || charset.Contains("latin1", StringComparison.OrdinalIgnoreCase)) return Encoding.Latin1.GetString(bytes);
            return charset.Length > 0 ? Encoding.GetEncoding(charset).GetString(bytes) : Encoding.UTF8.GetString(bytes);
        }
        catch (ArgumentException) { return Encoding.UTF8.GetString(bytes); }
    }

    private static string ExtractAddress(string command)
    {
        var colon = command.IndexOf(':');
        return (colon >= 0 ? command[(colon + 1)..] : command).Trim().Trim('<', '>');
    }

    private static string GetHeader(Dictionary<string, string> headers, string name) => headers.GetValueOrDefault(name, "");

    private static string GetParameter(string header, string name)
    {
        var match = Regex.Match(header, $@"(?:^|;)\s*{Regex.Escape(name)}\s*=\s*(?:\""(?<quoted>[^\""\r\n]*)\""|(?<plain>[^;\r\n]+))", RegexOptions.IgnoreCase);
        return (match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value).Trim();
    }

    private static string SafeFileName(string name)
    {
        foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
        return string.IsNullOrWhiteSpace(name) ? "anhang.bin" : name;
    }

    private static string UniqueFile(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{name}-{index}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private string T(string key, string fallback) => language?.Get(key, fallback) ?? fallback;
    private string TF(string key, string fallback, params object?[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, T(key, fallback), arguments);

    private sealed class ParsedMail
    {
        public string TextBody { get; set; } = "";
        public string HtmlBody { get; set; } = "";
        public List<string> Attachments { get; } = [];
    }
}
