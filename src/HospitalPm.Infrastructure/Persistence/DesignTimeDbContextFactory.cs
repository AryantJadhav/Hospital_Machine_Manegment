using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HospitalPm.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef` when generating migrations. Never runs at
/// runtime, and the connection string here is never opened — EF needs a
/// configured provider, not a reachable server, to scaffold a migration.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<HospitalPmDbContext>
{
    public HospitalPmDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<HospitalPmDbContext>()
            .UseNpgsql("Host=localhost;Database=hospitalpm_design;Username=postgres")
            .Options;

        return new HospitalPmDbContext(options);
    }
}
