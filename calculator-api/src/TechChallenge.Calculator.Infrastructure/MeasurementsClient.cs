using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using TechChallenge.Calculator.Application.Abstractions;
using TechChallenge.Calculator.Domain;
using TechChallenge.Calculator.Domain.Exceptions;
using TechChallenge.Calculator.Infrastructure.Dto;

namespace TechChallenge.Calculator.Infrastructure;

public class MeasurementsClient(HttpClient httpClient, ILogger<MeasurementsClient> logger) : IMeasurementsClient
{
    public async Task<EnergyReading[]> GetReadingsAsync(
        string userId, long from, long to, CancellationToken cancellationToken)
    {
        try
        {
            var dtos = await httpClient.GetFromJsonAsync<MeasurementResponseDto[]>(
                $"/measurements/{userId}?from={from}&to={to}", cancellationToken);

            var readings = dtos is null
                ? []
                : Array.ConvertAll(dtos, d => new EnergyReading(d.Timestamp, d.Watts));

            logger.LogDebug(
                "Fetched {Count} readings for user {UserId}, from={From}, to={To}",
                readings.Length, userId, from, to);

            return readings;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            throw new UpstreamUnavailableException("Measurements", ex);
        }
    }
}