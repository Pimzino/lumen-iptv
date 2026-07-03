using System.Net;
using FluentAssertions;
using Lumen.Providers.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumen.Providers.Tests.Http;

public sealed class TransientRetryHandlerTests
{
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public SequenceHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private static HttpClient CreateClient(SequenceHandler inner) =>
        new(new TransientRetryHandler(NullLogger<TransientRetryHandler>.Instance) { InnerHandler = inner });

    [Fact]
    public async Task Retries_TransientServerErrors_ThenSucceeds()
    {
        var inner = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.BadGateway),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        var client = CreateClient(inner);

        var response = await client.GetAsync(new Uri("http://example.com/api"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.Calls.Should().Be(3);
    }

    [Fact]
    public async Task GivesUp_AfterMaxAttempts()
    {
        var inner = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = CreateClient(inner);

        var response = await client.GetAsync(new Uri("http://example.com/api"));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        inner.Calls.Should().Be(3);
    }

    [Fact]
    public async Task DoesNotRetry_ClientErrors()
    {
        var inner = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.BadRequest));
        var client = CreateClient(inner);

        var response = await client.GetAsync(new Uri("http://example.com/api"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DoesNotRetry_NonGetRequests()
    {
        var inner = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.BadGateway));
        var client = CreateClient(inner);

        var response = await client.PostAsync(new Uri("http://example.com/api"), new StringContent("x"));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Retries_ConnectionFailures()
    {
        var calls = 0;
        var flaky = new FlakyHandler(() =>
        {
            calls++;
            if (calls < 3)
            {
                throw new HttpRequestException("boom");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new HttpClient(
            new TransientRetryHandler(NullLogger<TransientRetryHandler>.Instance) { InnerHandler = flaky });

        var response = await client.GetAsync(new Uri("http://example.com/api"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        calls.Should().Be(3);
    }

    private sealed class FlakyHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responder;

        public FlakyHandler(Func<HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder());
    }
}
