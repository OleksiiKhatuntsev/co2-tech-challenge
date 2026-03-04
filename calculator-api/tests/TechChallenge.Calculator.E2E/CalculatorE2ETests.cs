using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace TechChallenge.Calculator.E2E;

public class CalculatorE2ETests : IClassFixture<CalculatorApiFactory>
{
    private readonly HttpClient _client;
    private readonly CalculatorApiFactory _factory;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // One 15-minute period: 2021-01-01 00:00:00 UTC → 00:15:00 UTC
    private const long From = 1609459200;
    private const long To = 1609460100;

    public CalculatorE2ETests(CalculatorApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task HappyPath_ReturnsCorrectCo2()
    {
        // 100W avg over 15 min, factor 0.5 kg/kWh
        // CO₂ = (100 / 4 / 1000) × 0.5 = 0.0125 kg
        SetupMeasurements(From, To, [new(From + 5, 100.0)]);
        SetupEmissions(From, To, [new(From, 0.5)]);

        var response = await _client.GetAsync($"/calculate/alpha?from={From}&to={To}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeResponse(response);
        body.TotalKg.Should().BeApproximately(0.0125, 1e-9);
    }

    [Fact]
    public async Task MeasurementsRetry_FirstCallFails_RetrySucceeds()
    {
        // First call → 500, second call → 200 with data
        // Scenario: use WireMock stateful behavior
        _factory.MeasurementsServer.Reset();

        _factory.MeasurementsServer
            .Given(Request.Create()
                .WithPath("/measurements/alpha")
                .UsingGet())
            .InScenario("retry")
            .WillSetStateTo("failed-once")
            .RespondWith(Response.Create()
                .WithStatusCode(500)
                .WithBody("Internal Server Error"));

        _factory.MeasurementsServer
            .Given(Request.Create()
                .WithPath("/measurements/alpha")
                .UsingGet())
            .InScenario("retry")
            .WhenStateIs("failed-once")
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(
                    new[] { new { Timestamp = From + 5, Watts = 100.0 } }, JsonOptions)));

        SetupEmissions(From, To, [new(From, 0.5)]);

        var response = await _client.GetAsync($"/calculate/alpha?from={From}&to={To}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeResponse(response);
        body.TotalKg.Should().BeApproximately(0.0125, 1e-9);
    }

    [Fact]
    public async Task EmissionsCache_SecondRequestUsesCache()
    {
        // Use a unique time range so cache from other tests doesn't interfere
        const long cacheFrom = 1609466400; // 2021-01-01 02:00:00 UTC
        const long cacheTo = 1609467300;   // 2021-01-01 02:15:00 UTC

        SetupMeasurements(cacheFrom, cacheTo, [new(cacheFrom + 5, 200.0)]);
        SetupEmissions(cacheFrom, cacheTo, [new(cacheFrom, 0.3)]);

        // First request — hits WireMock for emissions
        var response1 = await _client.GetAsync($"/calculate/alpha?from={cacheFrom}&to={cacheTo}");
        response1.StatusCode.Should().Be(HttpStatusCode.OK);

        // Record how many times emissions endpoint was called
        var emissionsCallsBefore = _factory.EmissionsServer.LogEntries.Count();

        // Second request — emissions should come from cache
        var response2 = await _client.GetAsync($"/calculate/alpha?from={cacheFrom}&to={cacheTo}");
        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        var emissionsCallsAfter = _factory.EmissionsServer.LogEntries.Count();
        emissionsCallsAfter.Should().Be(emissionsCallsBefore, "second request should use cached emissions");
    }

    [Fact]
    public async Task InvalidParams_FromGreaterThanTo_Returns400()
    {
        var response = await _client.GetAsync($"/calculate/alpha?from={To}&to={From}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("error");
    }

    [Fact]
    public async Task InvalidParams_NotAligned_Returns400()
    {
        var response = await _client.GetAsync("/calculate/alpha?from=1609459201&to=1609460100");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("error");
    }

    [Fact]
    public async Task EmptyMeasurements_ReturnsZero()
    {
        const long emptyFrom = 1609470000;
        const long emptyTo = 1609470900;

        SetupMeasurements(emptyFrom, emptyTo, []);
        SetupEmissions(emptyFrom, emptyTo, [new(emptyFrom, 0.5)]);

        var response = await _client.GetAsync($"/calculate/alpha?from={emptyFrom}&to={emptyTo}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await DeserializeResponse(response);
        body.TotalKg.Should().Be(0.0);
    }

    [Fact]
    public async Task UpstreamDown_AllRetriesFail_Returns502()
    {
        const long downFrom = 1609473600;
        const long downTo = 1609474500;

        // Measurements always returns 500
        _factory.MeasurementsServer
            .Given(Request.Create()
                .WithPath("/measurements/alpha")
                .WithParam("from", downFrom.ToString())
                .WithParam("to", downTo.ToString())
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(500)
                .WithBody("Internal Server Error"));

        SetupEmissions(downFrom, downTo, [new(downFrom, 0.5)]);

        var response = await _client.GetAsync($"/calculate/alpha?from={downFrom}&to={downTo}");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("error");
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("healthy");
    }

    // --- Helpers ---

    private void SetupMeasurements(long from, long to, MeasurementDto[] data)
    {
        _factory.MeasurementsServer
            .Given(Request.Create()
                .WithPath("/measurements/alpha")
                .WithParam("from", from.ToString())
                .WithParam("to", to.ToString())
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(data, JsonOptions)));
    }

    private void SetupEmissions(long from, long to, EmissionDto[] data)
    {
        _factory.EmissionsServer
            .Given(Request.Create()
                .WithPath("/emissions")
                .WithParam("from", from.ToString())
                .WithParam("to", to.ToString())
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(data, JsonOptions)));
    }

    private static async Task<CalculateResponse> DeserializeResponse(HttpResponseMessage response)
    {
        var result = await response.Content.ReadFromJsonAsync<CalculateResponse>(JsonOptions);
        return result!;
    }

    private record MeasurementDto(long Timestamp, double Watts);
    private record EmissionDto(long Timestamp, double KgPerWattHr);
    private record CalculateResponse(double TotalKg);
}