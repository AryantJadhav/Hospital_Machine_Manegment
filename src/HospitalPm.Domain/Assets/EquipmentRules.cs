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

    /// <summary>
    /// How old a machine is, in whole months, and which date that was counted from: the day it was installed, or when
    /// that is not recorded, the day it was bought. Null when neither is known, and when the date is still ahead (a
    /// machine bought for a ward that is not open yet is not old, and is not "-3 months" either).
    ///
    /// Whole months, not years with a fraction, so 11 months reads as 11 months and a screen can say "2 years 3 months".
    /// The month is complete on the same day number: from 15 March, 15 April is one month, 14 April is not yet.
    /// </summary>
    public static (int Months, string From)? AgeOf(DateOnly? installation, DateOnly? purchase, DateOnly today)
    {
        var (start, from) = installation is { } i ? (i, AgeFromInstallation) : purchase is { } p ? (p, AgeFromPurchase) : (default, string.Empty);
        if (from.Length == 0 || start > today)
        {
            return null;
        }

        var months = ((today.Year - start.Year) * 12) + today.Month - start.Month;
        if (today.Day < start.Day)
        {
            months--;
        }

        return (Math.Max(0, months), from);
    }

    /// <summary>The longest expected life a machine can be given, in years. Longer is a typing mistake, not a plan.</summary>
    public const int MaxExpectedLifeYears = 60;

    /// <summary>Why an expected life cannot be used, or null when it can. Blank is allowed: it is simply not set.</summary>
    public static string? LifeProblem(int? years) =>
        years is { } y && (y < 1 || y > MaxExpectedLifeYears)
            ? $"The expected life must be between 1 and {MaxExpectedLifeYears} years."
            : null;

    /// <summary>
    /// Months of expected life left: positive while the machine has time, zero in the month it reaches it, negative once past
    /// it (-14 is fourteen months past). Null when either the expected life or the age is not known.
    /// </summary>
    public static int? LifeRemainingMonths(int? expectedLifeYears, int? ageMonths) =>
        expectedLifeYears is { } years && ageMonths is { } age ? (years * 12) - age : null;

    /// <summary>The two things an age can be counted from, as the API says them.</summary>
    public const string AgeFromInstallation = "installation";

    public const string AgeFromPurchase = "purchase";
}
