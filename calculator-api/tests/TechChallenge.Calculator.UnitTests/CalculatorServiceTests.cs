using TechChallenge.Calculator.Application.Abstractions;
using TechChallenge.Calculator.Application.Services;

namespace TechChallenge.Calculator.UnitTests;

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using TechChallenge.Calculator.Application;
using TechChallenge.Calculator.Domain;
using TechChallenge.Calculator.Domain.Exceptions;

public class CalculatorServiceTests
{
    private readonly IMeasurementsClient _measurementsClient = Substitute.For<IMeasurementsClient>();
    private readonly IEmissionsClient _emissionsClient = Substitute.For<IEmissionsClient>();
    private readonly ILogger<CalculatorService> _logger = Substitute.For<ILogger<CalculatorService>>();
    private readonly CalculatorService _sut;

    public CalculatorServiceTests()
    {
        _sut = new CalculatorService(_measurementsClient, _emissionsClient, _logger);
    }

    [Fact]
    public async Task CalculateAsync_HappyPath_ReturnsCorrectCo2()
    {
        // Arrange: 2 readings in one 15-min period, factor = 0.5
        // period start = 0 (timestamp 0 and 100 both map to period 0)
        var readings = new[] { new EnergyReading(0, 100), new EnergyReading(100, 200) };
        var factors = new[] { new EmissionFactor(0, 0.5) };

        _measurementsClient.GetReadingsAsync("alpha", 0, 900, Arg.Any<CancellationToken>())
            .Returns(readings);
        _emissionsClient.GetFactorsAsync(0, 900, Arg.Any<CancellationToken>())
            .Returns(factors);

        // Act
        var result = await _sut.CalculateAsync("alpha", 0, 900, CancellationToken.None);

        // Assert: avgW = 150, kWh = 150/4/1000 = 0.0375, co2 = 0.0375 * 0.5 = 0.01875
        result.TotalKg.Should().BeApproximately(0.01875, 1e-10);
    }

    [Fact]
    public async Task CalculateAsync_EmptyMeasurements_ReturnsZero()
    {
        _measurementsClient.GetReadingsAsync("alpha", 0, 900, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EnergyReading>());
        _emissionsClient.GetFactorsAsync(0, 900, Arg.Any<CancellationToken>())
            .Returns(new[] { new EmissionFactor(0, 0.5) });

        var result = await _sut.CalculateAsync("alpha", 0, 900, CancellationToken.None);

        result.TotalKg.Should().Be(0);
    }

    [Fact]
    public async Task CalculateAsync_MissingEmissionFactor_SkipsPeriod()
    {
        // Two periods: 0 and 900. Factor only for period 900.
        var readings = new[]
        {
            new EnergyReading(0, 100),   // period 0
            new EnergyReading(900, 200)  // period 900
        };
        var factors = new[] { new EmissionFactor(900, 0.5) };

        _measurementsClient.GetReadingsAsync("alpha", 0, 1800, Arg.Any<CancellationToken>())
            .Returns(readings);
        _emissionsClient.GetFactorsAsync(0, 1800, Arg.Any<CancellationToken>())
            .Returns(factors);

        var result = await _sut.CalculateAsync("alpha", 0, 1800, CancellationToken.None);

        // Only period 900 counted: avgW=200, kWh=200/4/1000=0.05, co2=0.05*0.5=0.025
        result.TotalKg.Should().BeApproximately(0.025, 1e-10);
    }

    [Theory]
    [InlineData(100, 100)]  // from == to
    [InlineData(200, 100)]  // from > to
    public async Task CalculateAsync_InvalidFromTo_ThrowsInvalidCalculationRequestException(long from, long to)
    {
        var act = () => _sut.CalculateAsync("alpha", from, to, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidCalculationRequestException>();
    }

    [Fact]
    public async Task CalculateAsync_NegativeFrom_ThrowsInvalidCalculationRequestException()
    {
        var act = () => _sut.CalculateAsync("alpha", -1, 900, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidCalculationRequestException>();
    }

    [Fact]
    public async Task CalculateAsync_CallsUpstreamsInParallel()
    {
        _measurementsClient.GetReadingsAsync("alpha", 0, 900, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EnergyReading>());
        _emissionsClient.GetFactorsAsync(0, 900, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EmissionFactor>());

        await _sut.CalculateAsync("alpha", 0, 900, CancellationToken.None);

        await _measurementsClient.Received(1).GetReadingsAsync("alpha", 0, 900, Arg.Any<CancellationToken>());
        await _emissionsClient.Received(1).GetFactorsAsync(0, 900, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_MultiplePeriodsGroupedCorrectly()
    {
        // 3 readings across 2 periods
        var readings = new[]
        {
            new EnergyReading(0, 100),    // period 0
            new EnergyReading(450, 200),  // period 0
            new EnergyReading(900, 300)   // period 900
        };
        var factors = new[]
        {
            new EmissionFactor(0, 0.4),
            new EmissionFactor(900, 0.6)
        };

        _measurementsClient.GetReadingsAsync("alpha", 0, 1800, Arg.Any<CancellationToken>())
            .Returns(readings);
        _emissionsClient.GetFactorsAsync(0, 1800, Arg.Any<CancellationToken>())
            .Returns(factors);

        var result = await _sut.CalculateAsync("alpha", 0, 1800, CancellationToken.None);

        // Period 0: avg=150, kWh=0.0375, co2=0.0375*0.4=0.015
        // Period 900: avg=300, kWh=0.075, co2=0.075*0.6=0.045
        // Total: 0.06
        result.TotalKg.Should().BeApproximately(0.06, 1e-10);
    }
}