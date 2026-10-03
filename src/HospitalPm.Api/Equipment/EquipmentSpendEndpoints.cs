using HospitalPm.Domain.Identity;
using HospitalPm.Api.Auth;
using HospitalPm.Api.Reports;
using HospitalPm.Infrastructure.Persistence;
using HospitalPm.Infrastructure.Reports;

namespace HospitalPm.Api.Equipment;

/// <summary>
/// What one machine has cost over its whole life: purchase, every insurance policy it has had,
/// its maintenance contract, and the spare parts used on it.
///
/// Worked out by the same code as the Cost report, so this page and that report agree. Open to
/// every role, as the machine's page already shows its purchase, insurance and contract costs.
/// </summary>
public static class EquipmentSpendEndpoints
{
    public static void MapEquipmentSpendEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/equipment/{id:int}/spend", SpendAsync)
            .WithTags("Equipment")
            .RequirePermission(Permissions.RegisterView);
    }

    private static async Task<IResult> SpendAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var inputs = await CostLoader.LoadAsync(db, id, null, null, null, ct);
        if (inputs.Count == 0)
        {
            return Results.NotFound();
        }

        return Results.Ok(new
        {
            spend = CostReport.Machine(inputs[0]),
            partsWithoutCost = inputs[0].PartsWithoutCost,
        });
    }
}
