using EquipmentAsset = HospitalPm.Domain.Assets.Equipment;
using HospitalPm.Domain.WorkOrders;

namespace HospitalPm.Domain.GatePasses;

/// <summary>Where a returnable gate pass stands.</summary>
public enum GatePassStatus
{
    /// <summary>The goods have left the hospital and have not come back.</summary>
    Out = 10,

    /// <summary>They are back, and the day they came back is recorded.</summary>
    Returned = 20,

    /// <summary>Written out and then not used: the machine never left. Kept, because its number was issued.</summary>
    Cancelled = 30,
}

/// <summary>
/// A returnable gate pass: the paper that lets biomedical equipment leave the hospital for a vendor
/// to repair, and that security checks at the gate on the way out.
///
/// Many machines cannot be repaired where they stand and have to go to the company. Without a record
/// the department cannot say where a machine is, who has it, or when it was due back, and the
/// register still shows it in service. This holds the same facts as the hospital's printed gate pass
/// book - who it went to, why, the items, the date it should come back and the day it did - and
/// prints the same form.
///
/// NO PATIENT DATA. A pass names a company, a purpose and a list of equipment. It never names the
/// patient the machine was used on. See CLAUDE.md.
/// </summary>
public sealed class GatePass
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    /// <summary>
    /// The number on the paper, 1001, 1002, ... Handed out by a database sequence, so two engineers
    /// writing a pass at the same moment cannot be given the same one. Never reused: a pass that is
    /// cancelled keeps its number, as a spoiled page in a printed book would.
    /// </summary>
    public int Number { get; set; }

    /// <summary>The day it was written, which is the day the goods leave.</summary>
    public DateOnly PassDate { get; set; }

    /// <summary>The company the goods are going to ("Name of Company / Person" on the form).</summary>
    public required string VendorName { get; set; }

    /// <summary>Who at the company, if it is worth saying.</summary>
    public string? ContactPerson { get; set; }

    public string? ContactPhone { get; set; }

    /// <summary>"Sending to the company for repair."</summary>
    public required string Purpose { get; set; }

    /// <summary>
    /// The service request this repair is for, which the printed form calls the Request No. Optional:
    /// a machine can go for calibration or a warranty swap with no fault raised.
    /// </summary>
    public int? WorkOrderId { get; set; }

    public DateOnly? ExpectedReturnDate { get; set; }

    /// <summary>The day the goods actually came back. Set when, and only when, the pass is returned.</summary>
    public DateOnly? ReturnedOn { get; set; }

    public GatePassStatus Status { get; set; } = GatePassStatus.Out;

    /// <summary>Who signed it off, when it is worth recording. The signature itself is on the paper.</summary>
    public string? AuthorisedBy { get; set; }

    public string? Notes { get; set; }

    /// <summary>How it ended: "Repaired, tested OK", "Not repairable, replaced", or why it was cancelled.</summary>
    public string? OutcomeNotes { get; set; }

    public int CreatedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public WorkOrder? WorkOrder { get; set; }

    public ICollection<GatePassItem> Items { get; set; } = [];

    /// <summary>What to quote and what is printed: GP-1001.</summary>
    public string Reference => ReferenceFor(Number);

    public static string ReferenceFor(int number) => $"GP-{number}";
}

/// <summary>
/// One line of a gate pass: a machine or an accessory going out, and how many.
///
/// The words are copied from the machine's record when the line is written and kept as written, so a
/// pass still reads as it did at the gate if the machine is renamed or its model corrected later.
/// A line need not be a registered machine: a cable, a probe or a set of accessories goes out too,
/// which is the "NA" the paper form puts in the asset code column.
/// </summary>
public sealed class GatePassItem
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int GatePassId { get; set; }

    /// <summary>The registered machine this line is, when it is one.</summary>
    public int? EquipmentId { get; set; }

    /// <summary>"Meniscus positioning device". Make, model and serial number belong here.</summary>
    public required string Description { get; set; }

    /// <summary>The machine's number on the register, or whatever the hospital calls it. Blank for something not registered.</summary>
    public string? AssetCode { get; set; }

    public int Quantity { get; set; } = 1;

    public string? Remarks { get; set; }

    public GatePass? GatePass { get; set; }

    public EquipmentAsset? Machine { get; set; }
}
