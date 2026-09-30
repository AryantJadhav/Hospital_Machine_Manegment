using System.Security.Claims;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Equipment;

public sealed record RenewInsuranceRequest(
    string? InsuranceProvider,
    string? InsurancePolicyNumber,
    DateOnly? InsuranceExpiryDate,
    decimal? InsuranceCost);

/// <summary>
/// Renewing a machine's insurance.
///
/// A renewal moves the policy that was in force into the machine's insurance history before the
/// new one takes its place, so what a machine has cost in insurance adds up over the years.
/// Editing the machine still overwrites the policy: that is for correcting a mistake, and a
/// correction is not a second policy to pay for.
/// </summary>
public static class EquipmentInsuranceEndpoints
{
    /// <summary>The most a numeric(14,2) column holds.</summary>
    private const decimal MaxAmount = 999_999_999_999.99m;

    public static void MapEquipmentInsuranceEndpoints(this IEndpointRouteBuilder app)
    {
        // The register is an Administrator's, and so is what its insurance is.
        app.MapPost("/api/equipment/{id:int}/insurance/renew", RenewAsync)
            .WithTags("Equipment")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }

    private static async Task<IResult> RenewAsync(
        int id,
        [FromBody] RenewInsuranceRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        var machine = await db.Equipment.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (machine is null)
        {
            return Results.NotFound();
        }

        if (!machine.IsInsured || machine.InsuranceExpiryDate is not { } oldExpiry)
        {
            return Results.Conflict(new
            {
                error = "This machine has no insurance to renew. Add its insurance by editing the machine.",
            });
        }

        var provider = request.InsuranceProvider?.Trim();
        if (string.IsNullOrEmpty(provider))
        {
            return Results.BadRequest(new { error = "Say who the new policy is with." });
        }

        if (provider.Length > 200)
        {
            return Results.BadRequest(new { error = "The insurer's name can be at most 200 characters." });
        }

        var policyNumber = string.IsNullOrWhiteSpace(request.InsurancePolicyNumber)
            ? null
            : request.InsurancePolicyNumber.Trim();
        if (policyNumber is { Length: > 100 })
        {
            return Results.BadRequest(new { error = "The policy number can be at most 100 characters." });
        }

        if (request.InsuranceExpiryDate is not { } newExpiry)
        {
            return Results.BadRequest(new { error = "Say when the new policy expires." });
        }

        if (newExpiry <= oldExpiry)
        {
            return Results.BadRequest(new
            {
                error = $"The new policy must run past the old one, which covered the machine until {oldExpiry:dd/MM/yyyy}.",
            });
        }

        if (request.InsuranceCost is < 0 or > MaxAmount)
        {
            return Results.BadRequest(new { error = "The cost of the insurance is not a sensible amount." });
        }

        db.PastInsurancePolicies.Add(new PastInsurancePolicy
        {
            TenantId = machine.TenantId,
            EquipmentId = machine.Id,
            Provider = machine.InsuranceProvider ?? "Unknown",
            PolicyNumber = machine.InsurancePolicyNumber,
            ExpiryDate = oldExpiry,
            Cost = machine.InsuranceCost,
            RenewedAtUtc = clock.GetUtcNow().UtcDateTime,
            RenewedByUserId = EquipmentMoveEndpoints.UserId(principal),
        });

        machine.InsuranceProvider = provider;
        machine.InsurancePolicyNumber = policyNumber;
        machine.InsuranceExpiryDate = newExpiry;
        machine.InsuranceCost = request.InsuranceCost is { } cost
            ? Math.Round(cost, 2, MidpointRounding.AwayFromZero)
            : null;

        // One save, so the old policy is never both kept and still current, or neither.
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }
}
