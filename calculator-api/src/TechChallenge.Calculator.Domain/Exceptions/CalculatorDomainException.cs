namespace TechChallenge.Calculator.Domain.Exceptions;

public class CalculatorDomainException(string message, Exception? inner = null)
    : Exception(message, inner);