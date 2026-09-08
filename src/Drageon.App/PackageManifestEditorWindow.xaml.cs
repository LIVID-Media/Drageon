using Drageon.App.ViewModels;

namespace Drageon.App;

public partial class PackageManifestEditorWindow : System.Windows.Window
{
    private readonly PackageManifestEditorViewModel _viewModel;

    public PackageManifestEditorWindow(PackageManifestEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.Saved += OnSaved;
        Closed += (_, _) => _viewModel.Saved -= OnSaved;
    }

    private void OnSaved(object? sender, EventArgs e)
    {
        DialogResult = true;
    }
}
