using System.Windows;
using System.Windows.Controls;

namespace CardViewer;

internal sealed class ErrorDetailsWindow : Window
{
    internal ErrorDetailsWindow(string title, string details)
    {
        AppTheme.Apply(this);
        Title = title;
        Width = 660;
        Height = 400;
        MinWidth = 420;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new DockPanel { Margin = new Thickness(20) };
        var heading = new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 90, Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        panel.Children.Add(new TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = panel;
    }
}
