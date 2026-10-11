using Drageon.App.ViewModels;

namespace Drageon.App;

public partial class ProjectToolsWindow : System.Windows.Window
{
    private ProjectToolsDialogViewModel? _viewModel;

    public ProjectToolsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += (_, _) => AttachViewModel(null);
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs eventArgs) =>
        AttachViewModel(eventArgs.NewValue as ProjectToolsDialogViewModel);

    private void AttachViewModel(ProjectToolsDialogViewModel? viewModel)
    {
        if (_viewModel is not null) { _viewModel.CloseRequested -= OnCloseRequested; _viewModel.Detach(); }
        _viewModel = viewModel;
        if (_viewModel is not null) _viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs eventArgs) => Close();
}
