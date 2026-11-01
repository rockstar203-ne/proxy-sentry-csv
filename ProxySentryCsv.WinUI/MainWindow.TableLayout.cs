using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ProxySentryCsv;

public sealed partial class MainWindow
{
    private readonly List<WeakReference<FrameworkElement>> _pinnedActions = [];

    private void PinnedActions_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if (!_pinnedActions.Any(item => item.TryGetTarget(out var target) && target == element))
            _pinnedActions.Add(new(element));
        UpdatePinnedActions();
    }

    private void ProxyTableScroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) => UpdatePinnedActions();
    private void ProxyTableScroller_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePinnedActions();

    private void UpdatePinnedActions()
    {
        if (ProxyTableScroller is null || ProxyHeaderGrid is null) return;
        var offset = Math.Min(0, ProxyTableScroller.HorizontalOffset + ProxyTableScroller.ViewportWidth - ProxyHeaderGrid.ActualWidth);
        if (PinnedActionsHeader is not null) PinnedActionsHeader.RenderTransform = new TranslateTransform { X = offset };
        _pinnedActions.RemoveAll(reference =>
        {
            if (!reference.TryGetTarget(out var element)) return true;
            element.RenderTransform = new TranslateTransform { X = offset };
            return false;
        });
    }

    private void ProfileFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_workspaceReady) return;
        RefreshVisibleProxies();
        SaveState();
    }
}
