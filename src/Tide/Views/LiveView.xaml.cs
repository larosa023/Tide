using System.Windows;
using System.Windows.Controls;
using Tide.Core;
using Tide.ViewModels;

namespace Tide.Views;

public partial class LiveView : UserControl
{
    public LiveView() => InitializeComponent();

    private void ShowGraph_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AppUsage app } && DataContext is MainViewModel vm)
            vm.SelectedApp = app;
    }
}
