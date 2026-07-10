namespace Lumen.Core;

/// <summary>
/// Hygiene for provider-supplied artwork/logo fields. IPTV panels sometimes leak
/// PHP-serialized fragments (<c>s:308:/images/…jpeg</c>) or bare relative paths where a
/// URL belongs; those values can never render, so they are treated as "no artwork".
/// </summary>
public static class WebUrl
{
    /// <summary>True when the value is an absolute http/https URL an image fetch can use.</summary>
    public static bool IsHttp(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>The value when <see cref="IsHttp"/>, otherwise null.</summary>
    public static string? NullIfNotHttp(string? value) => IsHttp(value) ? value : null;
}
