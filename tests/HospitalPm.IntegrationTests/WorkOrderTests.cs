using HospitalPm.Domain.Locations;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>The transition table, with no database involved.</summary>
public sealed class WorkOrderTransitionTests
{
    [Theory]
    [InlineData(WorkOrderStatus.Reported, WorkOrderStatus.Assigned)]
    [InlineData(WorkOrderStatus.Reported, WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.Assigned, WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.Assigned, WorkOrderStatus.Reported)]
    [InlineData(WorkOrderStatus.InProgress, WorkOrderStatus.OnHold)]
    [InlineData(WorkOrderStatus.InProgress, WorkOrderStatus.Resolved)]
    [InlineData(WorkOrderStatus.OnHold, WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.Resolved, WorkOrderStatus.Closed)]
    public void Allowed_moves_are_allowed(WorkOrderStatus from, WorkOrderStatus to)
        => Assert.True(WorkOrderTransitions.CanMove(from, to));

    [Fact]
    public void A_resolved_order_can_be_reopened()
    {
        // "We thought it was fixed" is the commonest thing that happens to a
        // resolved ticket. Forcing a new one would break the link between a
        // fault and its real repair history.
        Assert.True(WorkOrderTransitions.CanMove(WorkOrderStatus.Resolved, WorkOrderStatus.InProgress));
    }

    [Theory]
    [InlineData(WorkOrderStatus.Reported, WorkOrderStatus.Resolved)]   // never worked on
    [InlineData(WorkOrderStatus.Reported, WorkOrderStatus.Closed)]     // never resolved
    [InlineData(WorkOrderStatus.Assigned, WorkOrderStatus.Resolved)]   // never started
    [InlineData(WorkOrderStatus.OnHold, WorkOrderStatus.Resolved)]     // resume first
    [InlineData(WorkOrderStatus.Closed, WorkOrderStatus.InProgress)]
    [InlineData(WorkOrderStatus.Cancelled, WorkOrderStatus.Reported)]
    public void Disallowed_moves_are_refused(WorkOrderStatus from, WorkOrderStatus to)
        => Assert.False(WorkOrderTransitions.CanMove(from, to));

    [Fact]
    public void Closed_and_cancelled_are_terminal()
    {
        Assert.True(WorkOrderTransitions.IsTerminal(WorkOrderStatus.Closed));
        Assert.True(WorkOrderTransitions.IsTerminal(WorkOrderStatus.Cancelled));
        Assert.False(WorkOrderTransitions.IsTerminal(WorkOrderStatus.Resolved));
    }

    [Fact]
    public void Every_open_status_can_be_cancelled()
    {
        // A ticket raised in error must always have a way out that is not
        // pretending it was fixed.
        foreach (var status in new[]
                 {
                     WorkOrderStatus.Reported, WorkOrderStatus.Assigned,
                     WorkOrderStatus.InProgress, WorkOrderStatus.OnHold,
                 })
        {
            Assert.True(WorkOrderTransitions.CanMove(status, WorkOrderStatus.Cancelled));
        }
    }

    [Fact]
    public void A_refusal_explains_itself_in_operator_terms()
    {
        var closed = WorkOrderTransitions.Explain(WorkOrderStatus.Closed, WorkOrderStatus.InProgress);
        Assert.Contains("Raise a new one", closed, StringComparison.Ordinal);

        var wrong = WorkOrderTransitions.Explain(WorkOrderStatus.Reported, WorkOrderStatus.Closed);
        Assert.Contains("can only move to", wrong, StringComparison.Ordinal);
        // Enum names would be meaningless to a biomedical head.
        Assert.DoesNotContain("InProgress", wrong, StringComparison.Ordinal);
    }
}

[Collection(nameof(PostgresCollection))]
public sealed class WorkOrderDatabaseTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_reported_order_gets_a_quotable_number()
    {
        await using var db = fixture.CreateContext();
        var order = await ReportAsync(db);

        await db.Entry(order).ReloadAsync();

        Assert.StartsWith("WO-", order.Number, StringComparison.Ordinal);
        Assert.Matches(@"^WO-\d{4}-\d{6}$", order.Number);
    }

    [Fact]
    public async Task Numbers_are_unique_across_concurrent_reports()
    {
        await using var db = fixture.CreateContext();

        var orders = new List<WorkOrder>();
        for (var i = 0; i < 5; i++)
        {
            orders.Add(await ReportAsync(db));
        }

        foreach (var o in orders)
        {
            await db.Entry(o).ReloadAsync();
        }

        // A sequence, not a count-and-increment: two wards reporting in the
        // same second must not collide.
        Assert.Equal(orders.Count, orders.Select(o => o.Number).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task The_database_refuses_an_invalid_transition()
    {
        await using var db = fixture.CreateContext();
        var order = await ReportAsync(db);

        // Reported straight to Closed skips everything that makes a work
        // order mean anything. Refused at the database, not just in the API.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE work_order SET status = 60 WHERE id = {0}", order.Id));

        Assert.Contains("cannot move", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_cancelled_order_cannot_be_revived()
    {
        await using var db = fixture.CreateContext();
        var order = await ReportAsync(db);

        order.Status = WorkOrderStatus.Cancelled;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE work_order SET status = 10 WHERE id = {0}", order.Id));

        Assert.Contains("cannot move", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolving_without_notes_is_refused()
    {
        await using var db = fixture.CreateContext();
        var order = await ReportAsync(db);

        order.Status = WorkOrderStatus.InProgress;
        await db.SaveChangesAsync();

        // "Fixed" with no explanation is worthless in a recurring-fault review.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE work_order SET status = 50, resolved_at_utc = now(), resolved_by_user_id = 1 WHERE id = {0}",
                order.Id));

        Assert.Equal("23514", ex.SqlState); // check_violation
    }

    [Fact]
    public async Task A_closed_order_cannot_have_its_resolution_rewritten()
    {
        await using var db = fixture.CreateContext();
        var order = await ResolvedAsync(db);

        order.Status = WorkOrderStatus.Closed;
        order.ClosedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE work_order SET resolution_notes = 'Something else entirely' WHERE id = {0}",
                order.Id));

        Assert.Contains("cannot be rewritten", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_resolved_order_may_still_be_reopened_in_the_database()
    {
        await using var db = fixture.CreateContext();
        var order = await ResolvedAsync(db);

        order.Status = WorkOrderStatus.InProgress;
        await db.SaveChangesAsync();

        var reloaded = await db.WorkOrders.AsNoTracking().SingleAsync(w => w.Id == order.Id);
        Assert.Equal(WorkOrderStatus.InProgress, reloaded.Status);
    }

    [Fact]
    public async Task Downtime_cannot_run_backwards()
    {
        await using var db = fixture.CreateContext();
        var order = await ReportAsync(db);

        order.OutOfServiceAtUtc = DateTime.UtcNow;
        order.BackInServiceAtUtc = DateTime.UtcNow.AddHours(-2);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Downtime_is_measured_from_report_not_from_first_touch()
    {
        await using var db = fixture.CreateContext();
        var order = await ReportAsync(db, outOfService: true);

        order.Status = WorkOrderStatus.InProgress;
        await db.SaveChangesAsync();

        order.Status = WorkOrderStatus.Resolved;
        order.ResolutionNotes = "Replaced flow sensor";
        order.ResolvedByUserId = order.ReportedByUserId;
        order.ResolvedAtUtc = DateTime.UtcNow;
        order.BackInServiceAtUtc = order.OutOfServiceAtUtc!.Value.AddHours(3);
        await db.SaveChangesAsync();

        // Downtime that started when an engineer got round to it would
        // understate every uptime figure the hospital reports.
        Assert.Equal(180, order.DowntimeMinutes);
    }

    [Fact]
    public async Task Notes_are_append_only()
    {
        await using var db = fixture.CreateContext();
        var order = await ReportAsync(db);

        db.WorkOrderNotes.Add(new WorkOrderNote
        {
            WorkOrderId = order.Id,
            Body = "Called the vendor",
            AuthorUserId = order.ReportedByUserId,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Editing a note rewrites a conversation other people acted on.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE work_order_note SET body = 'Never mind' WHERE work_order_id = {0}", order.Id));

        Assert.Contains("append-only", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Work_orders_are_audited()
    {
        await using var db = fixture.CreateContext();
        var order = await ReportAsync(db);

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE table_name = 'work_order' AND record_pk = @pk", conn);
        cmd.Parameters.AddWithValue("pk", order.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(
            Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task A_work_order_holds_no_patient_columns()
    {
        await using var db = fixture.CreateContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        // A breakdown ticket is the likeliest place for clinical detail to
        // creep in, because a ward describing a fault naturally reaches for
        // it. The fault description is free text and the hospital's own
        // business; what must never exist is a structured field inviting it.
        const string sql = @"
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name IN ('work_order', 'work_order_note')
              AND (column_name LIKE '%patient%' OR column_name LIKE '%mrn%'
                OR column_name LIKE '%diagnos%' OR column_name = 'uhid')";

        await using var cmd = new NpgsqlCommand(sql, conn);
        var offenders = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) offenders.Add(reader.GetString(0));

        Assert.Empty(offenders);
    }

    // ---------------- helpers ----------------

    private static async Task<WorkOrder> ReportAsync(HospitalPmDbContext db, bool outOfService = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var room = new Location { Code = $"WOR-{suffix}", Name = $"Room {suffix}", Level = LocationLevel.Room };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");

        var equipment = new Domain.Assets.Equipment
        {
            AssetTag = $"WO-{suffix}".ToUpperInvariant(),
            EquipmentTypeId = type.Id,
            LocationId = room.Id,
        };
        db.Equipment.Add(equipment);

        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = "wo-" + suffix,
            FullName = "Work Order Test User",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;

        var order = new WorkOrder
        {
            EquipmentId = equipment.Id,
            FaultDescription = "Alarm sounding continuously, display blank",
            Priority = WorkOrderPriority.High,
            ReportedByUserId = user.Id,
            ReportedAtUtc = now,
            OutOfServiceAtUtc = outOfService ? now : null,
        };

        db.WorkOrders.Add(order);
        await db.SaveChangesAsync();

        return order;
    }

    private static async Task<WorkOrder> ResolvedAsync(HospitalPmDbContext db)
    {
        var order = await ReportAsync(db);

        order.Status = WorkOrderStatus.InProgress;
        await db.SaveChangesAsync();

        order.Status = WorkOrderStatus.Resolved;
        order.ResolutionNotes = "Replaced the flow sensor and recalibrated";
        order.ResolvedByUserId = order.ReportedByUserId;
        order.ResolvedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return order;
    }
}
