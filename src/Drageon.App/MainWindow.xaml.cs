using System.ComponentModel;
using System.Windows;
using Drageon.App.ViewModels;

namespace Drageon.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    public bool AllowClose { get; set; }
    public event EventHandler? HiddenToTray;

    public void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose)
        {
            return;
        }

        var closeToTray = DataContext is not MainViewModel viewModel || viewModel.CloseToTrayOnWindowClose;
        if (!closeToTray)
        {
            e.Cancel = true;
            if (System.Windows.Application.Current is App app) _ = app.RequestExitAsync();
            return;
        }

        e.Cancel = true;
        ShowInTaskbar = false;
        Hide();
        HiddenToTray?.Invoke(this, EventArgs.Empty);
    }
}
