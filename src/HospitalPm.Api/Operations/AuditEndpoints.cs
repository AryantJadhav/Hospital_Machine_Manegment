using System.Globalization;
using System.Text.Json;
using HospitalPm.Api.Auth;
using HospitalPm.Api.Reports;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace HospitalPm.Api.Operations;

/// <summary>One line of the audit log as the database holds it.</summary>
public sealed class AuditRow
{
    public long Id { get; set; }

    public string TableName { get; set; } = string.Empty;

    public string RecordPk { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    public string? OldData { get; set; }

    public string? NewData { get; set; }

    public DateTime ChangedAtUtc { get; set; }

    public string? ChangedBy { get; set; }
}

/// <summary>
/// The audit log, readable: who changed what, and when.
///
/// The log itself is written by database triggers and cannot be edited or deleted; this only reads it.
/// For the IT team and the Developer, because it answers "who did that?" and nobody else needs to ask.
///
/// What it never shows is a secret. A staff account's password hash and security stamp are removed by the
/// database before the row is written, and removed again here before anything is searched or sent, so
/// neither can be read and the search cannot be used to guess them one character at a time.
/// </summary>
public static class AuditEndpoints
{
    private const int MaxPageSize = 100;
    private const int DefaultDays = 30;
    private const int MaxFieldsPerRow = 40;
    private const int MaxValueLength = 200;

    /// <summary>Never shown, never searched.</summary>
    private static readonly string[] Secret = ["password_hash", "security_stamp", "concurrency_stamp"];

    /// <summary>Changes nobody wants to read: they happen whenever anything does.</summary>
    private static readonly string[] Noise = ["tenant_id", "updated_at_utc", "created_at_utc", "concurrency_stamp", "security_stamp"];

    /// <summary>What a row is called, in order of preference: the first of these it has.</summary>
    private static readonly string[] Names =
        ["asset_tag", "number", "part_number", "user_name", "title", "name", "code", "permission"];

    /// <summary>A table as a person would name it. Anything not here is named from its own name.</summary>
    private static readonly Dictionary<string, string> TableLabels = new(StringComparer.Ordinal)
    {
        ["app_user"] = "Staff account",
        ["app_role"] = "Role",
        ["app_user_role"] = "Staff role",
        ["user_permission"] = "Access given or taken away",
        ["user_location"] = "Department given to a person",
        ["equipment"] = "Equipment",
        ["equipment_type"] = "Kind of machine",
        ["equipment_type_category"] = "Kind of machine's category",
        ["equipment_move"] = "Machine move",
        ["equipment_insurance_history"] = "Past insurance",
        ["category"] = "Category",
        ["location"] = "Location",
        ["work_order"] = "Service request",
        ["work_order_part"] = "Part used on a request",
        ["work_order_attachment"] = "Photo on a request",
        ["pm_task"] = "PM task",
        ["pm_schedule"] = "PM schedule",
        ["pm_completion"] = "Completed PM",
        ["pm_task_attachment"] = "File on a PM",
        ["checklist_template"] = "Checklist",
        ["checklist_template_version"] = "Checklist version",
        ["spare_part"] = "Spare part",
        ["training_session"] = "Training session",
        ["training_attendee"] = "Training attendee",
        ["diagnosis"] = "Diagnosis",
    };

    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/audit")
            .WithTags("Audit log")
            .RequirePermission(Permissions.AuditView);

        group.MapGet("/", ListAsync);
        group.MapGet("/tables", TablesAsync);
    }

    private static string Label(string table) =>
        TableLabels.TryGetValue(table, out var label) ? label : Humanise(table);

    /// <summary>"purchase_cost" as "Purchase cost".</summary>
    private static string Humanise(string name)
    {
        var words = name.Replace("_utc", string.Empty, StringComparison.Ordinal).Replace('_', ' ').Trim();
        return words.Length == 0 ? name : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static async Task<IResult> TablesAsync(HospitalPmDbContext db, CancellationToken ct)
    {
        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT DISTINCT table_name AS \"Value\" FROM audit_log WHERE tenant_id = 1")
            .ToListAsync(ct);

        return Results.Ok(tables.Select(t => new { table = t, label = Label(t) }).OrderBy(t => t.label, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The same filter for the page of rows and for the count, so the two cannot disagree. No curly braces in
    /// it: the raw-SQL call reads them as placeholders.
    /// </summary>
    private const string Filter = """
        FROM audit_log
        WHERE tenant_id = 1
          AND changed_at_utc >= @start AND changed_at_utc < @end
          AND (@table IS NULL OR table_name = @table)
          AND (@op IS NULL OR operation = @op)
          AND (@by IS NULL OR (@by = 'none' AND changed_by IS NULL) OR changed_by = @by)
          AND (@record IS NULL OR record_pk = @record)
          AND (@like IS NULL
               OR (COALESCE(old_data, jsonb_build_object()) - @secret)::text ILIKE @like
               OR (COALESCE(new_data, jsonb_build_object()) - @secret)::text ILIKE @like)
        """;

    private static NpgsqlParameter[] Parameters(
        DateTime start, DateTime end, string? table, string? op, string? by, string? record, string? like) =>
    [
        new("start", NpgsqlDbType.TimestampTz) { Value = start },
        new("end", NpgsqlDbType.TimestampTz) { Value = end },
        new("table", NpgsqlDbType.Text) { Value = (object?)table ?? DBNull.Value },
        new("op", NpgsqlDbType.Text) { Value = (object?)op ?? DBNull.Value },
        new("by", NpgsqlDbType.Text) { Value = (object?)by ?? DBNull.Value },
        new("record", NpgsqlDbType.Text) { Value = (object?)record ?? DBNull.Value },
        new("like", NpgsqlDbType.Text) { Value = (object?)like ?? DBNull.Value },
        new("secret", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = Secret },
    ];

    private static async Task<IResult> ListAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? table,
        [FromQuery] string? action,
        [FromQuery] string? by,
        [FromQuery] string? record,
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var today = clock.Today();
        var (scope, error) = await ReportScope.ResolveAsync(db, clock, from ?? today.AddDays(-DefaultDays), to ?? today, null, ct);
        if (error is not null)
        {
            return error;
        }

        // What a person asks for ("Created", "Changed", "Removed") is what the log calls INSERT, UPDATE, DELETE.
        var op = action?.Trim().ToLowerInvariant() switch
        {
            "created" => "INSERT",
            "changed" => "UPDATE",
            "removed" => "DELETE",
            null or "" => null,
            _ => "?",
        };
        if (op == "?")
        {
            return Results.BadRequest(new { error = "Action must be created, changed or removed." });
        }

        var tableName = string.IsNullOrWhiteSpace(table) ? null : table.Trim();
        var who = string.IsNullOrWhiteSpace(by) ? null : by.Trim();
        var recordPk = string.IsNullOrWhiteSpace(record) ? null : record.Trim();
        var like = string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

        NpgsqlParameter[] P() => Parameters(scope!.StartUtc, scope.EndUtc, tableName, op, who, recordPk, like);

        // The sums are over what is asked for, never over the whole log.
        var total = await db.Database
            .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" " + Filter, P().Cast<object>().ToArray())
            .SingleAsync(ct);

        var paging = P().Cast<object>().Append(new NpgsqlParameter("skip", (page - 1) * pageSize)).Append(new NpgsqlParameter("take", pageSize)).ToArray();
        var rows = await db.Database.SqlQueryRaw<AuditRow>(
            "SELECT id AS \"Id\", table_name AS \"TableName\", record_pk AS \"RecordPk\", operation AS \"Operation\", "
            + "old_data::text AS \"OldData\", new_data::text AS \"NewData\", changed_at_utc AS \"ChangedAtUtc\", changed_by AS \"ChangedBy\" "
            + Filter + " ORDER BY id DESC OFFSET @skip LIMIT @take",
            paging).ToListAsync(ct);

        var ids = rows.Select(r => int.TryParse(r.ChangedBy, out var id) ? id : (int?)null)
            .Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        var people = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new { u.FullName, u.UserName }, ct);

        var items = rows.Select(r =>
        {
            var by2 = int.TryParse(r.ChangedBy, out var uid) && people.TryGetValue(uid, out var p)
                ? new { id = uid, name = p.FullName, userName = p.UserName }
                : null;

            var old = Parse(r.OldData);
            var now = Parse(r.NewData);

            return new
            {
                id = r.Id,
                at = DateTime.SpecifyKind(r.ChangedAtUtc, DateTimeKind.Utc),
                // Null for what the system did itself, and for everything written before the log began to record who.
                by = by2,
                byRecorded = r.ChangedBy is not null,
                table = r.TableName,
                tableLabel = Label(r.TableName),
                recordId = r.RecordPk,
                record = Describe(now ?? old),
                action = r.Operation switch { "INSERT" => "Created", "UPDATE" => "Changed", "DELETE" => "Removed", _ => r.Operation },
                changes = Changes(r.Operation, old, now),
            };
        }).ToList();

        return Results.Ok(new
        {
            from = scope!.From,
            to = scope.To,
            items,
            total,
            page,
            pageSize,
        });
    }

    private static Dictionary<string, JsonElement>? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    /// <summary>What the row is called: its tag, number, name, whichever it has.</summary>
    private static string? Describe(Dictionary<string, JsonElement>? row)
    {
        if (row is null)
        {
            return null;
        }

        foreach (var key in Names)
        {
            if (row.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
            {
                return v.GetString();
            }
        }

        return null;
    }

    private static string? Show(JsonElement v)
    {
        var text = v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => v.GetString(),
            JsonValueKind.True => "yes",
            JsonValueKind.False => "no",
            _ => v.GetRawText(),
        };

        return text is { Length: > MaxValueLength } ? text[..MaxValueLength] + "…" : text;
    }

    /// <summary>What changed, field by field, without the secrets and without the changes that happen with everything.</summary>
    private static List<object> Changes(string operation, Dictionary<string, JsonElement>? old, Dictionary<string, JsonElement>? now)
    {
        var changes = new List<object>();
        var keys = (now?.Keys ?? Enumerable.Empty<string>()).Concat(old?.Keys ?? Enumerable.Empty<string>())
            .Distinct().Order(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            if (Noise.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            var was = old is not null && old.TryGetValue(key, out var o) ? Show(o) : null;
            var is_ = now is not null && now.TryGetValue(key, out var n) ? Show(n) : null;

            // An update lists only what differs; a creation or a removal lists what was filled in.
            if (operation == "UPDATE" ? was == is_ : was is null && is_ is null)
            {
                continue;
            }

            changes.Add(new { field = Humanise(key), from = was, to = is_ });
            if (changes.Count >= MaxFieldsPerRow)
            {
                break;
            }
        }

        // The database strips the password and the security stamp out of a staff account's row before it
        // writes it, so a new password leaves nothing to compare. An account that was updated with nothing
        // else visibly different is a change to how it signs in; the fact is shown, never the secret.
        if (operation == "UPDATE" && changes.Count == 0 && now is not null && now.ContainsKey("user_name"))
        {
            changes.Add(new { field = "Sign-in details", from = (string?)null, to = "changed" });
        }

        return changes;
    }
}
