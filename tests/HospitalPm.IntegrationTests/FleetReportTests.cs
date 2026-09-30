using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Reports;

namespace HospitalPm.IntegrationTests;

/// <summary>The downtime and cost reports' arithmetic, with no database involved.</summary>
public sealed class FleetReportTests
{
    // A 10-day period, so a window's edges are easy to place on it.
    private static readonly DateTime Start = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly From = new(2026, 9, 1);
    private static readonly DateOnly To = new(2026, 9, 10);
    private static readonly DateTime LongAgo = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime Day(int d, int hour = 0) => new(2026, 9, d, hour, 0, 0, DateTimeKind.Utc);

    private static DowntimeInput Machine(
        string tag, DateTime? registered = null, params DowntimeWindow[] windows)
        => new(tag.GetHashCode(StringComparison.Ordinal), tag, "Ventilator", "ICU", registered ?? LongAgo, windows);

    private static DowntimeReportData Build(params DowntimeInput[] inputs)
        => DowntimeReport.Build(inputs, From, To, Start, End);

    [Fact]
    public void A_window_inside_the_period_counts_its_hours_and_one_incident()
    {
        var report = Build(Machine("M1", null, new DowntimeWindow(Day(2, 6), Day(2, 18))));

        var m = Assert.Single(report.Machines);
        Assert.Equal(12, m.DowntimeHours);
        Assert.Equal(1, m.Incidents);
        Assert.False(m.StillDown);
    }

    [Fact]
    public void A_window_that_began_before_the_period_counts_only_the_part_inside_it()
    {
        // Down from 30 Aug 00:00 to 2 Sep 00:00: only the first day of September is this period's.
        var report = Build(Machine("M1", null, new DowntimeWindow(Day(1).AddDays(-2), Day(2))));

        Assert.Equal(24, Assert.Single(report.Machines).DowntimeHours);
    }

    [Fact]
    public void A_window_that_runs_past_the_period_counts_only_the_part_inside_it()
    {
        // Down from 10 Sep 12:00 to 15 Sep: the period ends at the start of 11 Sep, so 12 hours.
        var report = Build(Machine("M1", null, new DowntimeWindow(Day(10, 12), Day(15))));

        Assert.Equal(12, Assert.Single(report.Machines).DowntimeHours);
    }

    [Fact]
    public void A_machine_still_down_is_counted_up_to_the_end_of_counting_and_flagged()
    {
        var report = Build(Machine("M1", null, new DowntimeWindow(Day(9), null)));

        var m = Assert.Single(report.Machines);
        Assert.Equal(48, m.DowntimeHours);
        Assert.True(m.StillDown);
        Assert.Equal(1, report.StillDown);
    }

    [Fact]
    public void Two_reports_open_at_once_count_the_shared_hours_once_but_are_two_incidents()
    {
        var report = Build(Machine(
            "M1",
            null,
            new DowntimeWindow(Day(2, 0), Day(2, 12)),
            new DowntimeWindow(Day(2, 6), Day(2, 18))));

        var m = Assert.Single(report.Machines);
        Assert.Equal(18, m.DowntimeHours);
        Assert.Equal(2, m.Incidents);
    }

    [Fact]
    public void A_window_wholly_outside_the_period_is_left_out()
    {
        var report = Build(
            Machine("BEFORE", null, new DowntimeWindow(Day(1).AddDays(-5), Day(1).AddDays(-3))),
            Machine("AFTER", null, new DowntimeWindow(Day(12), Day(13))));

        Assert.Empty(report.Machines);
    }

    [Fact]
    public void A_machine_added_part_way_through_is_measured_from_when_it_was_added()
    {
        // On the register from 6 Sep, so 5 days tracked; down for the whole of 6 Sep (24 h of 120).
        var report = Build(Machine("M1", Day(6), new DowntimeWindow(Day(5), Day(7))));

        var m = Assert.Single(report.Machines);
        Assert.Equal(24, m.DowntimeHours);
        Assert.Equal(80.0, m.AvailabilityPercent);
    }

    [Fact]
    public void Availability_is_the_share_of_the_period_the_machine_was_up()
    {
        // 24 hours down out of 240.
        var report = Build(Machine("M1", null, new DowntimeWindow(Day(3), Day(4))));

        Assert.Equal(90.0, Assert.Single(report.Machines).AvailabilityPercent);
    }

    [Fact]
    public void The_machine_that_was_down_longest_comes_first()
    {
        var report = Build(
            Machine("SHORT", null, new DowntimeWindow(Day(2), Day(2, 6))),
            Machine("LONG", null, new DowntimeWindow(Day(3), Day(5))),
            Machine("MEDIUM", null, new DowntimeWindow(Day(6), Day(7))));

        Assert.Equal(["LONG", "MEDIUM", "SHORT"], report.Machines.Select(m => m.AssetTag).ToArray());
        Assert.Equal(3, report.Machines.Count);
    }

    [Fact]
    public void The_downtime_csv_has_a_row_per_machine_and_reads_in_excel()
    {
        var report = Build(Machine("=CMD", null, new DowntimeWindow(Day(2), Day(3))));

        var csv = DowntimeReport.ToCsv(report);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("﻿Asset tag,Equipment type,Location,Incidents,Downtime (hours)", lines[0], StringComparison.Ordinal);
        // A tag a person typed must not be read as a formula.
        Assert.StartsWith("'=CMD,Ventilator,ICU,1,24,", lines[1], StringComparison.Ordinal);
    }

    private static CostInput Cost(
        string tag, decimal? purchase = null, decimal? insurance = null, decimal? contract = null,
        decimal parts = 0m, int partsWithoutCost = 0)
        => new(
            tag.GetHashCode(StringComparison.Ordinal),
            tag,
            "Ventilator",
            "ICU",
            purchase,
            contract,
            insurance is null ? [] : [Policy(insurance, current: true)],
            parts > 0 ? [new CostPartLine("FUSE-1", "Fuse", 1, parts, false)] : [],
            partsWithoutCost);

    private static CostPolicyLine Policy(decimal? cost, bool current, int year = 2027)
        => new("Star Health", null, new DateOnly(year, 1, 1), cost, current);

    [Fact]
    public void A_machines_total_is_its_purchase_insurance_contract_and_parts()
    {
        var report = CostReport.Build([Cost("M1", 100_000m, 5_000m, 12_000m, 3_000m)], From, To);

        var m = Assert.Single(report.Machines);
        Assert.Equal(120_000m, m.Total);
        Assert.Equal(120_000m, report.GrandTotal);
        Assert.Equal(100_000m, report.PurchaseTotal);
        Assert.Equal(5_000m, report.InsuranceTotal);
        Assert.Equal(12_000m, report.ContractTotal);
        Assert.Equal(3_000m, report.PartsTotal);
    }

    [Fact]
    public void A_cost_nobody_recorded_is_left_blank_not_counted_as_a_zero_that_hides_it()
    {
        var report = CostReport.Build([Cost("M1", purchase: 50_000m)], From, To);

        var m = Assert.Single(report.Machines);
        Assert.Null(m.InsuranceCost);
        Assert.Null(m.ContractCost);
        Assert.Equal(50_000m, m.Total);
    }

    [Fact]
    public void Machines_with_no_cost_at_all_are_counted_not_listed()
    {
        var report = CostReport.Build([Cost("PAID", purchase: 10_000m), Cost("BLANK1"), Cost("BLANK2")], From, To);

        Assert.Single(report.Machines);
        Assert.Equal(2, report.MachinesWithoutCost);
    }

    [Fact]
    public void The_dearest_machine_comes_first()
    {
        var report = CostReport.Build(
            [Cost("CHEAP", purchase: 1_000m), Cost("DEAR", purchase: 90_000m), Cost("MID", purchase: 20_000m)], From, To);

        Assert.Equal(["DEAR", "MID", "CHEAP"], report.Machines.Select(m => m.AssetTag).ToArray());
    }

    [Fact]
    public void Parts_used_without_a_recorded_cost_are_counted_so_the_gap_is_visible()
    {
        var report = CostReport.Build(
            [Cost("M1", purchase: 1_000m, parts: 200m, partsWithoutCost: 2), Cost("M2", partsWithoutCost: 1)], From, To);

        Assert.Equal(3, report.PartsWithoutCost);
    }

    [Fact]
    public void The_cost_csv_leaves_an_unrecorded_cost_empty_and_names_the_parts_used()
    {
        var report = CostReport.Build([Cost("M1", purchase: 250_000m, parts: 1_500.5m)], From, To);

        var lines = CostReport.ToCsv(report).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith(
            "﻿Asset tag,Equipment type,Location,Purchase cost,Insurance cost,Insurance policies,Maintenance contract cost,Spare parts cost,Spare parts used,Total",
            lines[0],
            StringComparison.Ordinal);
        Assert.Equal("M1,Ventilator,ICU,250000,,,,1500.5,FUSE-1 x1,251500.5", lines[1]);
    }

    private static CostInput WithPolicies(string tag, params CostPolicyLine[] policies)
        => new(tag.GetHashCode(StringComparison.Ordinal), tag, "Ventilator", "ICU", null, null, policies, [], 0);

    [Fact]
    public void A_renewed_policy_adds_to_the_insurance_instead_of_replacing_the_old_one()
    {
        // Renewed once: this year's policy and last year's, both paid for.
        var report = CostReport.Build(
            [WithPolicies("M1", Policy(6_000m, current: true, year: 2027), Policy(5_000m, current: false, year: 2026))],
            From,
            To);

        var m = Assert.Single(report.Machines);
        Assert.Equal(11_000m, m.InsuranceCost);
        Assert.Equal(11_000m, m.Total);
        Assert.Equal(2, m.Policies.Count);
        Assert.Equal(11_000m, report.InsuranceTotal);
    }

    [Fact]
    public void A_policy_with_no_recorded_cost_is_not_shown_as_a_zero()
    {
        var unpriced = CostReport.Machine(WithPolicies("M1", Policy(null, current: true)));
        Assert.Null(unpriced.InsuranceCost);

        // One priced and one not: the known cost counts, the unknown one adds nothing.
        var mixed = CostReport.Machine(
            WithPolicies("M2", Policy(4_000m, current: true, year: 2027), Policy(null, current: false, year: 2026)));
        Assert.Equal(4_000m, mixed.InsuranceCost);
    }

    [Fact]
    public void A_machine_with_no_cost_can_still_be_read_on_its_own()
    {
        // The machine's own page shows it even when there is nothing to add up.
        var m = CostReport.Machine(Cost("BLANK"));

        Assert.Equal(0m, m.Total);
        Assert.Empty(m.Parts);
    }

    [Fact]
    public void The_parts_used_are_listed_and_add_up_to_the_parts_cost()
    {
        var input = new CostInput(
            1, "M1", "Ventilator", "ICU", null, null, [],
            [
                new CostPartLine("FLOW-SENS-04", "Flow sensor", 1, 4_200m, false),
                new CostPartLine("O-RING-12", "O-ring seal", 3, 45m, false),
            ],
            0);

        var m = CostReport.Machine(input);

        Assert.Equal(4_245m, m.PartsCost);
        Assert.Equal(2, m.Parts.Count);
        Assert.Equal(4_245m, m.Total);
    }
}
