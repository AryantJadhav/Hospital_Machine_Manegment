namespace HospitalPm.Domain.Inventory;

public enum StockLevel
{
    /// <summary>More than <see cref="StockRule.LowStockMax"/> on the shelf.</summary>
    Ok = 0,

    /// <summary>Between one and <see cref="StockRule.LowStockMax"/>: still there, but time to buy more.</summary>
    Low = 1,

    /// <summary>None left.</summary>
    Out = 2,
}

/// <summary>
/// When a spare part counts as running short.
///
/// One rule for every part, because it is the one a store keeper actually uses: none left is out of
/// stock, and one to five is low. It used to be a number set on each part, which nobody kept up, so
/// the same filter meant a different thing on every row. Here once, so the screen, the filter and the
/// report cannot disagree.
/// </summary>
public static class StockRule
{
    /// <summary>The most pieces that still count as low.</summary>
    public const int LowStockMax = 5;

    public static StockLevel For(int quantityOnHand) => quantityOnHand switch
    {
        <= 0 => StockLevel.Out,
        <= LowStockMax => StockLevel.Low,
        _ => StockLevel.Ok,
    };
}
