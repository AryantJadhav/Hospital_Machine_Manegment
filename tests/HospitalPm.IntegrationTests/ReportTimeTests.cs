using HospitalPm.Infrastructure.Reports;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Printed times are the hospital's, not UTC.
///
/// A PM signed at 00:27 on the 20th in India is 18:57 on the 19th in UTC. The
/// certificate printed the UTC figure beside the word "local", which put both
/// the hour and the date wrong on the document an auditor is handed.
/// </summary>
public sealed class ReportTimeTests
{
    private static readonly TimeSpan India = TimeSpan.FromMinutes(330);

    [Fact]
    public void A_late_evening_utc_time_is_the_next_morning_and_day_in_india()
    {
        var utc = new DateTime(2026, 9, 19, 18, 57, 0, DateTimeKind.Utc);

        Assert.Equal("20/09/2026 00:27", ReportTime.DateTime(utc, India));
        Assert.Equal("20/09 00:27", ReportTime.DayAndTime(utc, India));
    }

    [Theory]
    [InlineData(330, "IST")]
    [InlineData(0, "UTC")]
    [InlineData(240, "UTC+04:00")]
    [InlineData(-300, "UTC-05:00")]
    public void The_zone_is_always_named(int offsetMinutes, string expected)
    {
        Assert.Equal(expected, ReportTime.Zone(TimeSpan.FromMinutes(offsetMinutes)));
    }

    [Fact]
    public void A_zero_offset_leaves_the_time_alone()
    {
        var utc = new DateTime(2026, 9, 19, 18, 57, 0, DateTimeKind.Utc);

        Assert.Equal("19/09/2026 18:57", ReportTime.DateTime(utc, TimeSpan.Zero));
    }

    [Fact]
    public void The_offset_can_carry_across_a_year_end()
    {
        var utc = new DateTime(2026, 12, 31, 20, 0, 0, DateTimeKind.Utc);

        Assert.Equal("01/01/2027 01:30", ReportTime.DateTime(utc, India));
    }
}
