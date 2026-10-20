using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CardViewer;

public partial class MainWindow : Window
{
    private readonly ThumbnailViewport viewport;
    private bool suppressTileOpen;
    public MainWindowViewModel ViewModel { get; }

    public MainWindow(MainWindowViewModel viewModel, string? initialFolder = null)
    {
        InitializeComponent();
        AppTheme.Apply(this);
        ViewModel = viewModel;
        viewport = new ThumbnailViewport(GridList, item => viewModel.ShowThumbnailCommand.Execute(item), item => viewModel.HideThumbnailCommand.Execute(item));
        DataContext = viewModel;
        Closed += (_, _) => { viewport.Dispose(); viewModel.Dispose(); };
        if (initialFolder != null)
            Loaded += (_, _) => viewModel.OpenFolderCommand.Execute(initialFolder);
    }

    // Virtualized tile lifetimes and hit testing are view responsibilities.
    private void TileLoaded(object sender, RoutedEventArgs e)
    {
        viewport.Track((FrameworkElement)sender);
    }

    private void TileUnloaded(object sender, RoutedEventArgs e)
    {
        viewport.Untrack((FrameworkElement)sender);
    }

    private void TileDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        viewport.ChangeItem((FrameworkElement)sender);
    }

    private void OpenTile(object sender, MouseButtonEventArgs e)
    {
        if (suppressTileOpen) { suppressTileOpen = false; return; }
        for (DependencyObject? current = e.OriginalSource as DependencyObject; current != null;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is CheckBox)
                return;
        }
        if (((FrameworkElement)sender).DataContext is ImageItem item)
            ViewModel.OpenImageCommand.Execute(item);
    }

    private void SelectTileRange(object sender, MouseButtonEventArgs e)
    {
        suppressTileOpen = false;
        if (((FrameworkElement)sender).DataContext is not ImageItem item) return;
        bool checkbox = false;
        for (DependencyObject? current = e.OriginalSource as DependencyObject; current != null;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is CheckBox) { checkbox = true; break; }
        e.Handled = ApplySelectionGesture(item, Keyboard.Modifiers, checkbox);
    }

    internal bool ApplySelectionGesture(ImageItem item, ModifierKeys modifiers, bool checkbox)
    {
        if (!ViewModel.SelectionEnabled) return false;
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            ViewModel.SelectRangeCommand.Execute(new RangeSelectionRequest(item, (modifiers & ModifierKeys.Control) != 0));
            return suppressTileOpen = true;
        }
        if ((modifiers & ModifierKeys.Control) != 0 && !checkbox)
        {
            item.IsSelected = !item.IsSelected;
            return suppressTileOpen = true;
        }
        return false;
    }
}
