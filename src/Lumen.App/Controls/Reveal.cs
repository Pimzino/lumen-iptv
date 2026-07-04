using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lumen.App.Theming;

namespace Lumen.App.Controls;

/// <summary>
/// Staggered entrance choreography for page sections. While <c>Reveal.When</c> is false the
/// element is held invisible; when it flips true the element fades in and rises, starting
/// after <c>Reveal.Delay</c> milliseconds — so a page's sections cascade instead of popping
/// in as one block. Honors OS-disabled animation by snapping straight to visible.
/// </summary>
public static class Reveal
{
    private const double RisePixels = 14;
    private static readonly TimeSpan RevealDuration = TimeSpan.FromMilliseconds(250); // Lumen "Slow"

    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached(
        "Delay", typeof(int), typeof(Reveal), new PropertyMetadata(0));

    public static int GetDelay(DependencyObject element) => (int)element.GetValue(DelayProperty);

    public static void SetDelay(DependencyObject element, int value) => element.SetValue(DelayProperty, value);

    public static readonly DependencyProperty WhenProperty = DependencyProperty.RegisterAttached(
        "When", typeof(bool), typeof(Reveal), new PropertyMetadata(false, OnWhenChanged));

    public static bool GetWhen(DependencyObject element) => (bool)element.GetValue(WhenProperty);

    public static void SetWhen(DependencyObject element, bool value) => element.SetValue(WhenProperty, value);

    private static void OnWhenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if (e.NewValue is not true)
        {
            // Armed: hidden and inert until the trigger flips.
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 0;
            element.IsHitTestVisible = false;
            return;
        }

        element.IsHitTestVisible = true;
        if (!MotionSettings.AnimationsEnabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            return;
        }

        var delay = TimeSpan.FromMilliseconds(Math.Max(0, GetDelay(element)));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var translate = new TranslateTransform(0, RisePixels);
        element.RenderTransform = translate;

        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, RevealDuration)
        {
            BeginTime = delay,
            EasingFunction = ease,
        });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(RisePixels, 0, RevealDuration)
        {
            BeginTime = delay,
            EasingFunction = ease,
        });
    }
}
