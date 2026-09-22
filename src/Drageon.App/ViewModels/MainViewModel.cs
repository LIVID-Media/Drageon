using System.Collections.ObjectModel;

namespace Drageon.App.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
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
            new("backups", services.Language.Get("nav.backups"), "\uEA35", () => new BackupsViewModel(services)),
            new("vhosts", services.Language.Get("nav.vhosts"), "\uE774", () => new VHostsViewModel(services)),
            new("cron", services.Language.Get("nav.cron"), "\uE823", () => new CronJobsViewModel(services)),
            new("mail", services.Language.Get("nav.mail"), "\uE715", () => new MailViewModel(services)),
            new("logs", services.Language.Get("nav.logs"), "\uE9D5", () => new LogsViewModel(services)),
            new("settings", services.Language.Get("nav.settings"), "\uE713", () => new SettingsViewModel(services)),
            new("help", services.Language.Get("nav.help"), "\uE897", () => new HelpViewModel(services))
        ];
        _selectedItem = Navigation[0];
        _selectedItem.IsActive = true;
        _currentPage = _selectedItem.CreatePage();
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
        if (ReferenceEquals(item, SelectedItem) && item.IsActive)
        {
            if (CurrentPage is IRefreshable current) _ = current.RefreshAsync();
            return;
        }
        DisposeCurrentPage();
        SelectedItem.IsActive = false;
        SelectedItem = item;
        SelectedItem.IsActive = true;
        CurrentPage = item.CreatePage();
    }

    private void OpenAbout()
    {
        DisposeCurrentPage();
        SelectedItem.IsActive = false;
        CurrentPage = new AboutViewModel(_services, OpenSupport);
    }

    private void OpenSupport()
    {
        DisposeCurrentPage();
        SelectedItem.IsActive = false;
        CurrentPage = new SupportViewModel(_services);
    }

    private static Task ExitAsync() => System.Windows.Application.Current is App app
        ? app.RequestExitAsync()
        : Task.CompletedTask;

    public void Dispose()
    {
        DisposeCurrentPage();
    }

    private void DisposeCurrentPage()
    {
        if (CurrentPage is IDisposable disposable) disposable.Dispose();
    }

}

public sealed class NavigationItem(string id, string title, string glyph, Func<object> pageFactory) : ViewModelBase
{
    private readonly Func<object> _pageFactory = pageFactory;
    private bool _isActive;

    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public object CreatePage() => _pageFactory();

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
