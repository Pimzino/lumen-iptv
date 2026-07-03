using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Animation;

namespace Lumen.App.Theming;

/// <summary>Named motion durations of the design system.</summary>
public enum MotionSpeed
{
    /// <summary>150 ms — hovers and small state changes.</summary>
    Fast,

    /// <summary>250 ms — panel slides and page transitions.</summary>
    Slow,

    /// <summary>1.6 s — skeleton shimmer sweep.</summary>
    Shimmer,
}

/// <summary>
/// Supplies motion durations to storyboards: <c>Duration="{theming:Dur Fast}"</c>.
/// A markup extension (rather than a resource) so the value works inside freezable
/// storyboards in any dictionary, and so OS-disabled animation collapses every
/// duration to zero from a single switch.
/// </summary>
[MarkupExtensionReturnType(typeof(Duration))]
public sealed class DurExtension : MarkupExtension
{
    public DurExtension()
    {
    }

    public DurExtension(MotionSpeed speed)
    {
        Speed = speed;
    }

    public MotionSpeed Speed { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (!MotionSettings.AnimationsEnabled)
        {
            return new Duration(TimeSpan.Zero);
        }

        return new Duration(Speed switch
        {
            MotionSpeed.Fast => TimeSpan.FromMilliseconds(150),
            MotionSpeed.Slow => TimeSpan.FromMilliseconds(250),
            MotionSpeed.Shimmer => TimeSpan.FromMilliseconds(1600),
            _ => TimeSpan.FromMilliseconds(150),
        });
    }
}

/// <summary>
/// RepeatBehavior for looping ambient animations (skeleton shimmer): Forever normally,
/// a single pass when the OS disables animation so zero-length timelines cannot spin.
/// </summary>
[MarkupExtensionReturnType(typeof(RepeatBehavior))]
public sealed class LoopExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        MotionSettings.AnimationsEnabled ? RepeatBehavior.Forever : new RepeatBehavior(1);
}
