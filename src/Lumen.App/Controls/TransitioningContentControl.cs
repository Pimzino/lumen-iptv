using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lumen.App.Theming;

namespace Lumen.App.Controls;

/// <summary>
/// ContentControl that plays a short fade + rise whenever its content changes, so page
/// swaps read as intentional transitions instead of hard cuts. Skipped entirely when the
/// OS disables animations (<see cref="MotionSettings"/>) and on the first assignment.
/// </summary>
public sealed class TransitioningContentControl : ContentControl
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(150); // Lumen "Fast"
    private const double RisePixels = 8;

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        if (!MotionSettings.AnimationsEnabled || oldContent is null || newContent is null)
        {
            return;
        }

        var translate = new TranslateTransform(0, RisePixels);
        RenderTransform = translate;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeDuration) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(RisePixels, 0, FadeDuration)
        {
            EasingFunction = ease,
        });
    }
}
