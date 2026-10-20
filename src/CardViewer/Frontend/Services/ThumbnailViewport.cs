using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CardViewer;

// Loaded/Unloaded alone are insufficient when WPF keeps recycled visuals alive.
// Reconcile the realized tiles against the actual scroll viewport after layout.
internal sealed class ThumbnailViewport : IDisposable
{
    private readonly ListBox list;
    private readonly Action<ImageItem> show;
    private readonly Action<ImageItem> hide;
    private readonly Dictionary<FrameworkElement, ImageItem> tiles = [];
    private readonly HashSet<ImageItem> visible = [];
    private readonly ScrollChangedEventHandler scrollChanged;
    private bool queued;
    private bool disposed;

    internal ThumbnailViewport(ListBox list, Action<ImageItem> show, Action<ImageItem> hide)
    {
        this.list = list;
        this.show = show;
        this.hide = hide;
        scrollChanged = (_, _) => QueueUpdate();
        list.AddHandler(ScrollViewer.ScrollChangedEvent, scrollChanged);
    }

    internal void Track(FrameworkElement tile)
    {
        if (tile.DataContext is ImageItem item)
            tiles[tile] = item;
        QueueUpdate();
    }

    internal void Untrack(FrameworkElement tile)
    {
        if (tiles.Remove(tile, out var item) && visible.Remove(item))
            hide(item);
        QueueUpdate();
    }

    internal void ChangeItem(FrameworkElement tile)
    {
        Untrack(tile);
        if (tile.IsLoaded)
            Track(tile);
    }

    private void QueueUpdate()
    {
        if (queued || disposed)
            return;
        queued = true;
        list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Update));
    }

    private void Update()
    {
        queued = false;
        if (disposed)
            return;
        var current = new HashSet<ImageItem>();
        var viewport = new Rect(list.RenderSize);
        foreach (var (tile, item) in tiles.ToArray())
        {
            if (!tile.IsDescendantOf(list))
            {
                tiles.Remove(tile);
                continue;
            }
            if (tile.IsVisible && tile.ActualWidth > 0 && tile.ActualHeight > 0 &&
                tile.TransformToAncestor(list).TransformBounds(new Rect(tile.RenderSize)).IntersectsWith(viewport))
                current.Add(item);
        }
        foreach (var item in visible.Except(current).ToArray())
            hide(item);
        foreach (var item in current.Except(visible))
            show(item);
        visible.Clear();
        visible.UnionWith(current);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        list.RemoveHandler(ScrollViewer.ScrollChangedEvent, scrollChanged);
        foreach (var item in visible)
            hide(item);
        visible.Clear();
        tiles.Clear();
    }
}
