using System.Windows.Controls;
using Drageon.App.ViewModels;
namespace Drageon.App;
public partial class DatabaseSettingsWindow : System.Windows.Window
{
    public DatabaseSettingsWindow() => InitializeComponent();
    private void OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is DatabaseSettingsViewModel viewModel && sender is PasswordBox passwordBox) viewModel.NewPassword = passwordBox.Password;
    }
}
