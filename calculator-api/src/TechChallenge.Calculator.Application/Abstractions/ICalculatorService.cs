using TechChallenge.Calculator.Domain;

namespace TechChallenge.Calculator.Application.Abstractions;

public interface ICalculatorService
{
    Task<CarbonFootprint> CalculateAsync(string userId, long from, long to, CancellationToken cancellationToken);
}