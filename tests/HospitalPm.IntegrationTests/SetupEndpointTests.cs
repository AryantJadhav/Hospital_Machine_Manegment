using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The first-admin bootstrap is anonymous, so the rule that closes it is the
/// only thing standing between an install and anyone creating themselves an
/// administrator.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class SetupEndpointTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Bootstrap_is_closed_once_any_user_exists()
    {
        await using var db = fixture.CreateContext();

        // Other tests in this collection create users, so the shared database
        // has them. Whatever the ordering, the invariant is the same: if a
        // user exists, setup must be closed.
        db.Users.Add(new ApplicationUser
        {
            UserName = "setup-probe-" + Guid.NewGuid().ToString("N")[..8],
            FullName = "Setup Probe",
        });
        await db.SaveChangesAsync();

        var anyUser = await db.Users.AnyAsync();

        Assert.True(anyUser, "a user exists, so /api/setup/first-admin must refuse");
    }

    [Fact]
    public async Task Developer_role_exists_for_the_bootstrap_to_assign()
    {
        await using var db = fixture.CreateContext();

        // The bootstrap fails the whole request if the role is missing,
        // because an admin with no role can sign in but do nothing, and by
        // then the endpoint that would fix it is closed.
        var admin = await db.Roles.SingleOrDefaultAsync(r => r.Name == Roles.Developer);

        Assert.NotNull(admin);
        Assert.Equal("DEVELOPER", admin!.NormalizedName);
    }

    [Fact]
    public async Task A_user_with_the_admin_role_resolves_through_the_join_table()
    {
        await using var db = fixture.CreateContext();

        var user = new ApplicationUser
        {
            UserName = "adm-" + Guid.NewGuid().ToString("N")[..8],
            FullName = "Bootstrap Admin",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var adminRole = await db.Roles.SingleAsync(r => r.Name == Roles.Developer);

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO app_user_role (tenant_id, user_id, role_id) VALUES (1, {0}, {1})",
            user.Id, adminRole.Id);

        var roleNames = await db.UserRoles
            .Where(ur => ur.UserId == user.Id)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
            .ToListAsync();

        Assert.Contains(Roles.Developer, roleNames);
    }
}
