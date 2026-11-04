using HospitalPm.Domain.Losses;
using HospitalPm.Infrastructure.Reports;

namespace HospitalPm.IntegrationTests;

/// <summary>The fleet report of lost equipment: the figures it works out from the loss records. Pure calculation, no database.</summary>
public sealed class LossFleetReportTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);
    private static readonly DateOnly From = new(2026, 4, 1);

    private static LossFleetInput Loss(
        int id,
        LossKind kind = LossKind.Lost,
        LossStatus status = LossStatus.Reported,
        decimal? value = 100m,
        string place = "ICU",
        int equipmentId = 0,
        DateOnly? noticed = null,
        DateOnly? recovered = null,
        string? fir = null) =>
        new(id, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), equipmentId == 0 ? id : equipmentId, $"TAG-{(equipmentId == 0 ? id : equipmentId)}",
            "Pump", place, kind, status, noticed ?? new DateOnly(2026, 10, 1), null, recovered, value, fir);

    private static LossFleetData Build(params LossFleetInput[] losses) => LossFleetReport.Build(losses, From, Today, Today);

    [Fact]
    public void A_loss_is_counted_by_how_it_stands_and_what_it_was_worth_is_split_into_lost_and_recovered()
    {
        var report = Build(
            Loss(1, status: LossStatus.Reported, value: 100m),
            Loss(2, status: LossStatus.Investigating, value: 200m),
            Loss(3, status: LossStatus.Recovered, value: 400m, recovered: new DateOnly(2026, 10, 5)),
            Loss(4, status: LossStatus.WrittenOff, value: 800m));

        Assert.Equal(4, report.Total);
        Assert.Equal(2, report.Open);
        Assert.Equal(1, report.Recovered);
        Assert.Equal(1, report.WrittenOff);
        Assert.Equal(1500m, report.ValueReported);
        Assert.Equal(1100m, report.ValueLost);        // everything but what came back
        Assert.Equal(400m, report.ValueRecovered);
        Assert.Equal(0, report.WithoutValue);
    }

    [Fact]
    public void A_loss_waiting_for_the_heads_decision_is_still_open_and_still_counts_as_missing()
    {
        var report = Build(
            Loss(1, status: LossStatus.Reported, value: 100m),
            Loss(2, status: LossStatus.AwaitingDecision, value: 200m, noticed: new DateOnly(2026, 10, 24)));

        Assert.Equal(2, report.Open);
        Assert.Equal(300m, report.ValueLost);
        Assert.Equal(10, report.Items.Single(i => i.Id == 2).DaysMissing);
        Assert.Equal("Waiting for the head's decision", report.Items.Single(i => i.Id == 2).StatusLabel);
    }

    [Fact]
    public void A_machine_with_no_value_adds_nothing_and_is_counted_so_the_total_is_not_mistaken_for_the_whole()
    {
        var report = Build(Loss(1, value: 500m), Loss(2, value: null), Loss(3, value: null));

        Assert.Equal(500m, report.ValueReported);
        Assert.Equal(2, report.WithoutValue);
    }

    [Fact]
    public void Every_kind_is_listed_in_order_even_when_none_were_lost_that_way()
    {
        var report = Build(Loss(1, LossKind.Lost), Loss(2, LossKind.Lost), Loss(3, LossKind.LostAway));

        Assert.Equal(["Lost", "Stolen", "Lost while away"], report.ByKind.Select(k => k.Label));
        Assert.Equal([2, 0, 1], report.ByKind.Select(k => k.Count));
    }

    [Fact]
    public void Places_are_ranked_by_how_many_went_from_them_and_a_machine_lost_twice_is_flagged()
    {
        var report = Build(
            Loss(1, place: "ICU", equipmentId: 7),
            Loss(2, place: "ICU", equipmentId: 7),
            Loss(3, place: "ICU"),
            Loss(4, place: "OT"));

        Assert.Equal("ICU", report.ByLocation[0].Label);
        Assert.Equal(3, report.ByLocation[0].Count);
        Assert.Equal("OT", report.ByLocation[1].Label);

        var repeat = Assert.Single(report.RepeatMachines);
        Assert.Equal(7, repeat.EquipmentId);
        Assert.Equal(2, repeat.Losses);
    }

    [Fact]
    public void Days_missing_run_to_today_while_it_is_missing_and_to_the_day_it_was_found_after_that()
    {
        var report = Build(
            Loss(1, status: LossStatus.Reported, noticed: new DateOnly(2026, 10, 24)),                                       // 10 days to the 3rd of November
            Loss(2, status: LossStatus.Recovered, noticed: new DateOnly(2026, 10, 1), recovered: new DateOnly(2026, 10, 4)), // 3 days, however long ago
            Loss(3, status: LossStatus.WrittenOff, noticed: new DateOnly(2026, 9, 1)));                                      // not counted

        Assert.Equal(10, report.Items.Single(i => i.Id == 1).DaysMissing);
        Assert.Equal(3, report.Items.Single(i => i.Id == 2).DaysMissing);
        Assert.Null(report.Items.Single(i => i.Id == 3).DaysMissing);
    }

    [Fact]
    public void The_list_is_latest_first_and_police_complaints_are_counted()
    {
        var report = Build(
            Loss(1, noticed: new DateOnly(2026, 8, 1), fir: "FIR 1"),
            Loss(2, noticed: new DateOnly(2026, 10, 1)),
            Loss(3, noticed: new DateOnly(2026, 9, 1), fir: "  "));

        Assert.Equal([2, 3, 1], report.Items.Select(i => i.Id));
        Assert.Equal(1, report.WithPoliceComplaint);                       // a blank number is no complaint
        Assert.Equal("LOSS-2026-00002", report.Items[0].Reference);
    }

    [Fact]
    public void Nothing_lost_gives_a_report_of_zeros_not_an_error()
    {
        var report = Build();

        Assert.Equal(0, report.Total);
        Assert.Equal(0m, report.ValueReported);
        Assert.Empty(report.Items);
        Assert.Empty(report.ByLocation);
        Assert.Equal(3, report.ByKind.Count);
    }

    [Fact]
    public void The_spreadsheet_has_a_header_and_a_line_per_loss_with_the_value_and_police_number()
    {
        var csv = LossFleetReport.ToCsv(Build(
            Loss(5, LossKind.Stolen, LossStatus.WrittenOff, 12500.5m, "ICU, Bay 4", fir: "FIR 9/2026")));

        var lines = csv.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Reference,Machine number,Machine,Meant to be in,How,Found missing", lines[0], StringComparison.Ordinal);
        Assert.Equal(2, lines.Length);
        Assert.Contains("LOSS-2026-00005", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"ICU, Bay 4\"", lines[1], StringComparison.Ordinal);     // a comma inside a place is quoted
        Assert.Contains("Stolen", lines[1], StringComparison.Ordinal);
        Assert.Contains("Written off", lines[1], StringComparison.Ordinal);
        Assert.Contains("12500.50", lines[1], StringComparison.Ordinal);
        Assert.Contains("FIR 9/2026", lines[1], StringComparison.Ordinal);
    }
}
