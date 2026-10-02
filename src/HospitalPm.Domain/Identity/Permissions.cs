namespace HospitalPm.Domain.Identity;

/// <summary>
/// What a person may do, named by the thing done rather than by who usually does it.
///
/// Every endpoint that is not open to every signed-in person asks for one of these, never for a
/// role. Which role holds which is decided in one place, <see cref="RolePermissions"/>, so that
/// giving a new kind of user access to one section is a change to a table and not a hunt through
/// fifty endpoints for each place that says "Admin".
///
/// Reading is mostly not here: anyone signed in can read the register, the work list and the
/// training record. A permission is for changing something, or for seeing what is not for everyone.
///
/// Constants rather than an enum for the reason <see cref="Roles"/> gives: they are compared as
/// strings, and a typo must not compile.
/// </summary>
public static class Permissions
{
    // The register: what the hospital owns and where it is.
    public const string EquipmentEdit = "equipment.edit";
    public const string EquipmentTypesEdit = "equipment-types.edit";
    public const string LocationsEdit = "locations.edit";
    public const string LabelsPrint = "labels.print";
    public const string DataImport = "data.import";
    public const string DataExport = "data.export";

    // The department's commitments.
    public const string ChecklistsEdit = "checklists.edit";
    public const string PmManage = "pm.manage";
    public const string WorkOrdersAssign = "work-orders.assign";
    public const string WorkOrdersCancel = "work-orders.cancel";
    public const string AttachmentsDelete = "attachments.delete";
    public const string SparePartsEdit = "spare-parts.edit";
    public const string TrainingEdit = "training.edit";
    public const string ReportsView = "reports.view";

    // People and the installation itself.
    public const string StaffManage = "staff.manage";
    public const string SystemBackups = "system.backups";
    public const string SystemRestore = "system.restore";
    public const string SystemUpdates = "system.updates";
    public const string SystemDiagnostics = "system.diagnostics";
    public const string SystemLicence = "system.licence";

    public static readonly IReadOnlyList<string> All =
    [
        EquipmentEdit, EquipmentTypesEdit, LocationsEdit, LabelsPrint, DataImport, DataExport,
        ChecklistsEdit, PmManage, WorkOrdersAssign, WorkOrdersCancel, AttachmentsDelete,
        SparePartsEdit, TrainingEdit, ReportsView,
        StaffManage, SystemBackups, SystemRestore, SystemUpdates, SystemDiagnostics, SystemLicence,
    ];
}
