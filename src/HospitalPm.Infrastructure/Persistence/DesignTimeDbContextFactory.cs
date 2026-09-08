using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HospitalPm.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef`. Never runs at runtime.
///
/// `migrations add` does not open this connection - EF needs a configured
/// provider, not a reachable server, to scaffold. `database update` does,
/// and it ignores ConnectionStrings__HospitalPm because this factory wins
/// over the startup project's configuration. So a hand-run `database update`
/// goes to hospitalpm_design on the local server, whatever the environment
/// says. That is a scratch database on a developer machine and nothing else
/// should ever point at it.
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
