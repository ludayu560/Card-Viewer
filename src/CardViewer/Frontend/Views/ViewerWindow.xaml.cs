using System.Windows;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CardViewer;

public partial class ViewerWindow : Window
{
    public ViewerViewModel ViewModel { get; }
    public Task Completion => ViewModel.Completion;
    private Size? fittedImage;
    private readonly bool deferDisplay;

    public ViewerWindow(ViewerViewModel viewModel, bool deferDisplay = false)
    {
        this.deferDisplay = deferDisplay;
        InitializeComponent();
        if (deferDisplay) Opacity = 0;
        AppTheme.Apply(this);
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += Close;
        Loaded += (_, _) => { viewModel.LoadCommand.Execute(null); QueueFit(); };
        viewModel.PropertyChanged += ImageChanged;
        Closed += (_, _) =>
        {
            viewModel.CloseRequested -= Close;
            viewModel.PropertyChanged -= ImageChanged;
            viewModel.Dispose();
        };
    }

    private void ImageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewerViewModel.ImageDimensions) && ViewModel.ImageDimensions is { } dimensions &&
            fittedImage != dimensions)
        {
            fittedImage = dimensions;
            QueueFit();
        }
        if (e.PropertyName == nameof(ViewerViewModel.IsLoading) && !ViewModel.IsLoading && ViewModel.ImageDimensions == null)
            Reveal();
    }

    private void QueueFit() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
    {
        if (IsLoaded && WindowState == WindowState.Normal) FitToImage();
    }));

    private void Reveal() => Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => { if (deferDisplay && IsLoaded) Opacity = 1; }));

    private void FitToImage()
    {
        if (ViewModel.ImageDimensions is not { } dimensions) return;
        var area = MonitorWorkArea();
        double chromeHeight = Math.Max(0, ActualHeight - ((FrameworkElement)Content).ActualHeight);
        double chromeWidth = Math.Max(0, ActualWidth - ((FrameworkElement)Content).ActualWidth);
        var padding = new Size(24 + chromeWidth, ViewerHeader.ActualHeight + 34 + chromeHeight);
        var size = FitSize(dimensions, area.Size, padding, new(MinWidth, MinHeight));
        double centerX = Left + ActualWidth / 2, centerY = Top + ActualHeight / 2;
        Width = size.Width;
        Height = size.Height;
        Left = Math.Clamp(centerX - Width / 2, area.Left, area.Right - Width);
        Top = Math.Clamp(centerY - Height / 2, area.Top, area.Bottom - Height);
        Reveal();
    }

    internal static Size FitSize(Size image, Size screen, Size padding, Size minimum)
    {
        double maxWidth = Math.Max(1, screen.Width - 32), maxHeight = Math.Max(1, screen.Height - 32);
        double scale = Math.Min(1, Math.Min(Math.Max(1, maxWidth - padding.Width) / image.Width, Math.Max(1, maxHeight - padding.Height) / image.Height));
        return new(Math.Min(maxWidth, Math.Max(minimum.Width, image.Width * scale + padding.Width)),
            Math.Min(maxHeight, Math.Max(minimum.Height, image.Height * scale + padding.Height)));
    }

    private Rect MonitorWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return SystemParameters.WorkArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        return new Rect(transform.Transform(new Point(info.Work.Left, info.Work.Top)), transform.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}

