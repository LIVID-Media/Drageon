using System.Diagnostics;

namespace Drageon.App.ViewModels;

public sealed class SupportViewModel
{
    public const string KofiUrl = "https://ko-fi.com/livid_media";
    public const string PayPalUrl = "https://www.paypal.com/donate/?hosted_button_id=93Z5AXMPT25EE";

    public SupportViewModel(AppServices services)
    {
        Title = services.Language.Get("support.title");
        Subtitle = services.Language.Get("support.subtitle");
        Description = services.Language.Get("support.description");
        Thank = services.Language.Get("support.thank");
        OpenKofiCommand = new RelayCommand(_ => Open(KofiUrl));
        OpenPayPalCommand = new RelayCommand(_ => Open(PayPalUrl));
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string Description { get; }
    public string Thank { get; }
    public RelayCommand OpenKofiCommand { get; }
    public RelayCommand OpenPayPalCommand { get; }

    private static void Open(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
