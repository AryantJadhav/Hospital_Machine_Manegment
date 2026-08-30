using HospitalPm.Domain.Equipment;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Infrastructure.Persistence;

public sealed class HospitalPmDbContext(DbContextOptions<HospitalPmDbContext> options)
    : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();

    public DbSet<EquipmentType> EquipmentTypes => Set<EquipmentType>();

    public DbSet<EquipmentTypeCategory> EquipmentTypeCategories => Set<EquipmentTypeCategory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>(e =>
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

        modelBuilder.Entity<EquipmentType>(e =>
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

        modelBuilder.Entity<EquipmentTypeCategory>(e =>
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
}
