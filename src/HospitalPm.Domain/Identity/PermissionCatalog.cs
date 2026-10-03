namespace HospitalPm.Domain.Identity;

/// <summary>One thing a person may be given, as a person reads it.</summary>
public sealed record PermissionInfo(string Permission, string Group, string Label, string Help);

/// <summary>
/// The sections that can be given to one person or taken from them, with names a person would use.
///
/// Not everything in <see cref="Permissions"/> can be given. Looking after staff is decided by role
/// (who may make whom is a rule about the roles themselves), and giving access is the Developer's
/// alone, so neither is here.
/// </summary>
public static class PermissionCatalog
{
    public static readonly IReadOnlyList<PermissionInfo> All =
    [
        new(Permissions.RegisterView, "Looking", "See the equipment register", "Equipment, locations, labels, history and the Today page."),
        new(Permissions.SparePartsView, "Looking", "See spare parts", "The shelf, its stock and its purchases."),
        new(Permissions.ChecklistsView, "Looking", "See checklists", "What a completed PM is checked against."),
        new(Permissions.TrainingView, "Looking", "See training", "Sessions held and who attended."),
        new(Permissions.WorkOrdersView, "Looking", "See service requests", "The list, each request, and its service report."),
        new(Permissions.PmWork, "Looking", "See and do PM", "The PM list, recording a PM, and its certificate."),

        new(Permissions.WorkOrdersReport, "Everyday work", "Report a fault", "Open a new service request."),
        new(Permissions.WorkOrdersNote, "Everyday work", "Add a note or photo to a request", "Answer what the engineer asks, and show what is wrong."),
        new(Permissions.WorkOrdersWork, "Everyday work", "Work a service request", "Notes, status, parts used, photos, and marking it resolved."),
        new(Permissions.EquipmentMove, "Everyday work", "Move a machine", "Record that a machine is now somewhere else."),
        new(Permissions.GatePassView, "Looking", "See gate passes", "Which machines are out of the hospital for repair, and with whom."),
        new(Permissions.GatePassEdit, "Everyday work", "Send a machine out", "Write a gate pass, record that it came back, or cancel one."),

        new(Permissions.EquipmentEdit, "The register", "Add and change equipment", "Add, edit, condemn, and renew insurance."),
        new(Permissions.EquipmentTypesEdit, "The register", "Change kinds of machine", "Add and edit equipment types."),
        new(Permissions.LocationsEdit, "The register", "Change locations", "Add and edit buildings, departments and rooms."),
        new(Permissions.LabelsPrint, "The register", "Print labels", "Bulk QR label sheets and label printer files."),
        new(Permissions.DataImport, "The register", "Import from Excel", "Bring equipment and locations in from a spreadsheet."),
        new(Permissions.DataExport, "The register", "Export all data", "Take a copy of the hospital's data out."),

        new(Permissions.ChecklistsEdit, "The department", "Write checklists", "Create and publish PM checklists."),
        new(Permissions.PmManage, "The department", "Schedule PM, or skip one", "Create schedules, and record that a PM will not happen."),
        new(Permissions.WorkOrdersAssign, "The department", "Assign work", "Decide who does a service request."),
        new(Permissions.WorkOrdersCancel, "The department", "Cancel a request", "Cancel a service request."),
        new(Permissions.AttachmentsDelete, "The department", "Remove an uploaded file", "A PM report or a photo that should not be kept."),
        new(Permissions.SparePartsEdit, "The department", "Change spare parts", "Add parts and adjust stock."),
        new(Permissions.TrainingEdit, "The department", "Record training", "Add, change and remove training sessions."),
        new(Permissions.ReportsView, "The department", "See reports and costs", "Downtime, cost, work done, stock and compliance reports."),

        new(Permissions.SystemBackups, "The installation", "Backups", "Run a backup and see its history."),
        new(Permissions.SystemRestore, "The installation", "Restore from a backup", "Put a backup back."),
        new(Permissions.SystemUpdates, "The installation", "Updates", "Check for and install an update."),
        new(Permissions.SystemDiagnostics, "The installation", "Diagnostics", "Health of the installation."),
        new(Permissions.SystemLicence, "The installation", "Licence", "See and install the licence."),
        new(Permissions.AuditView, "The installation", "Audit log", "Read who changed what, and when."),
    ];

    private static readonly HashSet<string> Names = All.Select(p => p.Permission).ToHashSet(StringComparer.Ordinal);

    /// <summary>May this be given to one person or taken from them?</summary>
    public static bool IsGrantable(string? permission) => permission is not null && Names.Contains(permission);
}
