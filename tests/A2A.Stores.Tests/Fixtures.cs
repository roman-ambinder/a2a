using A2A.Server.Postgres;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace A2A.Stores.Tests;

/// <summary>
/// Starts a container once per test run. When Docker is missing the fixture reports why and the tests that need it skip,
/// unless <c>A2A_REQUIRE_DOCKER=1</c> (set on CI), which turns the missing Docker into a failure.
/// </summary>
public abstract class ContainerFixture : IAsyncLifetime
{
    public string? Unavailable { get; private set; }

    public static bool DockerRequired => Environment.GetEnvironmentVariable("A2A_REQUIRE_DOCKER") == "1";

    public async Task InitializeAsync()
    {
        try
        {
            await StartAsync();
        }
        catch (Exception ex) when (!DockerRequired)
        {
            Unavailable = $"Docker is not available ({ex.GetType().Name}: {ex.Message.Split('\n')[0]})";
        }
    }

    public abstract Task DisposeAsync();

    protected abstract Task StartAsync();

    /// <summary>Skips the calling test when the container could not start.</summary>
    public void SkipIfUnavailable() => Skip.If(Unavailable is not null, Unavailable);
}

public sealed class PostgresFixture : ContainerFixture
{
    private PostgreSqlContainer? _container;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    protected override async Task StartAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
    }

    /// <summary>A data source of its own (for example for an instance whose death cuts its database access). Dispose it.</summary>
    public NpgsqlDataSource NewDataSource() => NpgsqlDataSource.Create(_container!.GetConnectionString());

    /// <summary>
    /// A fresh schema with the A2A tables, so every test starts empty. Pass the test's clock when it moves a fake one: the
    /// stores then use it for deadlines instead of the database clock (<see cref="PostgresStoreOptions.UseDatabaseClock"/>).
    /// </summary>
    public async Task<PostgresStoreOptions> NewSchemaAsync(TimeProvider? clock = null)
    {
        SkipIfUnavailable();
        var options = new PostgresStoreOptions { Schema = "t_" + Guid.NewGuid().ToString("N"), UseDatabaseClock = clock is null };
        await PostgresSchema.EnsureSchemaAsync(DataSource, options.Schema);
        return options;
    }

    public override async Task DisposeAsync()
    {
        if (DataSource is not null) await DataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}

public sealed class RedisFixture : ContainerFixture
{
    private RedisContainer? _container;

    public string ConnectionString { get; private set; } = "";

    protected override async Task StartAsync()
    {
        _container = new RedisBuilder("redis:7-alpine").Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task<ConnectionMultiplexer> ConnectAsync()
    {
        SkipIfUnavailable();
        return await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public override async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}

/// <summary>Tests that need the containers. One collection, so each container starts once per run.</summary>
[CollectionDefinition(Name)]
public sealed class ContainersCollection : ICollectionFixture<PostgresFixture>, ICollectionFixture<RedisFixture>
{
    public const string Name = "containers";
}
