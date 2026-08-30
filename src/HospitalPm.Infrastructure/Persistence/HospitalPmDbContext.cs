using HospitalPm.Domain.Equipment;
using HospitalPm.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Infrastructure.Persistence;

public sealed class HospitalPmDbContext(DbContextOptions<HospitalPmDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, int>(options)
{
    public DbSet<Category> Categories => Set<Category>();

    public DbSet<EquipmentType> EquipmentTypes => Set<EquipmentType>();

    public DbSet<EquipmentTypeCategory> EquipmentTypeCategories => Set<EquipmentTypeCategory>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        ConfigureIdentity(builder);

        builder.Entity<Category>(e =>
        {
            e.ToTable("category");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.DisplayOrder).HasColumnName("display_order");
            e.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasDefaultValueSql("now()");

            // Code is the stable key imports and reports rely on.
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasDatabaseName("ux_category_tenant_code");
        });

        builder.Entity<EquipmentType>(e =>
        {
            e.ToTable("equipment_type");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.IsSeeded).HasColumnName("is_seeded").HasDefaultValue(false);
            e.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasDefaultValueSql("now()");

            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasDatabaseName("ux_equipment_type_tenant_code");
        });

        builder.Entity<EquipmentTypeCategory>(e =>
        {
            e.ToTable("equipment_type_category");
            e.HasKey(x => new { x.EquipmentTypeId, x.CategoryId });
            e.Property(x => x.EquipmentTypeId).HasColumnName("equipment_type_id");
            e.Property(x => x.CategoryId).HasColumnName("category_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.IsPrimary).HasColumnName("is_primary").HasDefaultValue(false);

            e.HasOne(x => x.EquipmentType)
                .WithMany(x => x!.Categories)
                .HasForeignKey(x => x.EquipmentTypeId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Category)
                .WithMany(x => x!.EquipmentTypes)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.CategoryId).HasDatabaseName("ix_equipment_type_category_category");
        });
    }

    /// <summary>
    /// Identity ships PascalCase table names (AspNetUsers) and no tenant
    /// column. Both are remapped so the identity tables obey the same rules
    /// as every other table in this schema.
    /// </summary>
    private static void ConfigureIdentity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApplicationUser>(e =>
        {
            e.ToTable("app_user");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.FullName).HasColumnName("full_name").HasMaxLength(200).IsRequired();
            e.Property(x => x.StaffCode).HasColumnName("staff_code").HasMaxLength(64);
            e.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasDefaultValueSql("now()");
            e.Property(x => x.LastLoginAtUtc).HasColumnName("last_login_at_utc");
            e.Property(x => x.UserName).HasColumnName("user_name").HasMaxLength(256);
            e.Property(x => x.NormalizedUserName).HasColumnName("normalized_user_name").HasMaxLength(256);
            e.Property(x => x.Email).HasColumnName("email").HasMaxLength(256);
            e.Property(x => x.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(256);
            e.Property(x => x.EmailConfirmed).HasColumnName("email_confirmed");
            e.Property(x => x.PasswordHash).HasColumnName("password_hash");
            e.Property(x => x.SecurityStamp).HasColumnName("security_stamp");
            e.Property(x => x.ConcurrencyStamp).HasColumnName("concurrency_stamp");
            e.Property(x => x.PhoneNumber).HasColumnName("phone_number");
            e.Property(x => x.PhoneNumberConfirmed).HasColumnName("phone_number_confirmed");
            e.Property(x => x.TwoFactorEnabled).HasColumnName("two_factor_enabled");
            e.Property(x => x.LockoutEnd).HasColumnName("lockout_end");
            e.Property(x => x.LockoutEnabled).HasColumnName("lockout_enabled");
            e.Property(x => x.AccessFailedCount).HasColumnName("access_failed_count");
        });

        modelBuilder.Entity<ApplicationRole>(e =>
        {
            e.ToTable("app_role");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(256);
            e.Property(x => x.NormalizedName).HasColumnName("normalized_name").HasMaxLength(256);
            e.Property(x => x.ConcurrencyStamp).HasColumnName("concurrency_stamp");
        });

        modelBuilder.Entity<IdentityUserRole<int>>(e =>
        {
            e.ToTable("app_user_role");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.Property<int>("TenantId").HasColumnName("tenant_id").HasDefaultValue(1);
        });

        modelBuilder.Entity<IdentityUserClaim<int>>(e =>
        {
            e.ToTable("app_user_claim");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.ClaimType).HasColumnName("claim_type");
            e.Property(x => x.ClaimValue).HasColumnName("claim_value");
            e.Property<int>("TenantId").HasColumnName("tenant_id").HasDefaultValue(1);
        });

        modelBuilder.Entity<IdentityUserLogin<int>>(e =>
        {
            e.ToTable("app_user_login");
            e.Property(x => x.LoginProvider).HasColumnName("login_provider");
            e.Property(x => x.ProviderKey).HasColumnName("provider_key");
            e.Property(x => x.ProviderDisplayName).HasColumnName("provider_display_name");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property<int>("TenantId").HasColumnName("tenant_id").HasDefaultValue(1);
        });

        modelBuilder.Entity<IdentityUserToken<int>>(e =>
        {
            e.ToTable("app_user_token");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.LoginProvider).HasColumnName("login_provider");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Value).HasColumnName("value");
            e.Property<int>("TenantId").HasColumnName("tenant_id").HasDefaultValue(1);
        });

        modelBuilder.Entity<IdentityRoleClaim<int>>(e =>
        {
            e.ToTable("app_role_claim");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.Property(x => x.ClaimType).HasColumnName("claim_type");
            e.Property(x => x.ClaimValue).HasColumnName("claim_value");
            e.Property<int>("TenantId").HasColumnName("tenant_id").HasDefaultValue(1);
        });

        // Passkeys (WebAuthn) are excluded. They require a browser
        // authenticator ceremony and, in practice, a roaming credential
        // store - neither of which fits an air-gapped hospital install
        // where technicians share ward tablets. Carrying an unused
        // AspNetUserPasskeys table would add schema surface and a review
        // question for no benefit.
        modelBuilder.Ignore<IdentityUserPasskey<int>>();

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_token");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(128).IsRequired();
            e.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");
            e.Property(x => x.RevokedAtUtc).HasColumnName("revoked_at_utc");
            e.Property(x => x.ReplacedByTokenId).HasColumnName("replaced_by_token_id");
            e.Property(x => x.RevokedReason).HasColumnName("revoked_reason").HasMaxLength(200);

            e.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Refresh lookup is by hash: it must be indexed and unique.
            e.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_refresh_token_hash");
            e.HasIndex(x => new { x.UserId, x.ExpiresAtUtc }).HasDatabaseName("ix_refresh_token_user_expiry");
        });
    }
}
