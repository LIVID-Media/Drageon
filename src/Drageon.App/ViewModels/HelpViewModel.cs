namespace Drageon.App.ViewModels;

public sealed class HelpViewModel
{
    public HelpViewModel(AppServices services)
    {
        string T(string key) => services.Language.Get($"help.{key}");
        Title = T("title");
        Subtitle = T("subtitle");
        QuickTitle = T("quick.title");
        QuickText = T("quick.text");
        QuickHint = T("quick.hint");
        ComponentsTitle = T("components.title");
        ComponentsText = T("components.text");
        ComponentsHint = T("components.hint");
        VHostsTitle = T("vhosts.title");
        VHostsText = T("vhosts.text");
        VHostsHint = T("vhosts.hint");
        VHostsApacheExample = T("vhosts.apacheExample");
        VHostsNginxExample = T("vhosts.nginxExample");
        PhpTitle = T("php.title");
        PhpText = T("php.text");
        PhpHint = T("php.hint");
        ServerTitle = T("server.title");
        ServerText = T("server.text");
        ServerHint = T("server.hint");
        CronTitle = T("cron.title");
        CronText = T("cron.text");
        CronExample = T("cron.example");
        CronHint = T("cron.hint");
        MailTitle = T("mail.title");
        MailText = T("mail.text");
        MailPhp = T("mail.php");
        MailServer = T("mail.server");
        MailExample = T("mail.example");
        MailHint = T("mail.hint");
        LogsTitle = T("logs.title");
        LogsText = T("logs.text");
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string QuickTitle { get; }
    public string QuickText { get; }
    public string QuickHint { get; }
    public string ComponentsTitle { get; }
    public string ComponentsText { get; }
    public string ComponentsHint { get; }
    public string VHostsTitle { get; }
    public string VHostsText { get; }
    public string VHostsHint { get; }
    public string VHostsApacheExample { get; }
    public string VHostsNginxExample { get; }
    public string PhpTitle { get; }
    public string PhpText { get; }
    public string PhpHint { get; }
    public string ServerTitle { get; }
    public string ServerText { get; }
    public string ServerHint { get; }
    public string CronTitle { get; }
    public string CronText { get; }
    public string CronExample { get; }
    public string CronHint { get; }
    public string MailTitle { get; }
    public string MailText { get; }
    public string MailPhp { get; }
    public string MailServer { get; }
    public string MailExample { get; }
    public string MailHint { get; }
    public string LogsTitle { get; }
    public string LogsText { get; }
}
