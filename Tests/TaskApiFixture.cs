using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Tests;

/// <summary>
/// Shared test infrastructure: a real PostgreSQL and a real RabbitMQ (Testcontainers), plus a
/// <see cref="WebApplicationFactory{TEntryPoint}"/> wiring the API to them. One instance is
/// shared by all tests in a test class (IClassFixture) to avoid restarting containers per test.
/// </summary>
public sealed class TaskApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3.13-management")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string RabbitConnectionString => _rabbit.GetConnectionString();

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public HttpClient CreateClient() => Factory.CreateClient();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        var rabbitUri = new Uri(_rabbit.GetConnectionString());
        var userInfo = rabbitUri.UserInfo.Split(':', 2);

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
                    ["RabbitMq:Host"] = rabbitUri.Host,
                    ["RabbitMq:Port"] = rabbitUri.Port.ToString(),
                    ["RabbitMq:Username"] = userInfo[0],
                    ["RabbitMq:Password"] = userInfo.Length > 1 ? userInfo[1] : string.Empty
                });
            });
        });

        // Force host creation now so DB bootstrap (EnsureCreated) runs once.
        using var _ = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }
}
