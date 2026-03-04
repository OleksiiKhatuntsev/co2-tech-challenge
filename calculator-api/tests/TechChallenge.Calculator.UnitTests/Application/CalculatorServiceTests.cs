using TechChallenge.Calculator.Application.Abstractions;
using TechChallenge.Calculator.Application.Services;

namespace TechChallenge.Calculator.UnitTests.Application;

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

    private const string AlphaUser = "alpha";
    private const long OnePeriod = 900;
    private const long TwoPeriods = 1800;

    public CalculatorServiceTests()
    {
        _sut = new CalculatorService(_measurementsClient, _emissionsClient, _logger);
    }

    [Fact]
    public async Task CalculateAsync_HappyPath_ReturnsCorrectCo2()
    {
        // Arrange: 2 readings in one 15-min period, factor = 0.5
        const long periodStart = 0;
        const double firstReading = 100;
        const double secondReading = 200;
        const double emissionFactor = 0.5;

        var readings = new[] { new EnergyReading(periodStart, firstReading), new EnergyReading(100, secondReading) };
        var factors = new[] { new EmissionFactor(periodStart, emissionFactor) };

        _measurementsClient.GetReadingsAsync(AlphaUser, periodStart, OnePeriod, Arg.Any<CancellationToken>())
            .Returns(readings);
        _emissionsClient.GetFactorsAsync(periodStart, OnePeriod, Arg.Any<CancellationToken>())
            .Returns(factors);

        // Act
        var result = await _sut.CalculateAsync(AlphaUser, periodStart, OnePeriod, CancellationToken.None);

        // Assert: avgW = 150, kWh = 150/4/1000 = 0.0375, co2 = 0.0375 * 0.5 = 0.01875
        var averageWatts = (firstReading + secondReading) / 2;
        var expectedKwh = averageWatts / 4.0 / 1000.0;
        var expectedCo2 = expectedKwh * emissionFactor;

        result.TotalKg.Should().BeApproximately(expectedCo2, 1e-10);
    }

    [Fact]
    public async Task CalculateAsync_EmptyMeasurements_ReturnsZero()
    {
        const long periodStart = 0;
        const double expectedCo2 = 0.0;

        _measurementsClient.GetReadingsAsync(AlphaUser, periodStart, OnePeriod, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EnergyReading>());
        _emissionsClient.GetFactorsAsync(periodStart, OnePeriod, Arg.Any<CancellationToken>())
            .Returns(new[] { new EmissionFactor(periodStart, 0.5) });

        var result = await _sut.CalculateAsync(AlphaUser, periodStart, OnePeriod, CancellationToken.None);

        result.TotalKg.Should().Be(expectedCo2);
    }

    [Fact]
    public async Task CalculateAsync_MissingEmissionFactor_SkipsPeriod()
    {
        const long firstPeriodStart = 0;
        const long secondPeriodStart = 900;
        const double readingInFirstPeriod = 100;
        const double readingInSecondPeriod = 200;
        const double factorForSecondPeriod = 0.5;

        var readings = new[]
        {
            new EnergyReading(firstPeriodStart, readingInFirstPeriod),
            new EnergyReading(secondPeriodStart, readingInSecondPeriod)
        };
        // Factor only for second period — first period should be skipped
        var factors = new[] { new EmissionFactor(secondPeriodStart, factorForSecondPeriod) };

        _measurementsClient.GetReadingsAsync(AlphaUser, firstPeriodStart, TwoPeriods, Arg.Any<CancellationToken>())
            .Returns(readings);
        _emissionsClient.GetFactorsAsync(firstPeriodStart, TwoPeriods, Arg.Any<CancellationToken>())
            .Returns(factors);

        var result = await _sut.CalculateAsync(AlphaUser, firstPeriodStart, TwoPeriods, CancellationToken.None);

        var expectedKwh = readingInSecondPeriod / 4.0 / 1000.0;
        var expectedCo2 = expectedKwh * factorForSecondPeriod;

        result.TotalKg.Should().BeApproximately(expectedCo2, 1e-10);
    }

    [Theory]
    [InlineData(100, 100)]  // from == to
    [InlineData(200, 100)]  // from > to
    public async Task CalculateAsync_InvalidFromTo_ThrowsInvalidCalculationRequestException(long from, long to)
    {
        var act = () => _sut.CalculateAsync(AlphaUser, from, to, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidCalculationRequestException>();
    }

    [Fact]
    public async Task CalculateAsync_NegativeFrom_ThrowsInvalidCalculationRequestException()
    {
        const long negativeFrom = -1;

        var act = () => _sut.CalculateAsync(AlphaUser, negativeFrom, OnePeriod, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidCalculationRequestException>();
    }

    [Theory]
    [InlineData(0, 901)]    // to not aligned
    [InlineData(1, 900)]    // from not aligned
    [InlineData(10, 1810)]  // both not aligned
    public async Task CalculateAsync_NotAlignedTo15Min_ThrowsInvalidCalculationRequestException(long from, long to)
    {
        var act = () => _sut.CalculateAsync(AlphaUser, from, to, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidCalculationRequestException>()
            .WithMessage("*aligned to 15-minute boundaries*");
    }

    [Fact]
    public async Task CalculateAsync_CallsUpstreamsInParallel()
    {
        const long periodStart = 0;

        _measurementsClient.GetReadingsAsync(AlphaUser, periodStart, OnePeriod, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EnergyReading>());
        _emissionsClient.GetFactorsAsync(periodStart, OnePeriod, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EmissionFactor>());

        await _sut.CalculateAsync(AlphaUser, periodStart, OnePeriod, CancellationToken.None);

        await _measurementsClient.Received(1).GetReadingsAsync(AlphaUser, periodStart, OnePeriod, Arg.Any<CancellationToken>());
        await _emissionsClient.Received(1).GetFactorsAsync(periodStart, OnePeriod, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CalculateAsync_MultiplePeriodsGroupedCorrectly()
    {
        const long firstPeriodStart = 0;
        const long secondPeriodStart = 900;
        const double firstPeriodReading1 = 100;
        const double firstPeriodReading2 = 200;
        const double secondPeriodReading = 300;
        const double firstPeriodFactor = 0.4;
        const double secondPeriodFactor = 0.6;

        var readings = new[]
        {
            new EnergyReading(firstPeriodStart, firstPeriodReading1),
            new EnergyReading(450, firstPeriodReading2),
            new EnergyReading(secondPeriodStart, secondPeriodReading)
        };
        var factors = new[]
        {
            new EmissionFactor(firstPeriodStart, firstPeriodFactor),
            new EmissionFactor(secondPeriodStart, secondPeriodFactor)
        };

        _measurementsClient.GetReadingsAsync(AlphaUser, firstPeriodStart, TwoPeriods, Arg.Any<CancellationToken>())
            .Returns(readings);
        _emissionsClient.GetFactorsAsync(firstPeriodStart, TwoPeriods, Arg.Any<CancellationToken>())
            .Returns(factors);

        var result = await _sut.CalculateAsync(AlphaUser, firstPeriodStart, TwoPeriods, CancellationToken.None);

        var firstPeriodAvg = (firstPeriodReading1 + firstPeriodReading2) / 2;
        var firstPeriodCo2 = firstPeriodAvg / 4.0 / 1000.0 * firstPeriodFactor;
        var secondPeriodCo2 = secondPeriodReading / 4.0 / 1000.0 * secondPeriodFactor;
        var expectedTotal = firstPeriodCo2 + secondPeriodCo2;

        result.TotalKg.Should().BeApproximately(expectedTotal, 1e-10);
    }
}