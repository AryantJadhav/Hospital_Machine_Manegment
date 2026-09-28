using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// A real PostgreSQL 18 in a container, matching what a hospital runs.
/// Not an in-memory provider: triggers, partial indexes and jsonb are the
/// things under test here, and none of them exist in an in-memory fake.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Empty database to current schema in one step - the Phase 0 gate.
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public HospitalPmDbContext CreateContext()
        => new(new DbContextOptionsBuilder<HospitalPmDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
