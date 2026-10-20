using System.Windows;
using System.Windows.Controls;

namespace CardViewer;

public sealed class DeleteConfirmationWindow : Window
{
    internal Button CancelButton { get; }
    internal Button DeleteButton { get; }
    internal ListBox FileList { get; }

    public DeleteConfirmationWindow(DeleteConfirmationViewModel viewModel)
    {
        AppTheme.Apply(this);
        DataContext = viewModel;
        void CloseDialog(bool confirmed) => DialogResult = confirmed;
        viewModel.CloseRequested += CloseDialog;
        Closed += (_, _) => viewModel.CloseRequested -= CloseDialog;
        Title = "Are you sure you want to delete these images?";
        Width = 660;
        Height = 430;
        MinWidth = 480;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(20) };
        var heading = new TextBlock
        {
            Text = viewModel.Heading,
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var explanation = new TextBlock
        {
            Text = "The selected images and their paired RAW files will be deleted PERMANENTLY. This cannot be undone.",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 139, 148)),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        DockPanel.SetDock(explanation, Dock.Top);
        panel.Children.Add(explanation);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        CancelButton = new Button { Content = "Cancel", Command = viewModel.CancelCommand, IsCancel = true, IsDefault = true, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(0, 0, 10, 0) };
        DeleteButton = new Button { Content = viewModel.ConfirmLabel, Command = viewModel.ConfirmCommand, Padding = new Thickness(18, 9, 18, 9) };
        DeleteButton.SetResourceReference(FrameworkElement.StyleProperty, "DangerButtonStyle");
        buttons.Children.Add(CancelButton);
        buttons.Children.Add(DeleteButton);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        FileList = new ListBox { ItemsSource = viewModel.Groups };
        FileList.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid Margin="8" ToolTip="{Binding Paths}" AutomationProperties.Name="{Binding Description}">
                <Grid.ColumnDefinitions><ColumnDefinition Width="36"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                <TextBlock Text="{Binding Icon}" FontFamily="Segoe Fluent Icons" FontSize="22" Foreground="#9CCAFF" VerticalAlignment="Center"/>
                <StackPanel Grid.Column="1" Margin="0,0,16,0">
                  <TextBlock Text="{Binding Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                  <TextBlock Text="{Binding DisplayPath}" Foreground="#AEBBCB" FontSize="11" TextTrimming="CharacterEllipsis"/>
                </StackPanel>
                <ItemsControl Grid.Column="2" ItemsSource="{Binding Tags}" VerticalAlignment="Center">
                  <ItemsControl.ItemsPanel><ItemsPanelTemplate><StackPanel Orientation="Horizontal"/></ItemsPanelTemplate></ItemsControl.ItemsPanel>
                  <ItemsControl.ItemTemplate><DataTemplate>
                    <Border CornerRadius="5" Background="{Binding Background}" Padding="8,4" Margin="3,0,0,0">
                      <TextBlock Text="{Binding Name}" Foreground="{Binding Foreground}" FontSize="11" FontWeight="SemiBold"/>
                    </Border>
                  </DataTemplate></ItemsControl.ItemTemplate>
                </ItemsControl>
              </Grid>
            </DataTemplate>
            """);
        FileList.ItemContainerStyle = new Style(typeof(ListBoxItem));
        FileList.ItemContainerStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        ScrollViewer.SetHorizontalScrollBarVisibility(FileList, ScrollBarVisibility.Auto);
        panel.Children.Add(FileList);
        Content = panel;
        Loaded += (_, _) => CancelButton.Focus();
    }
}
