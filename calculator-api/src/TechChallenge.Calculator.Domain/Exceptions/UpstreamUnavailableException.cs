namespace TechChallenge.Calculator.Domain.Exceptions;

public class UpstreamUnavailableException(string serviceName, Exception inner)
    : CalculatorDomainException($"Upstream service '{serviceName}' is unavailable", inner);