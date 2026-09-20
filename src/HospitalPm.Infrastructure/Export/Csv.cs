namespace HospitalPm.Infrastructure.Export;

/// <summary>
/// The one place a value becomes a CSV cell, shared by every report and by the
/// data export.
/// </summary>
public static class Csv
{
    /// <summary>
    /// A spreadsheet treats a cell starting with = + - or @ as a formula, and an
    /// asset tag or a skip reason is typed by a person. Prefixing a quote keeps
    /// the text as text.
    /// </summary>
    public static string Cell(string? value)
    {
        value ??= string.Empty;

        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    public static string Line(IEnumerable<string?> cells) => string.Join(',', cells.Select(Cell));
}
