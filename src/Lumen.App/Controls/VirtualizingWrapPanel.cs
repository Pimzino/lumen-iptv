using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Lumen.App.Controls;

/// <summary>
/// A wrap panel that virtualizes: it realizes only the item containers for the visible rows
/// (plus a small cache), so poster grids stay smooth with thousands of movies. Items are laid
/// out on a uniform grid sized by <see cref="ItemWidth"/>/<see cref="ItemHeight"/>; the column
/// count adapts to the available width.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    private Size _extent;
    private Size _viewport;
    private double _verticalOffset;
    private int _columns = 1;

    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(150.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(280.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // WPF connects an items host to its ItemContainerGenerator lazily, on the first
        // InternalChildren access. Touch it before anything else: a first measure that skips
        // this (e.g. items still loading) would leave the panel unsubscribed from the
        // generator's ItemsChanged, so items added afterwards would never invalidate measure
        // and the grid would stay blank until an unrelated layout pass (window resize).
        _ = InternalChildren;

        var itemCount = GetItemCount(this);
        _columns = Math.Max(1, (int)(availableSize.Width / ItemWidth));
        var rows = (int)Math.Ceiling(itemCount / (double)_columns);

        UpdateViewport(availableSize);
        var extentHeight = rows * ItemHeight;
        UpdateExtent(new Size(availableSize.Width, extentHeight));

        RealizeVisibleItems(itemCount);
        return new Size(
            double.IsInfinity(availableSize.Width) ? _columns * ItemWidth : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? extentHeight : availableSize.Height);
    }

    private void RealizeVisibleItems(int itemCount)
    {
        // Until this panel is wired up as the ItemsHost, its generator is null (a transient state
        // during template application). Bail rather than dereference it — measure runs again once
        // the generator exists.
        var generator = ItemContainerGenerator;
        if (generator is null)
        {
            return;
        }

        if (itemCount == 0)
        {
            CleanupChildren(-1, -1);
            return;
        }

        var firstRow = Math.Max(0, (int)(_verticalOffset / ItemHeight) - 1);
        var lastRow = (int)((_verticalOffset + _viewport.Height) / ItemHeight) + 1;
        var firstIndex = firstRow * _columns;
        var lastIndex = Math.Min(itemCount - 1, (lastRow + 1) * _columns - 1);

        var startPos = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (var i = firstIndex; i <= lastIndex; i++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var isNew);
                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }

                    generator.PrepareItemContainer(child);
                }

                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }

        CleanupChildren(firstIndex, lastIndex);
    }

    private void CleanupChildren(int firstIndex, int lastIndex)
    {
        var generator = ItemContainerGenerator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(position);
            if (itemIndex < firstIndex || itemIndex > lastIndex)
            {
                generator.Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        if (generator is null)
        {
            return finalSize;
        }

        foreach (UIElement child in InternalChildren)
        {
            var itemIndex = generator.IndexFromGeneratorPosition(
                new GeneratorPosition(InternalChildren.IndexOf(child), 0));
            if (itemIndex < 0)
            {
                continue;
            }

            var row = itemIndex / _columns;
            var column = itemIndex % _columns;
            var x = column * ItemWidth;
            var y = row * ItemHeight - _verticalOffset;
            child.Arrange(new Rect(x, y, ItemWidth, ItemHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        switch (args.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                // Remove only the realized containers the generator dropped for the affected
                // items (ItemUICount is 0 when the item wasn't realized). Removing all children
                // here would desync from the generator and blank the panel on the next measure.
                if (args.ItemUICount > 0)
                {
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                }

                break;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                // The generator clears its own realized map on reset; drop all containers to match.
                RemoveInternalChildRange(0, InternalChildren.Count);
                break;
        }

        InvalidateMeasure();
    }

    private static int GetItemCount(DependencyObject panel)
    {
        var itemsControl = ItemsControl.GetItemsOwner(panel);
        return itemsControl?.Items.Count ?? 0;
    }

    // ----- IScrollInfo -----

    public bool CanVerticallyScroll { get; set; } = true;

    public bool CanHorizontallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => 0;

    public double VerticalOffset => _verticalOffset;

    public ScrollViewer? ScrollOwner { get; set; }

    private void UpdateViewport(Size size)
    {
        if (_viewport != size)
        {
            _viewport = size;
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    private void UpdateExtent(Size extent)
    {
        if (_extent != extent)
        {
            _extent = extent;
            ScrollOwner?.InvalidateScrollInfo();
        }

        _verticalOffset = Math.Max(0, Math.Min(_verticalOffset, Math.Max(0, _extent.Height - _viewport.Height)));
    }

    public void LineUp() => SetVerticalOffset(_verticalOffset - 48);

    public void LineDown() => SetVerticalOffset(_verticalOffset + 48);

    public void LineLeft()
    {
    }

    public void LineRight()
    {
    }

    public void PageUp() => SetVerticalOffset(_verticalOffset - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_verticalOffset + _viewport.Height);

    public void PageLeft()
    {
    }

    public void PageRight()
    {
    }

    public void MouseWheelUp() => SetVerticalOffset(_verticalOffset - ItemHeight);

    public void MouseWheelDown() => SetVerticalOffset(_verticalOffset + ItemHeight);

    public void MouseWheelLeft()
    {
    }

    public void MouseWheelRight()
    {
    }

    public void SetHorizontalOffset(double offset)
    {
    }

    public void SetVerticalOffset(double offset)
    {
        var clamped = Math.Max(0, Math.Min(offset, Math.Max(0, _extent.Height - _viewport.Height)));
        if (Math.Abs(clamped - _verticalOffset) > 0.01)
        {
            _verticalOffset = clamped;
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateMeasure();
        }
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        // Bring a focused container into view (keyboard navigation).
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (ReferenceEquals(InternalChildren[i], visual))
            {
                var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                var row = itemIndex / _columns;
                var top = row * ItemHeight;
                var bottom = top + ItemHeight;
                if (top < _verticalOffset)
                {
                    SetVerticalOffset(top);
                }
                else if (bottom > _verticalOffset + _viewport.Height)
                {
                    SetVerticalOffset(bottom - _viewport.Height);
                }

                break;
            }
        }

        return rectangle;
    }
}
