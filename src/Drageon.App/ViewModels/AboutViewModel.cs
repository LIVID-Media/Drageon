using System.Diagnostics;

namespace Drageon.App.ViewModels;

public sealed class AboutViewModel
{
    public const string GitHubUrl = "https://github.com/LIVID-Media/Drageon";
    public const string LicenseUrl = "https://www.mozilla.org/en-US/MPL/2.0/";

    public AboutViewModel(AppServices services, Action openSupport)
    {
        Title = services.Language.Get("about.title");
        Subtitle = services.Language.Get("app.subtitle");
        OpenSource = services.Language.Get("about.openSource");
        GitHubText = services.Language.Get("about.github");
        SupportPrompt = services.Language.Get("about.supportPrompt");
        SupportText = services.Language.Get("about.support");
        LicenseText = services.Language.Get("about.license");
        OpenGitHubCommand = new RelayCommand(_ => Open(GitHubUrl));
        OpenLicenseCommand = new RelayCommand(_ => Open(LicenseUrl));
        OpenSupportCommand = new RelayCommand(_ => openSupport());
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string OpenSource { get; }
    public string GitHubText { get; }
    public string SupportPrompt { get; }
    public string SupportText { get; }
    public string LicenseText { get; }
    public RelayCommand OpenGitHubCommand { get; }
    public RelayCommand OpenLicenseCommand { get; }
    public RelayCommand OpenSupportCommand { get; }

    private static void Open(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
