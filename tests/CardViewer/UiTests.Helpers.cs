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
    private static void HideFromDesktop(Window window)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Opacity = 0;
        window.Left = -20000;
        window.Top = -20000;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
    }

    private static async Task FlushAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(3))
        {
            // Loaded/Unloaded notifications can wait for the next render-cycle timer.
            await Task.Delay(10);
            await FlushAsync();
        }
    }

    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Children<T>(child))
                yield return nested;
        }
    }

    private static bool Click(ButtonBase button)
    {
        if (!button.IsEnabled)
            return false;
        // Raising Click alone bypasses ButtonBase's command execution and toggling.
        // Invoke its actual virtual click implementation, with no OS mouse injection.
        button.GetType().GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
        return true;
    }

    private static void MouseUp(UIElement element) => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
    {
        RoutedEvent = UIElement.MouseLeftButtonUpEvent
    });

    private static void PressKey(Window window, Key key)
    {
        var binding = window.InputBindings.OfType<KeyBinding>().Single(input => input.Key == key);
        if (binding.Command.CanExecute(binding.CommandParameter))
            binding.Command.Execute(binding.CommandParameter);
    }

    private static BitmapSource Pixel(byte shade)
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { shade, shade, shade, 255 }, 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static bool? Modal(Window window, Action inspectAndClick)
    {
        HideFromDesktop(window);
        Exception? failure = null;
        bool timedOut = false;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timeout.Tick += (_, _) => { timedOut = true; window.Close(); };
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try { inspectAndClick(); }
            catch (Exception ex) { failure = ex; window.Close(); }
        }));
        timeout.Start();
        bool? result;
        try { result = window.ShowDialog(); }
        finally { timeout.Stop(); window.Close(); }
        if (failure != null)
            throw new InvalidOperationException("UI dialog operation failed.", failure);
        if (timedOut)
            throw new TimeoutException("UI dialog did not close.");
        return result;
    }

    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
    }
}
