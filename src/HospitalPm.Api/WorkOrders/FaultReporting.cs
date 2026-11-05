using HospitalPm.Domain.Assets;
using HospitalPm.Domain.GatePasses;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.WorkOrders;

/// <summary>
/// When a fault can be reported on a machine, and who is told it cannot.
///
/// A machine that has left the hospital on a gate pass, been recorded as lost, or been retired cannot be repaired by the
/// department that uses it. The department's own people do not see it offered when they report a fault, and the server
/// refuses it if they ask anyway, with the reason in words. The biomedical team can still raise a request on a machine that
/// is out on a gate pass (it is theirs to chase), but not on one that is lost or retired, for which there is nothing to repair.
/// </summary>
public static class FaultReporting
{
    /// <summary>
    /// Why a fault cannot be reported on this machine by this kind of person, or null when it can.
    /// </summary>
    /// <param name="status">The machine's status on the register.</param>
    /// <param name="assetTag">The machine's number, for the sentence.</param>
    /// <param name="openPass">The reference of the gate pass the machine is out on right now (GP-1001), or null.</param>
    /// <param name="byDepartment">True for a person from another department: someone who does not work on the equipment.</param>
    public static string? WhyNot(EquipmentStatus status, string assetTag, string? openPass, bool byDepartment) => status switch
    {
        EquipmentStatus.Condemned => "This machine has been condemned, so a fault cannot be reported on it.",
        EquipmentStatus.Disposed => $"{assetTag} has been disposed of, so a fault cannot be reported on it.",
        EquipmentStatus.Lost => $"{assetTag} is recorded as lost, so a fault cannot be reported on it. Ask the biomedical team.",
        _ when byDepartment && openPass is not null =>
            $"{assetTag} is out of the hospital for repair on gate pass {openPass}, so a fault cannot be reported on it until it is back. Ask the biomedical team.",
        _ => null,
    };

    /// <summary>The reference of the gate pass this machine is out on right now, or null when it is not out.</summary>
    public static async Task<string?> OpenPassAsync(HospitalPmDbContext db, int equipmentId, CancellationToken ct)
    {
        var number = await db.GatePassItems.AsNoTracking()
            .Where(i => i.EquipmentId == equipmentId && i.GatePass!.Status == GatePassStatus.Out)
            .Select(i => (int?)i.GatePass!.Number)
            .FirstOrDefaultAsync(ct);

        return number is { } n ? GatePass.ReferenceFor(n) : null;
    }
}
