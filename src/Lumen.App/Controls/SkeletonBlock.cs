using System.Windows;
using System.Windows.Controls;

namespace Lumen.App.Controls;

/// <summary>
/// Shimmering placeholder shown while content loads. A dedicated control (rather than a
/// Border style) so every instance gets its own animated gradient brush from the template.
/// </summary>
public sealed class SkeletonBlock : Control
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(SkeletonBlock),
        new FrameworkPropertyMetadata(new CornerRadius(8)));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }
}
