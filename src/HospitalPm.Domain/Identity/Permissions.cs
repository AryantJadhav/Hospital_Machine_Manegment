namespace HospitalPm.Domain.Identity;

/// <summary>
/// What a person may do, named by the thing done rather than by who usually does it.
///
/// Every endpoint that is not open to every signed-in person asks for one of these, never for a
/// role. Which role holds which is decided in one place, <see cref="RolePermissions"/>, so that
/// giving a new kind of user access to one section is a change to a table and not a hunt through
/// fifty endpoints for each place that says "Admin".
///
/// Reading is here too, because not everyone signed in may read everything: the hospital's IT team
/// keeps the installation running and has no business in the equipment register, and a person in
/// another department sees only what is theirs.
///
/// Constants rather than an enum for the reason <see cref="Roles"/> gives: they are compared as
/// strings, and a typo must not compile.
/// </summary>
public static class Permissions
{
    // Reading and doing the everyday work.
    public const string RegisterView = "register.view";
    public const string SparePartsView = "spare-parts.view";
    public const string ChecklistsView = "checklists.view";
    public const string TrainingView = "training.view";
    public const string PmWork = "pm.work";
    public const string WorkOrdersView = "work-orders.view";

    /// <summary>
    /// Add a note or a photo to a service request. Held by everyone who may report a fault, so the
    /// person who raised one can answer what the engineer asks. Resolving it, changing its status
    /// and drawing parts for it are <see cref="WorkOrdersWork"/>, which a department user does not hold.
    /// </summary>
    public const string WorkOrdersNote = "work-orders.note";

    /// <summary>
    /// See the equipment of one's own departments, and nothing outside them. Not a section that can
    /// be given: it is what makes a person a department user, and it is held with no
    /// <see cref="RegisterView"/>, which is how the system knows to limit what they see.
    /// </summary>
    public const string DepartmentView = "department.view";
    public const string WorkOrdersReport = "work-orders.report";
    public const string WorkOrdersWork = "work-orders.work";
    public const string EquipmentMove = "equipment.move";

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

    /// <summary>
    /// Giving one person access to a section, or taking it away. Held by the Developer alone. It is
    /// not something that can itself be granted, or the hospital could hand out the right to hand out.
    /// </summary>
    public const string AccessManage = "access.manage";
    public const string SystemBackups = "system.backups";
    public const string SystemRestore = "system.restore";
    public const string SystemUpdates = "system.updates";
    public const string SystemDiagnostics = "system.diagnostics";
    public const string SystemLicence = "system.licence";

    public static readonly IReadOnlyList<string> All =
    [
        RegisterView, SparePartsView, ChecklistsView, TrainingView, PmWork,
        WorkOrdersView, WorkOrdersReport, WorkOrdersNote, WorkOrdersWork, EquipmentMove, DepartmentView,
        EquipmentEdit, EquipmentTypesEdit, LocationsEdit, LabelsPrint, DataImport, DataExport,
        ChecklistsEdit, PmManage, WorkOrdersAssign, WorkOrdersCancel, AttachmentsDelete,
        SparePartsEdit, TrainingEdit, ReportsView,
        StaffManage, AccessManage, SystemBackups, SystemRestore, SystemUpdates, SystemDiagnostics, SystemLicence,
    ];
}
