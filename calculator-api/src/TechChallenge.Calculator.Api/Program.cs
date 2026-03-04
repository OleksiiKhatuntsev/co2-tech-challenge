using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using TechChallenge.Calculator.Api.Middleware;
using TechChallenge.Calculator.Application.Abstractions;
using TechChallenge.Calculator.Application.Services;
using TechChallenge.Calculator.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddMemoryCache();

// --- DI: Calculator Service ---
builder.Services.AddScoped<ICalculatorService, CalculatorService>();

// --- DI: Measurements HTTP Client + Resilience ---
builder.Services
    .AddHttpClient<IMeasurementsClient, MeasurementsClient>(client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["Upstream:MeasurementsUrl"]!);
    })
    .AddResilienceHandler("measurements", pipeline =>
    {
        // Retry: 3 retries (4 total), exponential backoff, on transient HTTP errors
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(1),
            UseJitter = true
        });

        // Circuit Breaker: break after 5 failures, 30s recovery
        pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 5,
            BreakDuration = TimeSpan.FromSeconds(30)
        });
    });

// --- DI: Emissions HTTP Client + Resilience ---
builder.Services
    .AddHttpClient<IEmissionsClient, EmissionsClient>(client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["Upstream:EmissionsUrl"]!);
    })
    .AddResilienceHandler("emissions", pipeline =>
    {
        // Outer timeout: 30s safety net for entire pipeline
        pipeline.AddTimeout(TimeSpan.FromSeconds(30));

        // Retry: 5 fast attempts (1s timeout each), 6th attempt gets full 15s
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 5,
            BackoffType = DelayBackoffType.Constant,
            Delay = TimeSpan.FromMilliseconds(200),
            UseJitter = true
        });

        // Per-attempt timeout: 1s — cuts off chaos delay quickly
        pipeline.AddTimeout(TimeSpan.FromSeconds(1));
    });

var app = builder.Build();

// --- Middleware pipeline ---
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();

// --- Endpoints ---
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/calculate/{userId}",
    async (
        [FromRoute] string userId,
        [FromQuery] long from,
        [FromQuery] long to,
        ICalculatorService calculator,
        ILogger<Program> logger,
        CancellationToken ct) =>
    {
        logger.LogInformation("Calculate request: userId={UserId}, from={From}, to={To}", userId, from, to);

        var result = await calculator.CalculateAsync(userId, from, to, ct);

        return Results.Ok(new { totalKg = result.TotalKg });
    })
    .WithName("Calculate")
    .Produces<object>()
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status502BadGateway)
    .Produces(StatusCodes.Status500InternalServerError)
    .WithOpenApi();

app.Run();

// Make Program class accessible for WebApplicationFactory in E2E tests
public partial class Program;