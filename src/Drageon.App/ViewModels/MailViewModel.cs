using System.Collections.ObjectModel;
using System.IO;
using Drageon.Core;

namespace Drageon.App.ViewModels;

public sealed class MailViewModel : ViewModelBase, IRefreshable, IDisposable
{
    private readonly AppServices _services;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private MailItemViewModel? _selectedMessage;
    private MailContentModeOption _selectedContentMode;
    private string _displayContent = "";
    private string _status = "";

    public MailViewModel(AppServices services)
    {
        _services = services;
        ContentModes =
        [
            new(MailContentMode.Text, T("mail.mode.text")),
            new(MailContentMode.Html, T("mail.mode.html")),
            new(MailContentMode.Headers, T("mail.mode.headers")),
            new(MailContentMode.Raw, T("mail.mode.raw"))
        ];
        _selectedContentMode = ContentModes[0];
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        StartStopCommand = new AsyncRelayCommand(_ => StartStopAsync());
        ClearCommand = new AsyncRelayCommand(_ => ClearAsync());
        OpenAttachmentsCommand = new RelayCommand(_ => OpenAttachments());
        _services.Mail.MessageReceived += OnMessageReceived;
        _ = RefreshAsync();
    }

    public ObservableCollection<MailItemViewModel> Messages { get; } = [];
    public ObservableCollection<string> Attachments { get; } = [];
    public IReadOnlyList<MailContentModeOption> ContentModes { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand StartStopCommand { get; }
    public AsyncRelayCommand ClearCommand { get; }
    public RelayCommand OpenAttachmentsCommand { get; }
    public string MailDirectory => _services.Paths.Resolve("data", "mail");
    public bool IsRunning => _services.Mail.IsRunning;
    public bool IsOperational => IsRunning;
    public string CatcherState => T(IsRunning ? "state.running" : "state.stopped");
    public string StartStopText => IsRunning ? T("mail.action.stop") : T("mail.action.start");
    public string StartStopGlyph => IsRunning ? "\uE73B" : "\uF5B0";
    public string Endpoint => $"127.0.0.1:{_services.Mail.Port}";

    public MailItemViewModel? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (!Set(ref _selectedMessage, value)) return;
            UpdateMessageDetails();
        }
    }

    public MailContentModeOption SelectedContentMode
    {
        get => _selectedContentMode;
        set
        {
            if (!Set(ref _selectedContentMode, value)) return;
            UpdateDisplayContent();
        }
    }

    public string DisplayContent { get => _displayContent; private set => Set(ref _displayContent, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public async Task RefreshAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            var selectedId = SelectedMessage?.Message.Id;
            var messages = await _services.Mail.GetMessagesAsync();
            Messages.Clear();
            foreach (var message in messages) Messages.Add(new MailItemViewModel(message));
            SelectedMessage = selectedId is null
                ? Messages.FirstOrDefault()
                : Messages.FirstOrDefault(item => item.Message.Id == selectedId) ?? Messages.FirstOrDefault();
            Raise(nameof(IsRunning));
            Raise(nameof(IsOperational));
            Raise(nameof(CatcherState));
            Raise(nameof(StartStopText));
            Raise(nameof(StartStopGlyph));
            Raise(nameof(Endpoint));
            Status = IsRunning ? TF("mail.status.running", Endpoint) : T("mail.status.stopped");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task StartStopAsync()
    {
        try
        {
            if (_services.Mail.IsRunning) await _services.Mail.StopAsync();
            else await _services.Mail.StartAsync();
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    private async Task ClearAsync()
    {
        if (Messages.Count == 0) return;
        var result = System.Windows.MessageBox.Show(T("mail.confirm.clear"), T("mail.action.clear"),
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;
        foreach (var item in Messages)
        {
            var directory = _services.Paths.Resolve("data", "mail", item.Message.Id.ToString("N"));
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        await RefreshAsync();
        Status = T("mail.status.cleared");
    }

    private void UpdateMessageDetails()
    {
        Attachments.Clear();
        if (SelectedMessage is not null)
        {
            foreach (var attachment in SelectedMessage.Message.Attachments) Attachments.Add(attachment);
        }
        UpdateDisplayContent();
    }

    private void UpdateDisplayContent()
    {
        if (SelectedMessage is null) { DisplayContent = ""; return; }
        var message = SelectedMessage.Message;
        DisplayContent = SelectedContentMode.Mode switch
        {
            MailContentMode.Html => message.HtmlBody,
            MailContentMode.Headers => message.Headers,
            MailContentMode.Raw => ReadRaw(message),
            _ => message.TextBody
        };
    }

    private string ReadRaw(CapturedMailMessage message)
    {
        var file = _services.Paths.Resolve("data", "mail", message.Id.ToString("N"), message.RawFile);
        return File.Exists(file) ? File.ReadAllText(file) : T("mail.raw.missing");
    }

    private void OpenAttachments()
    {
        var directory = SelectedMessage is null
            ? MailDirectory
            : _services.Paths.Resolve("data", "mail", SelectedMessage.Message.Id.ToString("N"), "attachments");
        try
        {
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
            Status = T("mail.status.attachmentsOpened");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            Status = TF("mail.status.attachmentsFailed", exception.Message);
        }
    }

    private void OnMessageReceived(object? sender, CapturedMailMessage message)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (Messages.Any(item => item.Message.Id == message.Id)) return;
            Messages.Insert(0, new MailItemViewModel(message));
            SelectedMessage = Messages[0];
            Status = TF("mail.status.received", message.Subject);
        });
    }

    public void Dispose()
    {
        _services.Mail.MessageReceived -= OnMessageReceived;
    }
}

public sealed class MailItemViewModel
{
    public MailItemViewModel(CapturedMailMessage message) => Message = message;

    public CapturedMailMessage Message { get; }
    public string Subject => string.IsNullOrWhiteSpace(Message.Subject) ? Localization.Get("mail.subject.empty") : Message.Subject;
    public string From => Message.From;
    public string Received => Message.ReceivedAt.LocalDateTime.ToString("g");
    public string Recipients => string.Join(", ", Message.To);
    public string HeaderLine => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        Localization.Get("mail.header.format"),
        From,
        Recipients);
    public string AttachmentInfo => Message.Attachments.Count == 0
        ? ""
        : string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.Get("mail.attachment.count"), Message.Attachments.Count);
}

public enum MailContentMode { Text, Html, Headers, Raw }

public sealed record MailContentModeOption(MailContentMode Mode, string DisplayName)
{
    public override string ToString() => DisplayName;
}
