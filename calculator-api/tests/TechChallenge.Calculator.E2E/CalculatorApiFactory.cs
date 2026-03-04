using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WireMock.Server;
using Xunit;

namespace TechChallenge.Calculator.E2E;

public class CalculatorApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public WireMockServer MeasurementsServer { get; private set; } = null!;
    public WireMockServer EmissionsServer { get; private set; } = null!;

    public Task InitializeAsync()
    {
        MeasurementsServer = WireMockServer.Start();
        EmissionsServer = WireMockServer.Start();

        // Force WebApplicationFactory to create the host with WireMock URLs
        _ = Server;

        return Task.CompletedTask;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Upstream:MeasurementsUrl"] = MeasurementsServer.Url!,
                ["Upstream:EmissionsUrl"] = EmissionsServer.Url!
            });
        });
    }

    public new async Task DisposeAsync()
    {
        MeasurementsServer?.Stop();
        EmissionsServer?.Stop();
        await base.DisposeAsync();
    }
}