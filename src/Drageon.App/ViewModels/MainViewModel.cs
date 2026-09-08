using System.Collections.ObjectModel;

namespace Drageon.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private object _currentPage;
    private NavigationItem _selectedItem;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Navigation =
        [
            new("dashboard", services.Language.Get("nav.dashboard"), "\uE80F", () => new DashboardViewModel(services)),
            new("components", services.Language.Get("nav.components"), "\uE71D", () => new PackagesViewModel(services)),
            new("projects", services.Language.Get("nav.projects"), "\uE8B7", () => new ProjectsViewModel(services)),
            new("vhosts", services.Language.Get("nav.vhosts"), "\uE774", () => new VHostsViewModel(services)),
            new("cron", services.Language.Get("nav.cron"), "\uE823", () => new CronJobsViewModel(services)),
            new("mail", services.Language.Get("nav.mail"), "\uE715", () => new MailViewModel(services)),
            new("logs", services.Language.Get("nav.logs"), "\uE9D5", () => new LogsViewModel(services)),
            new("settings", services.Language.Get("nav.settings"), "\uE713", () => new SettingsViewModel(services)),
            new("help", services.Language.Get("nav.help"), "\uE897", () => new HelpViewModel(services))
        ];
        _selectedItem = Navigation[0];
        _selectedItem.IsActive = true;
        _currentPage = _selectedItem.Page!;
        NavigateCommand = new RelayCommand(Navigate);
        OpenAboutCommand = new RelayCommand(_ => OpenAbout());
        OpenSupportCommand = new RelayCommand(_ => OpenSupport());
        ExitCommand = new AsyncRelayCommand(_ => ExitAsync());
    }

    public ObservableCollection<NavigationItem> Navigation { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand OpenAboutCommand { get; }
    public RelayCommand OpenSupportCommand { get; }
    public AsyncRelayCommand ExitCommand { get; }
    public bool CloseToTrayOnWindowClose => _services.Settings.Current.CloseToTrayOnWindowClose;

    public NavigationItem SelectedItem
    {
        get => _selectedItem;
        set => Set(ref _selectedItem, value);
    }

    public object CurrentPage
    {
        get => _currentPage;
        private set => Set(ref _currentPage, value);
    }

    public bool NavigateTo(string id)
    {
        var item = Navigation.FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (item is null) return false;
        Navigate(item);
        return true;
    }

    private void Navigate(object? parameter)
    {
        var item = parameter switch
        {
            NavigationItem navigationItem => navigationItem,
            string id => Navigation.FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase)),
            _ => null
        };
        if (item is null) return;
        SelectedItem.IsActive = false;
        SelectedItem = item;
        SelectedItem.IsActive = true;
        CurrentPage = item.Page;
        if (item.Page is IRefreshable refreshable)
        {
            _ = refreshable.RefreshAsync();
        }
    }

    private void OpenAbout()
    {
        SelectedItem.IsActive = false;
        CurrentPage = new AboutViewModel(_services, OpenSupport);
    }

    private void OpenSupport()
    {
        SelectedItem.IsActive = false;
        CurrentPage = new SupportViewModel(_services);
    }

    private static Task ExitAsync() => System.Windows.Application.Current is App app
        ? app.RequestExitAsync()
        : Task.CompletedTask;

}

public sealed class NavigationItem(string id, string title, string glyph, Func<object> pageFactory) : ViewModelBase
{
    private readonly Lazy<object> _page = new(pageFactory);
    private bool _isActive;

    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public object Page => _page.Value;

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }
}

public interface IRefreshable
{
    Task RefreshAsync();
}
