using System.Net;
using System.Text;

namespace Lumen.Providers.Tests.Support;

/// <summary>Loads fixture files copied to the test output directory.</summary>
public static class FixtureFile
{
    public static string PathOf(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string ReadText(string name) => File.ReadAllText(PathOf(name));

    public static Stream OpenRead(string name) => File.OpenRead(PathOf(name));
}

/// <summary>HttpMessageHandler stub with a per-request responder function.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<Uri?> Requests { get; } = [];

    public static StubHttpMessageHandler RespondingWithJson(string json) =>
        new(_ => JsonResponse(json));

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    public static HttpResponseMessage HtmlResponse(string html) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri);
        return Task.FromResult(_responder(request));
    }
}
