using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Lumen.Core.Models;

namespace Lumen.App.Controls.Epg;

/// <summary>
/// Custom two-axis virtualized EPG timeline. Channels run vertically, time horizontally.
/// Rather than realize a visual per programme (an ItemsControl-in-ItemsControl would choke on
/// 500 × 7 days), the panel implements <see cref="IScrollInfo"/> and draws only the programmes
/// intersecting the current viewport directly with a <see cref="DrawingContext"/> — so panning
/// stays smooth regardless of catalog size. The time ruler (top) and channel gutter (left) are
/// pinned; the grid scrolls beneath them.
/// </summary>
public sealed class EpgGuidePanel : FrameworkElement, IScrollInfo
{
    private const double RowHeight = 64;
    private const double RulerHeight = 40;
    private const double GutterWidth = 200;
    private const double MinutesPerGrid = 30;
    private const double LineScrollMinutes = 30;

    private double _horizontalOffset;
    private double _verticalOffset;
    private Size _viewport;
    private Size _extent;

    // Cached brushes/pens (frozen). Populated from theme resources on first render.
    private bool _resourcesReady;
    private Brush _rowEvenBrush = Brushes.Transparent;
    private Brush _rowOddBrush = Brushes.Transparent;
    private Brush _blockBrush = Brushes.Gray;
    private Brush _blockNowBrush = Brushes.DimGray;
    private Brush _gutterBrush = Brushes.Black;
    private Brush _rulerBrush = Brushes.Black;
    private Brush _textPrimary = Brushes.White;
    private Brush _textSecondary = Brushes.Gray;
    private Brush _accent = Brushes.DodgerBlue;
    private Pen _gridPen = new(Brushes.Gray, 1);
    private Pen _hourPen = new(Brushes.Gray, 1);
    private Pen _nowPen = new(Brushes.Red, 2);
    private Pen _strokePen = new(Brushes.Gray, 1);
    private Typeface _typeface = new("Segoe UI");

    public EpgGuidePanel()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    // ----- bound data -----

    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IReadOnlyList<GuideRow>), typeof(EpgGuidePanel),
        new FrameworkPropertyMetadata(Array.Empty<GuideRow>(), FrameworkPropertyMetadataOptions.AffectsMeasure, OnDataChanged));

    public IReadOnlyList<GuideRow> Rows
    {
        get => (IReadOnlyList<GuideRow>)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public static readonly DependencyProperty TimelineStartProperty = DependencyProperty.Register(
        nameof(TimelineStart), typeof(DateTimeOffset), typeof(EpgGuidePanel),
        new FrameworkPropertyMetadata(DateTimeOffset.UnixEpoch, FrameworkPropertyMetadataOptions.AffectsMeasure, OnDataChanged));

    public DateTimeOffset TimelineStart
    {
        get => (DateTimeOffset)GetValue(TimelineStartProperty);
        set => SetValue(TimelineStartProperty, value);
    }

    public static readonly DependencyProperty TimelineEndProperty = DependencyProperty.Register(
        nameof(TimelineEnd), typeof(DateTimeOffset), typeof(EpgGuidePanel),
        new FrameworkPropertyMetadata(DateTimeOffset.UnixEpoch.AddDays(1), FrameworkPropertyMetadataOptions.AffectsMeasure, OnDataChanged));

    public DateTimeOffset TimelineEnd
    {
        get => (DateTimeOffset)GetValue(TimelineEndProperty);
        set => SetValue(TimelineEndProperty, value);
    }

    public static readonly DependencyProperty NowProperty = DependencyProperty.Register(
        nameof(Now), typeof(DateTimeOffset), typeof(EpgGuidePanel),
        new FrameworkPropertyMetadata(DateTimeOffset.UnixEpoch, FrameworkPropertyMetadataOptions.AffectsRender));

    public DateTimeOffset Now
    {
        get => (DateTimeOffset)GetValue(NowProperty);
        set => SetValue(NowProperty, value);
    }

    public static readonly DependencyProperty PixelsPerMinuteProperty = DependencyProperty.Register(
        nameof(PixelsPerMinute), typeof(double), typeof(EpgGuidePanel),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnDataChanged));

    public double PixelsPerMinute
    {
        get => (double)GetValue(PixelsPerMinuteProperty);
        set => SetValue(PixelsPerMinuteProperty, value);
    }

    /// <summary>Raised when the user clicks a programme block.</summary>
    public event EventHandler<ProgrammeActivatedEventArgs>? ProgrammeActivated;

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (EpgGuidePanel)d;
        panel.UpdateExtent();
        panel.InvalidateVisual();
    }

    // ----- geometry helpers -----

    private double TotalMinutes => Math.Max(1, (TimelineEnd - TimelineStart).TotalMinutes);

    private double TimelineWidth => TotalMinutes * PixelsPerMinute;

    private double ContentHeight => Rows.Count * RowHeight;

    private double MinuteToX(double minutes) => GutterWidth + minutes * PixelsPerMinute - _horizontalOffset;

    // ----- layout -----

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? TimelineWidth + GutterWidth : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? ContentHeight + RulerHeight : availableSize.Height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_viewport != finalSize)
        {
            _viewport = finalSize;
            UpdateExtent();
        }

        return finalSize;
    }

    private void UpdateExtent()
    {
        // The ruler/gutter are pinned, so only the grid content contributes to the extent.
        var extent = new Size(TimelineWidth + GutterWidth, ContentHeight + RulerHeight);
        if (extent != _extent)
        {
            _extent = extent;
            ScrollOwner?.InvalidateScrollInfo();
        }

        // Clamp offsets when content shrinks.
        _horizontalOffset = Clamp(_horizontalOffset, 0, Math.Max(0, _extent.Width - _viewport.Width));
        _verticalOffset = Clamp(_verticalOffset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();
    }

    // ----- rendering -----

    protected override void OnRender(DrawingContext dc)
    {
        EnsureResources();

        var rows = Rows;
        if (_viewport.Width <= 0 || _viewport.Height <= 0)
        {
            return;
        }

        // Background.
        dc.DrawRectangle(_rulerBrush, null, new Rect(0, 0, _viewport.Width, _viewport.Height));

        var gridTop = RulerHeight;
        var gridLeft = GutterWidth;
        var gridWidth = Math.Max(0, _viewport.Width - gridLeft);
        var gridHeight = Math.Max(0, _viewport.Height - gridTop);

        // Visible channel range (vertical virtualization).
        var firstRow = Math.Max(0, (int)(_verticalOffset / RowHeight));
        var lastRow = Math.Min(rows.Count - 1, (int)((_verticalOffset + gridHeight) / RowHeight));

        // Visible time range (horizontal virtualization).
        var visibleStartMin = _horizontalOffset / PixelsPerMinute;
        var visibleEndMin = (_horizontalOffset + gridWidth) / PixelsPerMinute;

        // --- grid clip so blocks/gridlines never bleed into the pinned bands ---
        dc.PushClip(new RectangleGeometry(new Rect(gridLeft, gridTop, gridWidth, gridHeight)));

        // Row backgrounds + separators.
        for (var r = firstRow; r <= lastRow; r++)
        {
            var y = gridTop + r * RowHeight - _verticalOffset;
            dc.DrawRectangle(r % 2 == 0 ? _rowEvenBrush : _rowOddBrush, null,
                new Rect(gridLeft, y, gridWidth, RowHeight));
            dc.DrawLine(_strokePen, new Point(gridLeft, y + RowHeight), new Point(_viewport.Width, y + RowHeight));
        }

        // 30-minute vertical gridlines.
        var firstGrid = Math.Floor(visibleStartMin / MinutesPerGrid) * MinutesPerGrid;
        for (var minute = firstGrid; minute <= visibleEndMin; minute += MinutesPerGrid)
        {
            var x = MinuteToX(minute);
            if (x < gridLeft)
            {
                continue;
            }

            var pen = (minute % 60 == 0) ? _hourPen : _gridPen;
            dc.DrawLine(pen, new Point(x, gridTop), new Point(x, _viewport.Height));
        }

        // Programme blocks.
        for (var r = firstRow; r <= lastRow; r++)
        {
            DrawRowProgrammes(dc, rows[r], r, visibleStartMin, visibleEndMin, gridTop, gridLeft);
        }

        // Now-line.
        var nowMinutes = (Now - TimelineStart).TotalMinutes;
        if (nowMinutes >= visibleStartMin && nowMinutes <= visibleEndMin)
        {
            var x = MinuteToX(nowMinutes);
            dc.DrawLine(_nowPen, new Point(x, gridTop), new Point(x, _viewport.Height));
        }

        dc.Pop(); // grid clip

        // --- pinned channel gutter (left) ---
        dc.DrawRectangle(_gutterBrush, null, new Rect(0, gridTop, gridLeft, gridHeight));
        dc.PushClip(new RectangleGeometry(new Rect(0, gridTop, gridLeft, gridHeight)));
        for (var r = firstRow; r <= lastRow; r++)
        {
            var y = gridTop + r * RowHeight - _verticalOffset;
            var row = rows[r];
            DrawText(dc, row.Channel.Name, _textPrimary, 13, FontWeights.SemiBold,
                new Rect(16, y + 12, gridLeft - 24, 18));
            if (row.Channel.Number is { } number)
            {
                DrawText(dc, number.ToString(CultureInfo.InvariantCulture), _textSecondary, 11, FontWeights.Normal,
                    new Rect(16, y + 34, gridLeft - 24, 16));
            }

            dc.DrawLine(_strokePen, new Point(0, y + RowHeight), new Point(gridLeft, y + RowHeight));
        }

        dc.Pop();
        dc.DrawLine(_strokePen, new Point(gridLeft, gridTop), new Point(gridLeft, _viewport.Height));

        // --- pinned time ruler (top) ---
        dc.DrawRectangle(_rulerBrush, null, new Rect(0, 0, _viewport.Width, gridTop));
        dc.PushClip(new RectangleGeometry(new Rect(gridLeft, 0, gridWidth, gridTop)));
        for (var minute = firstGrid; minute <= visibleEndMin; minute += MinutesPerGrid)
        {
            var x = MinuteToX(minute);
            if (x < gridLeft)
            {
                continue;
            }

            var label = TimelineStart.AddMinutes(minute).ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
            DrawText(dc, label, _textSecondary, 12, FontWeights.Normal, new Rect(x + 6, 12, 60, 16));
        }

        dc.Pop();
        dc.DrawLine(_strokePen, new Point(0, gridTop), new Point(_viewport.Width, gridTop));

        // corner cover
        dc.DrawRectangle(_gutterBrush, null, new Rect(0, 0, gridLeft, gridTop));
    }

    private void DrawRowProgrammes(
        DrawingContext dc, GuideRow row, int rowIndex,
        double visibleStartMin, double visibleEndMin, double gridTop, double gridLeft)
    {
        var y = gridTop + rowIndex * RowHeight - _verticalOffset;
        var programmes = row.Programmes;
        if (programmes.Count == 0)
        {
            DrawText(dc, Lumen.App.Resources.Strings.LiveTv_NoGuideData, _textSecondary, 12, FontWeights.Normal,
                new Rect(gridLeft + 12, y + 22, 240, 16));
            return;
        }

        var startUnix = TimelineStart.ToUnixTimeSeconds();
        var nowUnix = Now.ToUnixTimeSeconds();

        // Binary search to the first programme that ends after the visible window start.
        var visibleStartUnix = startUnix + (long)(visibleStartMin * 60);
        var index = LowerBound(programmes, visibleStartUnix);

        for (var i = index; i < programmes.Count; i++)
        {
            var programme = programmes[i];
            var startMin = (programme.StartUtc - startUnix) / 60.0;
            if (startMin > visibleEndMin)
            {
                break;
            }

            var endMin = (programme.StopUtc - startUnix) / 60.0;
            var left = MinuteToX(startMin);
            var right = MinuteToX(endMin);
            var width = right - left;
            if (width < 1)
            {
                continue;
            }

            var blockLeft = Math.Max(left, gridLeft);
            var blockWidth = right - blockLeft - 2;
            if (blockWidth < 1)
            {
                continue;
            }

            var isNow = nowUnix >= programme.StartUtc && nowUnix < programme.StopUtc;
            var rect = new Rect(blockLeft + 1, y + 4, blockWidth, RowHeight - 8);
            dc.DrawRoundedRectangle(isNow ? _blockNowBrush : _blockBrush, null, rect, 6, 6);
            if (isNow)
            {
                dc.DrawRoundedRectangle(null, new Pen(_accent, 1), rect, 6, 6);
            }

            // Clip title/time to the block.
            dc.PushClip(new RectangleGeometry(rect));
            DrawText(dc, programme.Title, _textPrimary, 12, FontWeights.SemiBold,
                new Rect(rect.X + 8, rect.Y + 6, Math.Max(0, rect.Width - 12), 16));
            if (rect.Width > 90)
            {
                var timeLabel = programme.Start.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
                DrawText(dc, timeLabel, _textSecondary, 11, FontWeights.Normal,
                    new Rect(rect.X + 8, rect.Y + 26, Math.Max(0, rect.Width - 12), 14));
            }

            dc.Pop();
        }
    }

    private void DrawText(DrawingContext dc, string text, Brush brush, double size, FontWeight weight, Rect bounds)
    {
        if (bounds.Width < 4 || string.IsNullOrEmpty(text))
        {
            return;
        }

        var formatted = new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(_typeface.FontFamily, FontStyles.Normal, weight, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = bounds.Width,
            MaxTextHeight = bounds.Height + 4,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1,
        };
        dc.DrawText(formatted, new Point(bounds.X, bounds.Y));
    }

    /// <summary>First programme index whose stop is strictly after <paramref name="unix"/>.</summary>
    private static int LowerBound(IReadOnlyList<Programme> programmes, long unix)
    {
        int lo = 0, hi = programmes.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (programmes[mid].StopUtc <= unix)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    // ----- interaction -----

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var point = e.GetPosition(this);
        if (point.X < GutterWidth || point.Y < RulerHeight)
        {
            return;
        }

        var rowIndex = (int)((point.Y - RulerHeight + _verticalOffset) / RowHeight);
        if (rowIndex < 0 || rowIndex >= Rows.Count)
        {
            return;
        }

        var minute = (point.X - GutterWidth + _horizontalOffset) / PixelsPerMinute;
        var unix = TimelineStart.ToUnixTimeSeconds() + (long)(minute * 60);

        var row = Rows[rowIndex];
        foreach (var programme in row.Programmes)
        {
            if (programme.StartUtc <= unix && unix < programme.StopUtc)
            {
                ProgrammeActivated?.Invoke(this, new ProgrammeActivatedEventArgs(row, programme));
                return;
            }
        }
    }

    /// <summary>Scrolls so the given moment sits near the left of the grid.</summary>
    public void ScrollToTime(DateTimeOffset moment)
    {
        var minutes = (moment - TimelineStart).TotalMinutes;
        var target = minutes * PixelsPerMinute - 120; // small lead-in
        SetHorizontalOffset(target);
    }

    private void EnsureResources()
    {
        if (_resourcesReady)
        {
            return;
        }

        Brush B(string key, Brush fallback) =>
            TryFindResource(key) is Brush brush ? brush : fallback;

        _rowEvenBrush = B("Lumen.Brush.Bg.Base", Brushes.Black);
        _rowOddBrush = B("Lumen.Brush.Bg.Raised", Brushes.DimGray);
        _blockBrush = B("Lumen.Brush.Control.Rest", Brushes.Gray);
        _blockNowBrush = B("Lumen.Brush.Accent.Subtle", Brushes.SlateGray);
        _gutterBrush = B("Lumen.Brush.Bg.Raised", Brushes.Black);
        _rulerBrush = B("Lumen.Brush.Bg.Base", Brushes.Black);
        _textPrimary = B("Lumen.Brush.Text.Primary", Brushes.White);
        _textSecondary = B("Lumen.Brush.Text.Secondary", Brushes.Gray);
        _accent = B("Lumen.Brush.Accent", Brushes.DodgerBlue);

        var stroke = B("Lumen.Brush.Stroke.Subtle", Brushes.Gray);
        _strokePen = new Pen(stroke, 1);
        _strokePen.Freeze();
        _gridPen = new Pen(stroke, 1);
        _gridPen.Freeze();
        _hourPen = new Pen(B("Lumen.Brush.Stroke.Strong", Brushes.Gray), 1);
        _hourPen.Freeze();
        _nowPen = new Pen(B("Lumen.Brush.Live", Brushes.Red), 2);
        _nowPen.Freeze();

        if (TryFindResource("Lumen.Font.Text") is FontFamily family)
        {
            _typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        }

        _resourcesReady = true;
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;

    // ----- IScrollInfo -----

    public bool CanVerticallyScroll { get; set; } = true;

    public bool CanHorizontallyScroll { get; set; } = true;

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => _horizontalOffset;

    public double VerticalOffset => _verticalOffset;

    public ScrollViewer? ScrollOwner { get; set; }

    private double LineScrollPixels => LineScrollMinutes * PixelsPerMinute;

    public void LineUp() => SetVerticalOffset(_verticalOffset - RowHeight);

    public void LineDown() => SetVerticalOffset(_verticalOffset + RowHeight);

    public void LineLeft() => SetHorizontalOffset(_horizontalOffset - LineScrollPixels);

    public void LineRight() => SetHorizontalOffset(_horizontalOffset + LineScrollPixels);

    public void PageUp() => SetVerticalOffset(_verticalOffset - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_verticalOffset + _viewport.Height);

    public void PageLeft() => SetHorizontalOffset(_horizontalOffset - _viewport.Width);

    public void PageRight() => SetHorizontalOffset(_horizontalOffset + _viewport.Width);

    public void MouseWheelUp() => SetVerticalOffset(_verticalOffset - RowHeight * 2);

    public void MouseWheelDown() => SetVerticalOffset(_verticalOffset + RowHeight * 2);

    public void MouseWheelLeft() => SetHorizontalOffset(_horizontalOffset - LineScrollPixels);

    public void MouseWheelRight() => SetHorizontalOffset(_horizontalOffset + LineScrollPixels);

    public void SetHorizontalOffset(double offset)
    {
        var clamped = Clamp(offset, 0, Math.Max(0, _extent.Width - _viewport.Width));
        if (Math.Abs(clamped - _horizontalOffset) > 0.01)
        {
            _horizontalOffset = clamped;
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateVisual();
        }
    }

    public void SetVerticalOffset(double offset)
    {
        var clamped = Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(clamped - _verticalOffset) > 0.01)
        {
            _verticalOffset = clamped;
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateVisual();
        }
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}

/// <summary>Event args for a clicked programme block.</summary>
public sealed class ProgrammeActivatedEventArgs : EventArgs
{
    public ProgrammeActivatedEventArgs(GuideRow row, Programme programme)
    {
        Row = row;
        Programme = programme;
    }

    public GuideRow Row { get; }

    public Programme Programme { get; }
}
