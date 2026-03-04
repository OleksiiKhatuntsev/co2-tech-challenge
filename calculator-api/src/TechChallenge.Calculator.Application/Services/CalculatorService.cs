using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TechChallenge.Calculator.Application.Abstractions;
using TechChallenge.Calculator.Domain;
using TechChallenge.Calculator.Domain.Exceptions;

namespace TechChallenge.Calculator.Application.Services;

public class CalculatorService(
    IMeasurementsClient measurementsClient,
    IEmissionsClient emissionsClient,
    ILogger<CalculatorService> logger) : ICalculatorService
{
    public async Task<CarbonFootprint> CalculateAsync(string userId, long from, long to, CancellationToken ct)
    {
        if (from >= to)
            throw new InvalidCalculationRequestException($"'from' ({from}) must be less than 'to' ({to})");
        if (from < 0)
            throw new InvalidCalculationRequestException($"'from' ({from}) must not be negative");

        logger.LogInformation("Calculating CO₂ for user {UserId}, from={From}, to={To}", userId, from, to);
        var sw = Stopwatch.StartNew();

        var readingsTask = measurementsClient.GetReadingsAsync(userId, from, to, ct);
        var factorsTask = emissionsClient.GetFactorsAsync(from, to, ct);
        await Task.WhenAll(readingsTask, factorsTask);

        var readings = readingsTask.Result;
        var factors = factorsTask.Result;

        var factorsByTimestamp = factors.ToDictionary(f => f.Timestamp, f => f.KgPerWattHr);

        var totalCo2 = 0.0;
        var periodCount = 0;

        var groups = readings.GroupBy(r => r.Timestamp / 900 * 900);

        foreach (var group in groups)
        {
            var periodStart = group.Key;
            var avgWatts = group.Average(r => r.Watts);
            var kWh = avgWatts / 4.0 / 1000.0;

            if (!factorsByTimestamp.TryGetValue(periodStart, out var factor))
            {
                logger.LogWarning("Missing emission factor for period {PeriodStart}, skipping", periodStart);
                continue;
            }

            var co2 = kWh * factor;
            totalCo2 += co2;
            periodCount++;

            logger.LogDebug(
                "Period {PeriodStart}: avgWatts={AvgWatts:F2}, kWh={KWh:F6}, factor={Factor:F6}, co2={Co2:F6}",
                periodStart, avgWatts, kWh, factor, co2);
        }

        sw.Stop();
        logger.LogInformation(
            "Calculation complete for {UserId}: totalCo2={TotalKg:F6} kg, periods={PeriodCount}, elapsed={ElapsedMs} ms",
            userId, totalCo2, periodCount, sw.ElapsedMilliseconds);

        return new CarbonFootprint(totalCo2);
    }
}
