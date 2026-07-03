namespace Lumen.App.Theming;

/// <summary>
/// Process-wide motion configuration. <see cref="AnimationsEnabled"/> is captured from
/// <c>SystemParameters.ClientAreaAnimation</c> in the App static constructor — before any
/// XAML parses — so every duration markup extension bakes in the right value.
/// </summary>
public static class MotionSettings
{
    /// <summary>False when the OS requests no animation; all durations collapse to zero.</summary>
    public static bool AnimationsEnabled { get; set; } = true;
}
