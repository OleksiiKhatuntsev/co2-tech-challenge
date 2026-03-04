namespace TechChallenge.Calculator.Domain.Exceptions;

public class InvalidCalculationRequestException(string reason)
    : CalculatorDomainException($"Invalid calculation request: {reason}");