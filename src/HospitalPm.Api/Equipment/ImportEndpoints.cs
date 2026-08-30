using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Import;

namespace HospitalPm.Api.Equipment;

public static class ImportEndpoints
{
    /// <summary>
    /// Bounded upload. 20,000 rows of asset data is a few megabytes; a much
    /// larger file is a mis-selected one, and accepting it would let a single
    /// request exhaust memory on a hospital PC.
    /// </summary>
    private const long MaxUploadBytes = 25 * 1024 * 1024;

    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static void MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/equipment/import")
            .WithTags("Equipment import")
            // Bulk-loading the register is a register-owner action, not a
            // technician's.
            .RequireAuthorization(p => p.RequireRole(Roles.Admin, Roles.BiomedicalHead));

        group.MapGet("/template", GetTemplate);
        group.MapPost("/validate", ValidateAsync).DisableAntiforgery();
        group.MapPost("/commit", CommitAsync).DisableAntiforgery();
    }

    private static IResult GetTemplate()
        => Results.File(
            EquipmentImportService.BuildTemplate(),
            XlsxContentType,
            "hospitalpm-equipment-template.xlsx");

    private static Task<IResult> ValidateAsync(
        IFormFile file, EquipmentImportService importer, CancellationToken ct)
        => RunAsync(file, importer, commit: false, ct);

    private static Task<IResult> CommitAsync(
        IFormFile file, EquipmentImportService importer, CancellationToken ct)
        => RunAsync(file, importer, commit: true, ct);

    private static async Task<IResult> RunAsync(
        IFormFile file, EquipmentImportService importer, bool commit, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { error = "No file was uploaded." });
        }

        if (file.Length > MaxUploadBytes)
        {
            return Results.BadRequest(new
            {
                error = $"File is larger than {MaxUploadBytes / (1024 * 1024)} MB.",
            });
        }

        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            // .xls is the old binary format and needs a different reader.
            // Saying so is more useful than a parser stack trace, since a
            // hospital's existing register is often still in it.
            return Results.BadRequest(new
            {
                error = "Only .xlsx files are supported. Open an .xls file in Excel and save it as .xlsx.",
            });
        }

        // Buffered to a MemoryStream because the reader seeks, and the
        // request body stream does not support seeking.
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        ImportResult result;
        try
        {
            result = commit
                ? await importer.CommitAsync(buffer, ct)
                : await importer.ValidateAsync(buffer, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            // A corrupt or non-Excel file renamed to .xlsx lands here. The
            // user needs to know their file is unreadable, not see a 500.
            return Results.BadRequest(new
            {
                error = "The file could not be read as an Excel workbook. It may be corrupt or renamed from another format.",
            });
        }

        return Results.Ok(new
        {
            totalRows = result.TotalRows,
            validRows = result.ValidRows,
            committed = result.Committed,
            errorCount = result.Errors.Count,
            // Capped: a spreadsheet where every row is wrong should not
            // return a 20,000-entry payload the browser then has to render.
            errors = result.Errors.Take(200).Select(e => new
            {
                row = e.RowNumber,
                column = e.Column,
                message = e.Message,
            }),
            errorsTruncated = result.Errors.Count > 200,
        });
    }
}
