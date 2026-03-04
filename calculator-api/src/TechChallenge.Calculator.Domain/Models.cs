namespace TechChallenge.Calculator.Domain;

public record EnergyReading(long Timestamp, double Watts);
public record EmissionFactor(long Timestamp, double KgPerWattHr);
public record CarbonFootprint(double TotalKg);