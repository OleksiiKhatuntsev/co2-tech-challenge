using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TechChallenge.Calculator.Domain.Exceptions;
using TechChallenge.Calculator.Infrastructure;
using TechChallenge.Calculator.Infrastructure.Dto;
using TechChallenge.Calculator.UnitTests.Fakes;
using Xunit;

namespace TechChallenge.Calculator.UnitTests.Infrastructure;

public class MeasurementsClientTests
{
    private readonly ILogger<MeasurementsClient> _logger = Substitute.For<ILogger<MeasurementsClient>>();

    private const string UserId = "alpha";
    private const long From = 0;
    private const long To = 900;

    [Fact]
    public async Task GetReadingsAsync_Success_ReturnsMappedDomainModels()
    {
        var dtos = new[]
        {
            new MeasurementResponseDto(100, 250.5),
            new MeasurementResponseDto(200, 300.0)
        };

        using var handler = new MockHttpHandler(JsonContent.Create(dtos), HttpStatusCode.OK);
        var client = new MeasurementsClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") }, _logger);

        var result = await client.GetReadingsAsync(UserId, From, To, CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Timestamp.Should().Be(100);
        result[0].Watts.Should().Be(250.5);
        result[1].Timestamp.Should().Be(200);
        result[1].Watts.Should().Be(300.0);
    }

    [Fact]
    public async Task GetReadingsAsync_EmptyResponse_ReturnsEmptyArray()
    {
        var dtos = Array.Empty<MeasurementResponseDto>();

        using var handler = new MockHttpHandler(JsonContent.Create(dtos), HttpStatusCode.OK);
        var client = new MeasurementsClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") }, _logger);

        var result = await client.GetReadingsAsync(UserId, From, To, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetReadingsAsync_HttpFailure_ThrowsUpstreamUnavailableException()
    {
        using var handler = new MockHttpHandler(new StringContent(""), HttpStatusCode.InternalServerError);
        var client = new MeasurementsClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") }, _logger);

        var act = () => client.GetReadingsAsync(UserId, From, To, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<UpstreamUnavailableException>();
        ex.Which.Message.Should().Contain("Measurements");
    }

    [Fact]
    public async Task GetReadingsAsync_Timeout_ThrowsUpstreamUnavailableException()
    {
        using var handler = new MockHttpHandler(new TimeoutException("Timed out"));
        var client = new MeasurementsClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") }, _logger);

        var act = () => client.GetReadingsAsync(UserId, From, To, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<UpstreamUnavailableException>();
        ex.Which.Message.Should().Contain("Measurements");
        ex.Which.InnerException.Should().BeOfType<TimeoutException>();
    }

    [Fact]
    public async Task GetReadingsAsync_BuildsCorrectUrl()
    {
        var dtos = Array.Empty<MeasurementResponseDto>();
        using var handler = new MockHttpHandler(JsonContent.Create(dtos), HttpStatusCode.OK);
        var client = new MeasurementsClient(new HttpClient(handler) { BaseAddress = new Uri("http://test") }, _logger);

        await client.GetReadingsAsync("beta", 1000, 2000, CancellationToken.None);

        handler.LastRequestUri!.PathAndQuery.Should().Be("/measurements/beta?from=1000&to=2000");
    }
}