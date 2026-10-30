using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scheduler.Tests.Support;
using Testcontainers.PostgreSql;

namespace Scheduler.Tests.Integration;

/// <summary>
/// Starts one real PostgreSQL (Testcontainers) and one in-process API host shared by all integration tests.
/// Set TEST_DB_CONNECTION to point at an existing PostgreSQL instead (e.g. when Docker is unavailable).
/// Exclusion constraints are a PostgreSQL feature, so an in-memory/SQLite substitute would hide real behaviour.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    /// <summary>The fixed "now" for the API under test: Monday 2 Nov 2026, 08:00 UTC.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    private PostgreSqlContainer? _container;
    private WebApplicationFactory<Program>? _factory;

    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => _factory!.Services;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
            await _container.StartAsync();
            connectionString = _container.GetConnectionString();
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Scheduler"] = connectionString,
                ["Database:Initialize"] = "true",
                ["Seed:Enabled"] = "false" // every test creates its own isolated data
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            });
        });

        Client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
