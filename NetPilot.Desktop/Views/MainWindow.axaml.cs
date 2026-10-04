using Avalonia.Controls;
using Avalonia.Interactivity;
using NetPilot.Desktop;

namespace NetPilot.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        // The selected page is set through the view model rather than a container's own
        // selection state, so a page stays selected when the shell is rebuilt and the
        // binding stays the single source of truth.
        if (sender is Button b && b.Tag is NavPage page && DataContext is ShellViewModel vm)
            vm.Current = page;
    }
}