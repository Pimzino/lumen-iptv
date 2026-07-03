using System.Windows;
using System.Windows.Controls;

namespace Lumen.App.Controls;

/// <summary>
/// Attached properties consumed by Lumen control templates: floating field labels and
/// icon glyphs. Visual-only behavior — no service logic lives here.
/// </summary>
public static class LumenUi
{
    /// <summary>Floating label text for TextBox/PasswordBox styled with Lumen.TextBox.</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.RegisterAttached(
        "Label", typeof(string), typeof(LumenUi), new PropertyMetadata(string.Empty));

    public static string GetLabel(DependencyObject element) => (string)element.GetValue(LabelProperty);

    public static void SetLabel(DependencyObject element, string value) => element.SetValue(LabelProperty, value);

    /// <summary>Icon glyph (Segoe Fluent Icons) consumed by templates such as toasts.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(LumenUi), new PropertyMetadata(string.Empty));

    public static string GetGlyph(DependencyObject element) => (string)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string value) => element.SetValue(GlyphProperty, value);

    /// <summary>True while a floating-label field should render its label floated (focused or non-empty).</summary>
    public static readonly DependencyProperty IsFloatingProperty = DependencyProperty.RegisterAttached(
        "IsFloating", typeof(bool), typeof(LumenUi), new PropertyMetadata(false));

    public static bool GetIsFloating(DependencyObject element) => (bool)element.GetValue(IsFloatingProperty);

    public static void SetIsFloating(DependencyObject element, bool value) => element.SetValue(IsFloatingProperty, value);

    /// <summary>Enables floating-label tracking on a TextBox or PasswordBox.</summary>
    public static readonly DependencyProperty EnableFloatingLabelProperty = DependencyProperty.RegisterAttached(
        "EnableFloatingLabel", typeof(bool), typeof(LumenUi), new PropertyMetadata(false, OnEnableFloatingLabelChanged));

    public static bool GetEnableFloatingLabel(DependencyObject element) => (bool)element.GetValue(EnableFloatingLabelProperty);

    public static void SetEnableFloatingLabel(DependencyObject element, bool value) => element.SetValue(EnableFloatingLabelProperty, value);

    /// <summary>Bindable mirror of PasswordBox.Password (which is not a dependency property).</summary>
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password", typeof(string), typeof(LumenUi),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    public static string GetPassword(DependencyObject element) => (string)element.GetValue(PasswordProperty);

    public static void SetPassword(DependencyObject element, string value) => element.SetValue(PasswordProperty, value);

    /// <summary>Enables two-way syncing between PasswordBox.Password and the Password attached property.</summary>
    public static readonly DependencyProperty BindPasswordProperty = DependencyProperty.RegisterAttached(
        "BindPassword", typeof(bool), typeof(LumenUi), new PropertyMetadata(false, OnBindPasswordChanged));

    public static bool GetBindPassword(DependencyObject element) => (bool)element.GetValue(BindPasswordProperty);

    public static void SetBindPassword(DependencyObject element, bool value) => element.SetValue(BindPasswordProperty, value);

    private static readonly DependencyProperty SuppressPasswordSyncProperty = DependencyProperty.RegisterAttached(
        "SuppressPasswordSync", typeof(bool), typeof(LumenUi), new PropertyMetadata(false));

    private static void OnBindPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box || e.NewValue is not true)
        {
            return;
        }

        box.PasswordChanged += (_, _) =>
        {
            box.SetValue(SuppressPasswordSyncProperty, true);
            SetPassword(box, box.Password);
            box.SetValue(SuppressPasswordSyncProperty, false);
        };
    }

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PasswordBox box &&
            !(bool)box.GetValue(SuppressPasswordSyncProperty) &&
            box.Password != (string)e.NewValue)
        {
            box.Password = (string)e.NewValue;
        }
    }

    private static void OnEnableFloatingLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        switch (d)
        {
            case TextBox textBox:
                textBox.TextChanged += (_, _) => UpdateTextBox(textBox);
                textBox.IsKeyboardFocusWithinChanged += (_, _) => UpdateTextBox(textBox);
                UpdateTextBox(textBox);
                break;

            case PasswordBox passwordBox:
                passwordBox.PasswordChanged += (_, _) => UpdatePasswordBox(passwordBox);
                passwordBox.IsKeyboardFocusWithinChanged += (_, _) => UpdatePasswordBox(passwordBox);
                UpdatePasswordBox(passwordBox);
                break;
        }

        static void UpdateTextBox(TextBox box) =>
            SetIsFloating(box, box.IsKeyboardFocusWithin || box.Text.Length > 0);

        static void UpdatePasswordBox(PasswordBox box) =>
            SetIsFloating(box, box.IsKeyboardFocusWithin || box.Password.Length > 0);
    }
}
