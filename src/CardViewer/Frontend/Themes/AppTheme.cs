using System.Windows;

namespace CardViewer;

internal static class AppTheme
{
    internal static void Apply(Window window)
    {
        // Each window owns its resources, including windows created by STA tests.
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/CardViewer;component/Frontend/Themes/Controls.xaml", UriKind.Relative)
        });
        window.SetResourceReference(FrameworkElement.StyleProperty, "AppWindowStyle");
        window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/CardViewer;component/Assets/CardViewer.ico"));
    }
}
