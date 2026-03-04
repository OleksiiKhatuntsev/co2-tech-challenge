using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TechChallenge.Calculator.Domain;
using TechChallenge.Calculator.Domain.Exceptions;
using TechChallenge.Calculator.Infrastructure;
using TechChallenge.Calculator.Infrastructure.Dto;
using TechChallenge.Calculator.UnitTests.Fakes;
using Xunit;

namespace TechChallenge.Calculator.UnitTests.Infrastructure;

public class EmissionsClientTests
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly ILogger<EmissionsClient> _logger = Substitute.For<ILogger<EmissionsClient>>();

    private const long From = 0;
    private const long OnePeriod = 900;
    private const long TwoPeriods = 1800;

    [Fact]
    public async Task GetFactorsAsync_CacheMiss_FetchesFromApi()
    {
        var dtos = new[]
        {
            new EmissionResponseDto(0, 0.5),
            new EmissionResponseDto(900, 0.6)
        };

        using var handler = new MockHttpHandler(JsonContent.Create(dtos), HttpStatusCode.OK);
        var client = CreateClient(handler);

        var result = await client.GetFactorsAsync(From, TwoPeriods, CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Timestamp.Should().Be(0);
        result[0].KgPerWattHr.Should().Be(0.5);
        result[1].Timestamp.Should().Be(900);
        result[1].KgPerWattHr.Should().Be(0.6);
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetFactorsAsync_CacheHit_ReturnsFromCacheWithoutHttpCall()
    {
        // Pre-populate cache
        _cache.Set("emission:0", new EmissionFactor(0, 0.5), TimeSpan.FromHours(24));
        _cache.Set("emission:900", new EmissionFactor(900, 0.6), TimeSpan.FromHours(24));

        using var handler = new MockHttpHandler(JsonContent.Create(Array.Empty<EmissionResponseDto>()), HttpStatusCode.OK);
        var client = CreateClient(handler);

        var result = await client.GetFactorsAsync(From, TwoPeriods, CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].KgPerWattHr.Should().Be(0.5);
        result[1].KgPerWattHr.Should().Be(0.6);
        handler.CallCount.Should().Be(0, "all blocks were cached — no HTTP call expected");
    }

    [Fact]
    public async Task GetFactorsAsync_PartialCacheHit_FetchesFromApi()
    {
        // Only first block cached — should still fetch full range
        _cache.Set("emission:0", new EmissionFactor(0, 0.5), TimeSpan.FromHours(24));

        var dtos = new[]
        {
            new EmissionResponseDto(0, 0.5),
            new EmissionResponseDto(900, 0.6)
        };

        using var handler = new MockHttpHandler(JsonContent.Create(dtos), HttpStatusCode.OK);
        var client = CreateClient(handler);

        var result = await client.GetFactorsAsync(From, TwoPeriods, CancellationToken.None);

        result.Should().HaveCount(2);
        handler.CallCount.Should().Be(1, "partial cache hit should trigger API fetch");
    }

    [Fact]
    public async Task GetFactorsAsync_CachesIndividualBlocks()
    {
        var dtos = new[]
        {
            new EmissionResponseDto(0, 0.5),
            new EmissionResponseDto(900, 0.6)
        };

        using var handler = new MockHttpHandler(JsonContent.Create(dtos), HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.GetFactorsAsync(From, TwoPeriods, CancellationToken.None);

        _cache.TryGetValue("emission:0", out EmissionFactor? first).Should().BeTrue();
        first!.KgPerWattHr.Should().Be(0.5);
        _cache.TryGetValue("emission:900", out EmissionFactor? second).Should().BeTrue();
        second!.KgPerWattHr.Should().Be(0.6);
    }

    [Fact]
    public async Task GetFactorsAsync_HttpFailure_ThrowsUpstreamUnavailableException()
    {
        using var handler = new MockHttpHandler(new StringContent(""), HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var act = () => client.GetFactorsAsync(From, OnePeriod, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<UpstreamUnavailableException>();
        ex.Which.Message.Should().Contain("Emissions");
    }

    [Fact]
    public async Task GetFactorsAsync_Timeout_ThrowsUpstreamUnavailableException()
    {
        using var handler = new MockHttpHandler(new TimeoutException("Timed out"));
        var client = CreateClient(handler);

        var act = () => client.GetFactorsAsync(From, OnePeriod, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<UpstreamUnavailableException>();
        ex.Which.Message.Should().Contain("Emissions");
        ex.Which.InnerException.Should().BeOfType<TimeoutException>();
    }

    [Fact]
    public async Task GetFactorsAsync_BuildsCorrectUrl()
    {
        var dtos = Array.Empty<EmissionResponseDto>();
        using var handler = new MockHttpHandler(JsonContent.Create(dtos), HttpStatusCode.OK);
        var client = CreateClient(handler);

        await client.GetFactorsAsync(1800, 3600, CancellationToken.None);

        handler.LastRequestUri!.PathAndQuery.Should().Be("/emissions?from=1800&to=3600");
    }

    private EmissionsClient CreateClient(MockHttpHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://test") }, _cache, _logger);
}
