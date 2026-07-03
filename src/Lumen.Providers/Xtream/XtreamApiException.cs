namespace Lumen.Providers.Xtream;

/// <summary>Raised when an Xtream portal returns something unusable (HTML page, auth rejection, bad payload).</summary>
public sealed class XtreamApiException : Exception
{
    public XtreamApiException(string message)
        : base(message)
    {
    }

    public XtreamApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
