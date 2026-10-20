using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CardViewer;

// Runs on the existing STA test dispatcher. Hidden windows exercise real WPF
// templates, bindings and command routing without sending input to the desktop.

internal static partial class UiTests
{
    private static async Task VerifyScrollbarPagingAndDraggingAsync(Action<bool, string> check)
    {
        var panel = new StackPanel();
        var vertical = new ScrollBar { Orientation = Orientation.Vertical, Height = 200, Minimum = 0, Maximum = 100, Value = 10, ViewportSize = 20, LargeChange = 10 };
        var horizontal = new ScrollBar { Orientation = Orientation.Horizontal, Width = 200, Minimum = 0, Maximum = 100, Value = 10, ViewportSize = 20, LargeChange = 10 };
        panel.Children.Add(vertical);
        panel.Children.Add(horizontal);
        var window = new Window { Content = panel, Width = 300, Height = 350 };
        AppTheme.Apply(window);
        HideFromDesktop(window);
        try
        {
            window.Show();
            await FlushAsync();
            foreach (var bar in new[] { vertical, horizontal })
            {
                bar.ApplyTemplate();
                var track = (Track)bar.Template.FindName("PART_Track", bar);
                track.Thumb.ApplyTemplate();
                check(((Border)bar.Template.FindName("Rail", bar)).CornerRadius.TopLeft == 6 && track.Thumb.Template.FindName("Grip", track.Thumb) != null,
                    "Rounded scrollbar track and thumb render for " + bar.Orientation);
                double before = bar.Value;
                Click(track.IncreaseRepeatButton);
                check(bar.Value > before, "Scrollbar page command works for " + bar.Orientation);
                before = bar.Value;
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(bar.Orientation == Orientation.Horizontal ? 10 : 0, bar.Orientation == Orientation.Vertical ? 10 : 0) { RoutedEvent = Thumb.DragDeltaEvent });
                check(bar.Value > before, "Scrollbar thumb drag works for " + bar.Orientation);
            }
        }
        finally { window.Close(); }
    }
}
