using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Inventory;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Domain.Operations;
using HospitalPm.Domain.Locations;
using HospitalPm.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Text.Json;

namespace HospitalPm.Infrastructure.Persistence;

public sealed class HospitalPmDbContext(DbContextOptions<HospitalPmDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, int>(options)
{
    /// <summary>
    /// Fixed on purpose. These documents outlive the code that wrote them, so
    /// the property naming must not drift with a future default.
    /// </summary>
    private static readonly JsonSerializerOptions ChecklistJson = new(JsonSerializerDefaults.Web);

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<EquipmentType> EquipmentTypes => Set<EquipmentType>();

    public DbSet<EquipmentTypeCategory> EquipmentTypeCategories => Set<EquipmentTypeCategory>();

    public DbSet<SparePart> SpareParts => Set<SparePart>();

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<Equipment> Equipment => Set<Equipment>();

    public DbSet<EquipmentMove> EquipmentMoves => Set<EquipmentMove>();

    public DbSet<ChecklistTemplate> ChecklistTemplates => Set<ChecklistTemplate>();

    public DbSet<ChecklistTemplateVersion> ChecklistTemplateVersions => Set<ChecklistTemplateVersion>();

    public DbSet<PmSchedule> PmSchedules => Set<PmSchedule>();

    public DbSet<PmTask> PmTasks => Set<PmTask>();

    public DbSet<PmCompletion> PmCompletions => Set<PmCompletion>();

    public DbSet<PmTaskAttachment> PmTaskAttachments => Set<PmTaskAttachment>();

    public DbSet<PmTaskAttachmentData> PmTaskAttachmentData => Set<PmTaskAttachmentData>();

    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();

    public DbSet<WorkOrderNote> WorkOrderNotes => Set<WorkOrderNote>();

    public DbSet<WorkOrderPart> WorkOrderParts => Set<WorkOrderPart>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();

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

        builder.Entity<SparePart>(e =>
        {
            e.ToTable("spare_part");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.PartNumber).HasColumnName("part_number").HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.EquipmentTypeId).HasColumnName("equipment_type_id");
            e.Property(x => x.Unit).HasColumnName("unit").HasMaxLength(20).HasDefaultValue("pcs");
            e.Property(x => x.QuantityOnHand).HasColumnName("quantity_on_hand").HasDefaultValue(0);
            e.Property(x => x.ReorderLevel).HasColumnName("reorder_level").HasDefaultValue(0);
            e.Property(x => x.UnitCost).HasColumnName("unit_cost").HasColumnType("numeric(12,2)");
            e.Property(x => x.Supplier).HasColumnName("supplier").HasMaxLength(200);
            e.Property(x => x.StorageLocation).HasColumnName("storage_location").HasMaxLength(200);
            e.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasDefaultValueSql("now()");

            e.HasOne(x => x.EquipmentType)
                .WithMany()
                .HasForeignKey(x => x.EquipmentTypeId)
                // Restrict: a type still stocked for cannot be removed out from under its parts.
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.TenantId, x.PartNumber }).IsUnique().HasDatabaseName("ux_spare_part_tenant_number");
            e.HasIndex(x => x.EquipmentTypeId).HasDatabaseName("ix_spare_part_equipment_type");
            // The low-stock list on the register page: active parts at or below
            // their reorder level, which is exactly this ordering.
            e.HasIndex(x => new { x.IsActive, x.QuantityOnHand }).HasDatabaseName("ix_spare_part_active_quantity");
        });

        builder.Entity<PmTaskAttachment>(e =>
        {
            e.ToTable("pm_task_attachment");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.PmTaskId).HasColumnName("pm_task_id");
            e.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(255).IsRequired();
            e.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(100).IsRequired();
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            e.Property(x => x.UploadedByUserId).HasColumnName("uploaded_by_user_id");
            e.Property(x => x.UploadedAtUtc).HasColumnName("uploaded_at_utc").HasDefaultValueSql("now()");

            // A completed PM cannot be deleted, so its files are never orphaned by that; the
            // Restrict is what stops a task being removed while it still has any.
            e.HasOne(x => x.Task)
                .WithMany()
                .HasForeignKey(x => x.PmTaskId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Data)
                .WithOne(x => x.Attachment)
                .HasForeignKey<PmTaskAttachmentData>(x => x.AttachmentId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.PmTaskId).HasDatabaseName("ix_pm_task_attachment_task");
        });

        builder.Entity<PmTaskAttachmentData>(e =>
        {
            e.ToTable("pm_task_attachment_data");
            e.HasKey(x => x.AttachmentId);
            e.Property(x => x.AttachmentId).HasColumnName("attachment_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.Data).HasColumnName("data").IsRequired();
        });

        builder.Entity<ChecklistTemplate>(e =>
        {
            e.ToTable("checklist_template");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.EquipmentTypeId).HasColumnName("equipment_type_id");
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Kind).HasColumnName("kind").HasConversion<int>().HasDefaultValue(ChecklistKind.Pm);
            e.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasDefaultValueSql("now()");

            e.HasOne(x => x.EquipmentType)
                .WithMany()
                .HasForeignKey(x => x.EquipmentTypeId)
                // Restrict: an equipment type still referenced by a checklist
                // cannot be removed out from under completed work.
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasDatabaseName("ux_checklist_template_tenant_code");
            e.HasIndex(x => x.EquipmentTypeId).HasDatabaseName("ix_checklist_template_equipment_type");
        });

        builder.Entity<ChecklistTemplateVersion>(e =>
        {
            e.ToTable("checklist_template_version");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.ChecklistTemplateId).HasColumnName("checklist_template_id");
            e.Property(x => x.VersionNo).HasColumnName("version_no");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
            e.Property(x => x.ChangeNote).HasColumnName("change_note");
            e.Property(x => x.PublishedAtUtc).HasColumnName("published_at_utc");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasDefaultValueSql("now()");

            // jsonb, not json: it is indexable, and Postgres validates and
            // normalises it on write rather than storing whatever arrived.
            //
            // Serialised explicitly rather than by enabling Npgsql's dynamic
            // JSON. Dynamic JSON is a global, reflection-based opt-in; doing
            // it here keeps the shape of stored definitions under this
            // mapping's control, which matters for data that has to remain
            // readable years after it was written.
            e.Property(x => x.Definition)
                .HasColumnName("definition")
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, ChecklistJson),
                    v => JsonSerializer.Deserialize<ChecklistDefinition>(v, ChecklistJson) ?? new ChecklistDefinition(),
                    // EF cannot see edits inside a mutable object graph without
                    // a comparer, so a changed question would not be saved.
                    new ValueComparer<ChecklistDefinition>(
                        (a, b) => JsonSerializer.Serialize(a, ChecklistJson) == JsonSerializer.Serialize(b, ChecklistJson),
                        v => JsonSerializer.Serialize(v, ChecklistJson).GetHashCode(StringComparison.Ordinal),
                        v => JsonSerializer.Deserialize<ChecklistDefinition>(
                                 JsonSerializer.Serialize(v, ChecklistJson), ChecklistJson)!))
                .IsRequired();

            e.HasOne(x => x.Template)
                .WithMany(x => x!.Versions)
                .HasForeignKey(x => x.ChecklistTemplateId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.ChecklistTemplateId, x.VersionNo })
                .HasDatabaseName("ix_checklist_version_template_no");
        });

        builder.Entity<PmSchedule>(e =>
        {
            e.ToTable("pm_schedule");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.EquipmentId).HasColumnName("equipment_id");
            e.Property(x => x.ChecklistTemplateId).HasColumnName("checklist_template_id");
            e.Property(x => x.Frequency).HasColumnName("frequency").HasConversion<int>();
            e.Property(x => x.IntervalDays).HasColumnName("interval_days");
            e.Property(x => x.AnchorDate).HasColumnName("anchor_date");
            e.Property(x => x.GraceDays).HasColumnName("grace_days");
            e.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            e.Property(x => x.PerformedBy).HasColumnName("performed_by").HasConversion<int>().HasDefaultValue(PmPerformedBy.InHouse);
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasDefaultValueSql("now()");

            e.HasOne(x => x.Equipment)
                .WithMany()
                .HasForeignKey(x => x.EquipmentId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.ChecklistTemplate)
                .WithMany()
                .HasForeignKey(x => x.ChecklistTemplateId)
                // Restrict: a checklist still driving a live schedule cannot
                // be pulled out from under it.
                .OnDelete(DeleteBehavior.Restrict);

            // One schedule per checklist per machine. Two would generate
            // duplicate work and double-count in compliance reporting.
            e.HasIndex(x => new { x.EquipmentId, x.ChecklistTemplateId })
                .IsUnique()
                .HasDatabaseName("ux_pm_schedule_equipment_checklist");

            // A PM with no checklist is not covered by the index above, which sees every empty
            // checklist as different. One such schedule per machine for each of who does it, the
            // team or the vendor, so a machine is not scheduled twice for the same thing.
            e.HasIndex(x => new { x.EquipmentId, x.PerformedBy })
                .IsUnique()
                .HasFilter("checklist_template_id IS NULL")
                .HasDatabaseName("ux_pm_schedule_equipment_plain");
        });

        builder.Entity<PmTask>(e =>
        {
            e.ToTable("pm_task");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.PmScheduleId).HasColumnName("pm_schedule_id");
            e.Property(x => x.EquipmentId).HasColumnName("equipment_id");
            e.Property(x => x.DueDate).HasColumnName("due_date");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
            e.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
            e.Property(x => x.CompletedByUserId).HasColumnName("completed_by_user_id");
            e.Property(x => x.ChecklistTemplateVersionId).HasColumnName("checklist_template_version_id");
            e.Property(x => x.SkipReason).HasColumnName("skip_reason").HasMaxLength(500);
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasDefaultValueSql("now()");

            e.HasOne(x => x.Schedule)
                .WithMany(x => x!.Tasks)
                .HasForeignKey(x => x.PmScheduleId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Equipment)
                .WithMany()
                .HasForeignKey(x => x.EquipmentId)
                .OnDelete(DeleteBehavior.Restrict);

            // The generator runs nightly and must be safe to run twice.
            e.HasIndex(x => new { x.PmScheduleId, x.DueDate })
                .IsUnique()
                .HasDatabaseName("ux_pm_task_schedule_due");

            e.HasIndex(x => new { x.Status, x.DueDate }).HasDatabaseName("ix_pm_task_status_due");
            e.HasIndex(x => x.EquipmentId).HasDatabaseName("ix_pm_task_equipment");
        });

        builder.Entity<PmCompletion>(e =>
        {
            e.ToTable("pm_completion");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.PmTaskId).HasColumnName("pm_task_id");
            e.Property(x => x.ChecklistTemplateVersionId).HasColumnName("checklist_template_version_id");
            e.Property(x => x.Signature).HasColumnName("signature");
            e.Property(x => x.SignatureFormat).HasColumnName("signature_format").HasMaxLength(8);
            e.Property(x => x.SignedByName).HasColumnName("signed_by_name").HasMaxLength(200);
            e.Property(x => x.CompletedByUserId).HasColumnName("completed_by_user_id");
            e.Property(x => x.CompletedAtUtc).HasColumnName("completed_at_utc");
            e.Property(x => x.PerformedAtUtc).HasColumnName("performed_at_utc");
            e.Property(x => x.ClientSubmissionId).HasColumnName("client_submission_id");
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.PerformedBy).HasColumnName("performed_by").HasConversion<int>().HasDefaultValue(PmPerformedBy.InHouse);
            e.Property(x => x.VendorName).HasColumnName("vendor_name").HasMaxLength(200);
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");

            e.Property(x => x.Answers)
                .HasColumnName("answers")
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, ChecklistJson),
                    v => JsonSerializer.Deserialize<Dictionary<string, ChecklistAnswer>>(v, ChecklistJson)
                         ?? new Dictionary<string, ChecklistAnswer>(),
                    new ValueComparer<Dictionary<string, ChecklistAnswer>>(
                        (a, b) => JsonSerializer.Serialize(a, ChecklistJson) == JsonSerializer.Serialize(b, ChecklistJson),
                        v => JsonSerializer.Serialize(v, ChecklistJson).GetHashCode(StringComparison.Ordinal),
                        v => JsonSerializer.Deserialize<Dictionary<string, ChecklistAnswer>>(
                                 JsonSerializer.Serialize(v, ChecklistJson), ChecklistJson)!))
                .IsRequired();

            e.HasOne(x => x.Task)
                .WithOne()
                .HasForeignKey<PmCompletion>(x => x.PmTaskId)
                // Restrict: a completed task cannot be deleted anyway, and
                // cascading would make erasing the evidence a side effect.
                .OnDelete(DeleteBehavior.Restrict);

            // One completion per task.
            e.HasIndex(x => x.PmTaskId).IsUnique().HasDatabaseName("ux_pm_completion_task");

            // Replay safety for the offline write queue.
            e.HasIndex(x => x.ClientSubmissionId)
                .IsUnique()
                .HasFilter("client_submission_id IS NOT NULL")
                .HasDatabaseName("ux_pm_completion_client_submission");
        });

        builder.Entity<WorkOrder>(e =>
        {
            e.ToTable("work_order");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            // Assigned by a database default from a sequence, so concurrent
            // reports cannot collide on a number.
            e.Property(x => x.Number).HasColumnName("number").HasMaxLength(32)
                .ValueGeneratedOnAdd().HasDefaultValueSql("''");
            e.Property(x => x.EquipmentId).HasColumnName("equipment_id");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
            e.Property(x => x.Priority).HasColumnName("priority").HasConversion<int>();
            e.Property(x => x.FaultDescription).HasColumnName("fault_description").IsRequired();
            e.Property(x => x.ReportedByUserId).HasColumnName("reported_by_user_id");
            e.Property(x => x.ReportedAtUtc).HasColumnName("reported_at_utc");
            e.Property(x => x.AssignedToUserId).HasColumnName("assigned_to_user_id");
            e.Property(x => x.AssignedAtUtc).HasColumnName("assigned_at_utc");
            e.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc");
            e.Property(x => x.ResolutionNotes).HasColumnName("resolution_notes");
            e.Property(x => x.ResolvedByUserId).HasColumnName("resolved_by_user_id");
            e.Property(x => x.ResolvedAtUtc).HasColumnName("resolved_at_utc");
            e.Property(x => x.ClosedAtUtc).HasColumnName("closed_at_utc");
            e.Property(x => x.OutOfServiceAtUtc).HasColumnName("out_of_service_at_utc");
            e.Property(x => x.BackInServiceAtUtc).HasColumnName("back_in_service_at_utc");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasDefaultValueSql("now()");
            e.Ignore(x => x.DowntimeMinutes);

            e.HasOne(x => x.Equipment)
                .WithMany()
                .HasForeignKey(x => x.EquipmentId)
                // Restrict: a machine's fault history is part of its record.
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique().HasDatabaseName("ux_work_order_number");
            e.HasIndex(x => x.EquipmentId).HasDatabaseName("ix_work_order_equipment");
            e.HasIndex(x => new { x.Status, x.Priority }).HasDatabaseName("ix_work_order_status_priority");
            e.HasIndex(x => x.AssignedToUserId).HasDatabaseName("ix_work_order_assignee");
        });

        builder.Entity<WorkOrderNote>(e =>
        {
            e.ToTable("work_order_note");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.WorkOrderId).HasColumnName("work_order_id");
            e.Property(x => x.Body).HasColumnName("body").IsRequired();
            e.Property(x => x.StatusAfter).HasColumnName("status_after").HasConversion<int?>();
            e.Property(x => x.AuthorUserId).HasColumnName("author_user_id");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");

            e.HasOne(x => x.WorkOrder)
                .WithMany(x => x!.Notes)
                .HasForeignKey(x => x.WorkOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.WorkOrderId, x.CreatedAtUtc })
                .HasDatabaseName("ix_work_order_note_timeline");
        });

        builder.Entity<WorkOrderPart>(e =>
        {
            e.ToTable("work_order_part");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.WorkOrderId).HasColumnName("work_order_id");
            e.Property(x => x.SparePartId).HasColumnName("spare_part_id");
            e.Property(x => x.QuantityUsed).HasColumnName("quantity_used");
            e.Property(x => x.UnitCostAtUse).HasColumnName("unit_cost_at_use").HasColumnType("numeric(12,2)");
            e.Property(x => x.UsedByUserId).HasColumnName("used_by_user_id");
            e.Property(x => x.UsedAtUtc).HasColumnName("used_at_utc").HasDefaultValueSql("now()");

            e.HasOne(x => x.WorkOrder)
                .WithMany(x => x!.PartsUsed)
                .HasForeignKey(x => x.WorkOrderId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.SparePart)
                .WithMany()
                .HasForeignKey(x => x.SparePartId)
                // Restrict: a part with repair history behind it cannot be
                // removed out from under that record.
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.WorkOrderId).HasDatabaseName("ix_work_order_part_order");
            e.HasIndex(x => x.SparePartId).HasDatabaseName("ix_work_order_part_spare_part");
        });

        builder.Entity<BackupRun>(e =>
        {
            e.ToTable("backup_run");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.StartedAtUtc).HasColumnName("started_at_utc").HasDefaultValueSql("now()");
            e.Property(x => x.FinishedAtUtc).HasColumnName("finished_at_utc");
            e.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
            e.Property(x => x.Trigger).HasColumnName("trigger").HasConversion<int>();
            e.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(200);
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.ServerVersion).HasColumnName("server_version").HasMaxLength(50);
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");

            // The dashboard's only question is "what happened most recently",
            // asked on every page load.
            e.HasIndex(x => x.StartedAtUtc).HasDatabaseName("ix_backup_run_recent")
                .IsDescending();
        });

        builder.Entity<Location>(e =>
        {
            e.ToTable("location");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.ParentId).HasColumnName("parent_id");
            e.Property(x => x.Level).HasColumnName("level").HasConversion<int>();
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Path).HasColumnName("path").HasMaxLength(1000);
            e.Property(x => x.Depth).HasColumnName("depth");
            e.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasDefaultValueSql("now()");

            e.HasOne(x => x.Parent)
                .WithMany(x => x!.Children)
                .HasForeignKey(x => x.ParentId)
                // Restrict, not Cascade. Deleting a site must not silently
                // take every department, room and their equipment with it.
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasDatabaseName("ux_location_tenant_code");
            e.HasIndex(x => x.ParentId).HasDatabaseName("ix_location_parent");
            e.HasIndex(x => x.Path).HasDatabaseName("ix_location_path");
        });

        builder.Entity<EquipmentMove>(e =>
        {
            e.ToTable("equipment_move");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.EquipmentId).HasColumnName("equipment_id");
            e.Property(x => x.FromLocationId).HasColumnName("from_location_id");
            e.Property(x => x.ToLocationId).HasColumnName("to_location_id");
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500);
            e.Property(x => x.MovedByUserId).HasColumnName("moved_by_user_id");
            e.Property(x => x.MovedAtUtc).HasColumnName("moved_at_utc");

            e.HasOne<Equipment>().WithMany().HasForeignKey(x => x.EquipmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.FromLocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.ToLocationId).OnDelete(DeleteBehavior.Restrict);

            // "Where has this machine been", newest first.
            e.HasIndex(x => new { x.EquipmentId, x.MovedAtUtc }).HasDatabaseName("ix_equipment_move_equipment");
        });

        builder.Entity<Equipment>(e =>
        {
            e.ToTable("equipment");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired().HasDefaultValue(1);
            e.Property(x => x.AssetTag).HasColumnName("asset_tag").HasMaxLength(64).IsRequired();
            e.Property(x => x.SerialNumber).HasColumnName("serial_number").HasMaxLength(128);
            e.Property(x => x.EquipmentTypeId).HasColumnName("equipment_type_id");
            e.Property(x => x.LocationId).HasColumnName("location_id");
            e.Property(x => x.Manufacturer).HasColumnName("manufacturer").HasMaxLength(200);
            e.Property(x => x.Model).HasColumnName("model").HasMaxLength(200);
            e.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
            e.Property(x => x.Criticality).HasColumnName("criticality").HasConversion<int?>();
            e.Property(x => x.PurchaseDate).HasColumnName("purchase_date");
            e.Property(x => x.PurchaseCost).HasColumnName("purchase_cost").HasPrecision(14, 2);
            e.Property(x => x.InstallationDate).HasColumnName("installation_date");
            e.Property(x => x.WarrantyExpiryDate).HasColumnName("warranty_expiry_date");
            e.Property(x => x.IsInsured).HasColumnName("is_insured").HasDefaultValue(false);
            e.Property(x => x.InsuranceProvider).HasColumnName("insurance_provider").HasMaxLength(200);
            e.Property(x => x.InsurancePolicyNumber).HasColumnName("insurance_policy_number").HasMaxLength(100);
            e.Property(x => x.InsuranceExpiryDate).HasColumnName("insurance_expiry_date");
            e.Property(x => x.InsuranceCost).HasColumnName("insurance_cost").HasPrecision(14, 2);
            e.Property(x => x.MaintenanceContractType).HasColumnName("maintenance_contract_type").HasConversion<int?>();
            e.Property(x => x.MaintenanceVendor).HasColumnName("maintenance_vendor").HasMaxLength(200);
            e.Property(x => x.MaintenanceContractNumber).HasColumnName("maintenance_contract_number").HasMaxLength(100);
            e.Property(x => x.MaintenanceStartDate).HasColumnName("maintenance_start_date");
            e.Property(x => x.MaintenanceEndDate).HasColumnName("maintenance_end_date");
            e.Property(x => x.MaintenanceCost).HasColumnName("maintenance_cost").HasPrecision(14, 2);
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasDefaultValueSql("now()");

            e.HasOne(x => x.EquipmentType)
                .WithMany()
                .HasForeignKey(x => x.EquipmentTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Location)
                .WithMany()
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Restrict);

            // The asset tag is what a QR scan resolves. It must be unique or
            // a scan is ambiguous at the bedside.
            e.HasIndex(x => new { x.TenantId, x.AssetTag }).IsUnique().HasDatabaseName("ux_equipment_tenant_asset_tag");

            // Serial is not unique (hospitals hand over blanks and
            // duplicates) but is searched constantly.
            e.HasIndex(x => x.SerialNumber).HasDatabaseName("ix_equipment_serial");
            e.HasIndex(x => x.LocationId).HasDatabaseName("ix_equipment_location");
            e.HasIndex(x => x.EquipmentTypeId).HasDatabaseName("ix_equipment_type");
            e.HasIndex(x => x.Status).HasDatabaseName("ix_equipment_status");
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
