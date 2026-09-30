using HospitalPm.Infrastructure.Persistence;
using HospitalPm.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Reports;

/// <summary>
/// Reads what machines have cost, for the fleet report and for one machine's own page, so the two
/// are worked out the same way.
/// </summary>
internal static class CostLoader
{
    /// <param name="equipmentId">One machine, or null for every machine in scope.</param>
    /// <param name="pathPrefix">A place and everything under it, or null for the whole hospital.</param>
    /// <param name="periodStartUtc">
    /// With <paramref name="periodEndUtc"/>, only spare parts used in that time count. Both null
    /// counts every use, which is a machine's lifetime.
    /// </param>
    public static async Task<List<CostInput>> LoadAsync(
        HospitalPmDbContext db,
        int? equipmentId,
        string? pathPrefix,
        DateTime? periodStartUtc,
        DateTime? periodEndUtc,
        CancellationToken ct)
    {
        var machineQuery = db.Equipment.AsNoTracking();
        var policyQuery = db.PastInsurancePolicies.AsNoTracking();
        var partsQuery = db.WorkOrderParts.AsNoTracking();

        if (equipmentId is { } one)
        {
            machineQuery = machineQuery.Where(e => e.Id == one);
            policyQuery = policyQuery.Where(p => p.EquipmentId == one);
            partsQuery = partsQuery.Where(p => p.WorkOrder!.EquipmentId == one);
        }
        else if (pathPrefix is { } prefix)
        {
            machineQuery = machineQuery.Where(e => e.Location!.Path.StartsWith(prefix));
            policyQuery = policyQuery.Where(p => p.Equipment!.Location!.Path.StartsWith(prefix));
            partsQuery = partsQuery.Where(p => p.WorkOrder!.Equipment!.Location!.Path.StartsWith(prefix));
        }

        if (periodStartUtc is { } start && periodEndUtc is { } end)
        {
            partsQuery = partsQuery.Where(p => p.UsedAtUtc >= start && p.UsedAtUtc < end);
        }

        var machines = await machineQuery
            .Select(e => new
            {
                e.Id,
                e.AssetTag,
                Type = e.EquipmentType!.Name,
                Place = e.Location!.Name,
                e.PurchaseCost,
                e.IsInsured,
                e.InsuranceProvider,
                e.InsurancePolicyNumber,
                e.InsuranceExpiryDate,
                e.InsuranceCost,
                e.MaintenanceCost,
            })
            .ToListAsync(ct);

        var past = await policyQuery
            .OrderByDescending(p => p.ExpiryDate)
            .Select(p => new { p.EquipmentId, p.Provider, p.PolicyNumber, p.ExpiryDate, p.Cost })
            .ToListAsync(ct);

        var uses = await partsQuery
            .Select(p => new
            {
                p.WorkOrder!.EquipmentId,
                p.SparePartId,
                PartNumber = p.SparePart!.PartNumber,
                Name = p.SparePart!.Name,
                p.QuantityUsed,
                p.UnitCostAtUse,
            })
            .ToListAsync(ct);

        var pastByMachine = past.ToLookup(p => p.EquipmentId);
        var usesByMachine = uses.ToLookup(u => u.EquipmentId);

        return machines.Select(m =>
        {
            // The policy in force first, then the ones it replaced, newest first.
            var policies = new List<CostPolicyLine>();
            if (m.IsInsured && m.InsuranceExpiryDate is { } expiry)
            {
                policies.Add(new CostPolicyLine(
                    m.InsuranceProvider ?? "Unknown", m.InsurancePolicyNumber, expiry, m.InsuranceCost, IsCurrent: true));
            }

            policies.AddRange(pastByMachine[m.Id].Select(p =>
                new CostPolicyLine(p.Provider, p.PolicyNumber, p.ExpiryDate, p.Cost, IsCurrent: false)));

            // A part used with no cost on record adds nothing, and is flagged so the figure is
            // not taken for the whole truth.
            var used = usesByMachine[m.Id].ToList();
            var parts = used
                .GroupBy(u => u.SparePartId)
                .Select(g => new CostPartLine(
                    g.First().PartNumber,
                    g.First().Name,
                    g.Sum(u => u.QuantityUsed),
                    g.Sum(u => (u.UnitCostAtUse ?? 0m) * u.QuantityUsed),
                    g.Any(u => u.UnitCostAtUse is null)))
                .OrderByDescending(l => l.Cost)
                .ThenBy(l => l.PartNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new CostInput(
                m.Id,
                m.AssetTag,
                m.Type,
                m.Place,
                m.PurchaseCost,
                m.MaintenanceCost,
                policies,
                parts,
                used.Count(u => u.UnitCostAtUse is null));
        }).ToList();
    }
}
