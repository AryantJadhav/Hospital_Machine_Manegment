namespace HospitalPm.Domain.Assets;

/// <summary>
/// The sense checks on a machine's record that are the same wherever it is entered: the form, an edit and the
/// Excel import. Kept in one place so the three cannot disagree about what a mistake is.
/// </summary>
public static class EquipmentRules
{
    /// <summary>
    /// Why an asset tag cannot be used, or null when it can. A "/" cannot be carried in the address
    /// that looks a machine up by its tag, nor in the link a printed QR label opens, so a machine with one
    /// could be on the register and unreachable from its own label.
    /// </summary>
    public static string? TagProblem(string? tag) =>
        tag is not null && tag.Contains('/', StringComparison.Ordinal)
            ? "An asset tag cannot contain \"/\". Use \"-\" instead, for example ICU-12."
            : null;

    /// <summary>
    /// The first obvious mistake in a machine's dates, or null. The field name is the one the
    /// Excel template and the form use, so the import can point at the column.
    ///
    /// Only the mistakes that cannot be true: bought in the future, installed before it was bought, a warranty
    /// that ran out before the purchase. A blank date is never a mistake, because a hospital with old records
    /// rarely has them all.
    /// </summary>
    public static (string Field, string Message)? DateProblem(
        DateOnly? purchase, DateOnly? installation, DateOnly? warrantyExpiry, DateOnly today)
    {
        if (purchase is { } bought && bought > today)
        {
            return ("Purchase Date", "The purchase date is in the future. A machine cannot have been bought yet.");
        }

        if (purchase is { } p && installation is { } installed && installed < p)
        {
            return ("Installation Date", "The machine cannot have been installed before it was bought.");
        }

        if (purchase is { } p2 && warrantyExpiry is { } ends && ends < p2)
        {
            return ("Warranty Expiry", "The warranty cannot have ended before the machine was bought.");
        }

        return null;
    }
}
