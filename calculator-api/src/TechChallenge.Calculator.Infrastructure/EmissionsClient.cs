using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TechChallenge.Calculator.Application.Abstractions;
using TechChallenge.Calculator.Domain;
using TechChallenge.Calculator.Domain.Exceptions;
using TechChallenge.Calculator.Infrastructure.Dto;

namespace TechChallenge.Calculator.Infrastructure;

public class EmissionsClient(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<EmissionsClient> logger) : IEmissionsClient
{
    private const long PeriodSeconds = 900;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    public async Task<EmissionFactor[]> GetFactorsAsync(long from, long to, CancellationToken cancellationToken)
    {
        var timestamps = GetExpectedTimestamps(from, to);

        if (TryGetAllFromCache(timestamps, from, to, out var cached))
            return cached;

        return await FetchFromApiAsync(from, to, cancellationToken);
    }

    private bool TryGetAllFromCache(List<long> timestamps, long from, long to, out EmissionFactor[] factors)
    {
        var result = new List<EmissionFactor>(timestamps.Count);

        foreach (var ts in timestamps)
        {
            if (cache.TryGetValue(CacheKey(ts), out EmissionFactor? factor))
                result.Add(factor!);
            else
            {
                factors = [];
                return false;
            }
        }

        logger.LogDebug("Cache hit: all {Count} emission blocks cached for from={From}, to={To}",
            timestamps.Count, from, to);

        factors = [.. result];
        return true;
    }

    private async Task<EmissionFactor[]> FetchFromApiAsync(long from, long to, CancellationToken cancellationToken)
    {
        logger.LogDebug("Cache miss: fetching emissions from API for from={From}, to={To}", from, to);

        try
        {
            var dtos = await httpClient.GetFromJsonAsync<EmissionResponseDto[]>(
                $"/emissions?from={from}&to={to}", cancellationToken);

            var factors = dtos is null
                ? []
                : Array.ConvertAll(dtos, d => new EmissionFactor(d.Timestamp, d.KgPerWattHr));

            foreach (var factor in factors)
            {
                cache.Set(CacheKey(factor.Timestamp), factor, CacheTtl);
            }

            logger.LogDebug("Cached {Count} emission blocks", factors.Length);

            return factors;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            throw new UpstreamUnavailableException("Emissions", ex);
        }
    }

    private static List<long> GetExpectedTimestamps(long from, long to)
    {
        var timestamps = new List<long>();
        for (var ts = from; ts < to; ts += PeriodSeconds)
        {
            timestamps.Add(ts);
        }
        return timestamps;
    }

    private static string CacheKey(long timestamp) => $"emission:{timestamp}";
}
