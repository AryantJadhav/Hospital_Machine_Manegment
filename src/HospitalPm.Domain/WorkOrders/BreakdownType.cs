namespace HospitalPm.Domain.WorkOrders;

/// <summary>
/// What kind of breakdown a service request was. Said by whoever reports it when they know, and by the
/// engineer who looks at the machine once they do; it can be left blank until then.
///
/// It is what lets the department answer "how much of our repair work is software, and how much is
/// machines being used wrongly?", which no list of fault descriptions can.
/// </summary>
public enum BreakdownType
{
    Hardware = 10,

    Software = 20,

    /// <summary>Hardware and software together.</summary>
    Both = 30,

    /// <summary>A cable, a probe, a sensor, a filter, a battery: something that is used up or plugs in.</summary>
    AccessoryOrConsumable = 40,

    /// <summary>The machine is fine; it was used in a way it is not meant to be.</summary>
    ImproperUsage = 50,
}

/// <summary>What each breakdown type is called to a person, so the screen, the API and the printed report say the same thing.</summary>
public static class BreakdownTypeWords
{
    public static string Label(BreakdownType type) => type switch
    {
        BreakdownType.Hardware => "Hardware",
        BreakdownType.Software => "Software",
        BreakdownType.Both => "Both (Hardware & Software)",
        BreakdownType.AccessoryOrConsumable => "Accessory / Consumable",
        _ => "Improper usage",
    };

    public static string? Label(int? type) =>
        type is { } t && Enum.IsDefined((BreakdownType)t) ? Label((BreakdownType)t) : null;
}
