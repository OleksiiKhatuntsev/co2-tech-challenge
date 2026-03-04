using TechChallenge.Calculator.Domain;

namespace TechChallenge.Calculator.Application.Abstractions;

public interface IMeasurementsClient
{
    Task<EnergyReading[]> GetReadingsAsync(string userId, long from, long to, CancellationToken cancellationToken);
}