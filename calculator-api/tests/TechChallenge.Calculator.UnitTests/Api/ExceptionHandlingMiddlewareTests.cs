using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TechChallenge.Calculator.Api.Middleware;
using TechChallenge.Calculator.Domain.Exceptions;
using Xunit;

namespace TechChallenge.Calculator.UnitTests.Api;

public class ExceptionHandlingMiddlewareTests
{
    private readonly ILogger<ExceptionHandlingMiddleware> _logger =
        Substitute.For<ILogger<ExceptionHandlingMiddleware>>();

    [Fact]
    public async Task Invoke_NoException_PassesThrough()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(_ => Task.CompletedTask, _logger);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Invoke_InvalidCalculationRequestException_Returns400()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidCalculationRequestException("bad input"), _logger);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var body = await ReadResponseBody(context);
        body.Should().Contain("bad input");
    }

    [Fact]
    public async Task Invoke_UpstreamUnavailableException_Returns502()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new UpstreamUnavailableException("Measurements", new HttpRequestException()), _logger);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        var body = await ReadResponseBody(context);
        body.Should().Contain("Measurements");
    }

    [Fact]
    public async Task Invoke_UnhandledException_Returns500()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("something broke"), _logger);

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        var body = await ReadResponseBody(context);
        body.Should().Contain("Internal server error");
        body.Should().NotContain("something broke");
    }

    [Fact]
    public async Task Invoke_ErrorResponse_HasJsonFormat()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidCalculationRequestException("test"), _logger);

        await middleware.InvokeAsync(context);

        context.Response.ContentType.Should().Be("application/json");
        var body = await ReadResponseBody(context);
        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("test");
    }

    private static async Task<string> ReadResponseBody(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }
}