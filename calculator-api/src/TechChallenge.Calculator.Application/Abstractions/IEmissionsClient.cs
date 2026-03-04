using TechChallenge.Calculator.Domain;

namespace TechChallenge.Calculator.Application.Abstractions;

public interface IEmissionsClient
{
    Task<EmissionFactor[]> GetFactorsAsync(long from, long to, CancellationToken cancellationToken);
}