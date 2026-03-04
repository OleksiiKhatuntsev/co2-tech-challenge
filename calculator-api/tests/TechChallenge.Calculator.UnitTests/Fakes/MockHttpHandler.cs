using System.Net;

namespace TechChallenge.Calculator.UnitTests.Fakes;

/// <summary>
/// Minimal HttpMessageHandler mock for unit testing typed HttpClients.
/// Supports returning a fixed response or throwing a fixed exception.
/// </summary>
public class MockHttpHandler : HttpMessageHandler
{
    private readonly HttpContent? _content;
    private readonly HttpStatusCode _statusCode;
    private readonly Exception? _exception;

    public int CallCount { get; private set; }
    public Uri? LastRequestUri { get; private set; }

    /// <summary>Create a handler that returns a fixed response.</summary>
    public MockHttpHandler(HttpContent content, HttpStatusCode statusCode)
    {
        _content = content;
        _statusCode = statusCode;
    }

    /// <summary>Create a handler that throws a fixed exception.</summary>
    public MockHttpHandler(Exception exception)
    {
        _exception = exception;
        _statusCode = HttpStatusCode.OK; // unused
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequestUri = request.RequestUri;

        if (_exception is not null)
            throw _exception;

        return Task.FromResult(new HttpResponseMessage(_statusCode) { Content = _content });
    }
}