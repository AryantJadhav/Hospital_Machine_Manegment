using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Inventory;
using HospitalPm.Infrastructure.Reports;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>The stock rule and the stock report's grouping, with no database.</summary>
public sealed class StockReportBuilderTests
{
    private static StockInput Part(string number, int qty, string name = "Part", string? supplier = null) =>
        new(number.GetHashCode(StringComparison.Ordinal), number, name, qty, "pcs", "Ventilator", supplier, "Rack 1", 120m);

    [Theory]
    [InlineData(-3, StockLevel.Out)]
    [InlineData(0, StockLevel.Out)]
    [InlineData(1, StockLevel.Low)]
    [InlineData(5, StockLevel.Low)]
    [InlineData(6, StockLevel.Ok)]
    [InlineData(500, StockLevel.Ok)]
    public void The_rule_is_none_is_out_one_to_five_is_low_and_more_is_fine(int quantity, StockLevel expected)
    {
        Assert.Equal(expected, StockRule.For(quantity));
    }

    [Fact]
    public void Out_of_stock_and_low_are_separate_lists_and_fine_parts_are_in_neither()
    {
        var report = StockReport.Build(
        [
            Part("A", 0), Part("B", 1), Part("C", 5), Part("D", 6), Part("E", 40),
        ]);

        Assert.Equal(["A"], report.OutOfStock.Select(l => l.PartNumber).ToArray());
        Assert.Equal(["B", "C"], report.LowStock.Select(l => l.PartNumber).ToArray());
    }

    [Fact]
    public void Low_stock_lists_the_fewest_left_first_and_out_of_stock_goes_by_name()
    {
        var report = StockReport.Build(
        [
            Part("P-1", 4, "Filter"),
            Part("P-2", 1, "Sensor"),
            Part("P-3", 2, "Cable"),
            Part("P-4", 0, "Valve"),
            Part("P-5", 0, "Battery"),
        ]);

        Assert.Equal(["P-2", "P-3", "P-1"], report.LowStock.Select(l => l.PartNumber).ToArray());
        Assert.Equal(["P-5", "P-4"], report.OutOfStock.Select(l => l.PartNumber).ToArray());
    }

    [Fact]
    public void The_csv_names_each_part_and_says_whether_it_is_out_or_low()
    {
        var report = StockReport.Build([Part("OUT-1", 0, "Flow sensor", "Acme"), Part("LOW-1", 3, "Fuse")]);

        var lines = StockReport.ToCsv(report).TrimEnd().Split("\r\n");

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("﻿Status,Part number,Name,In stock", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("Out of stock,OUT-1,Flow sensor,0", lines[1], StringComparison.Ordinal);
        Assert.Contains("Acme", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("Low stock,LOW-1,Fuse,3", lines[2], StringComparison.Ordinal);
    }
}

/// <summary>The stock report over HTTP, against the real database.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class StockReportApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "StockReport2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];
        _admin = await SignInAsync("st-admin", Domain.Identity.Roles.BmeHead);
        _employee = await SignInAsync("st-emp", Domain.Identity.Roles.BmeEngineer);
    }

    private async Task<HttpClient> SignInAsync(string prefix, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = $"{prefix}-{_suffix}", FullName = $"{prefix} person", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, role);
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _admin?.Dispose();
        _employee?.Dispose();
        _factory?.Dispose();
    }

    private async Task<string> AddAsync(string key, int qty, bool active = true)
    {
        var number = $"{key}-{_suffix}";
        var created = await _admin.PostAsJsonAsync("/api/spare-parts", new
        {
            partNumber = number, name = $"Part {key}", quantityOnHand = qty, isActive = active,
        });
        created.EnsureSuccessStatusCode();
        return number;
    }

    private static HashSet<string> Numbers(JsonElement list) =>
        list.EnumerateArray().Select(i => i.GetProperty("partNumber").GetString()!).ToHashSet();

    [Fact]
    public async Task The_report_names_the_parts_out_of_stock_and_those_running_low()
    {
        var out1 = await AddAsync("RPT-OUT", 0);
        var low1 = await AddAsync("RPT-LOW1", 1);
        var low5 = await AddAsync("RPT-LOW5", 5);
        var fine = await AddAsync("RPT-FINE", 6);

        var report = await _admin.GetFromJsonAsync<JsonElement>("/api/reports/stock");

        var outList = Numbers(report.GetProperty("outOfStock"));
        var lowList = Numbers(report.GetProperty("lowStock"));

        Assert.Contains(out1, outList);
        Assert.Contains(low1, lowList);
        Assert.Contains(low5, lowList);
        Assert.DoesNotContain(fine, outList);
        Assert.DoesNotContain(fine, lowList);
        Assert.DoesNotContain(out1, lowList);
        Assert.True(report.GetProperty("outOfStockCount").GetInt32() >= 1);
        Assert.True(report.GetProperty("lowStockCount").GetInt32() >= 2);
    }

    [Fact]
    public async Task A_retired_part_is_not_one_to_buy()
    {
        var retired = await AddAsync("RPT-RETIRED", 0, active: false);

        var report = await _admin.GetFromJsonAsync<JsonElement>("/api/reports/stock");

        Assert.DoesNotContain(retired, Numbers(report.GetProperty("outOfStock")));
    }

    [Fact]
    public async Task The_csv_downloads_with_each_part_and_its_status()
    {
        var out1 = await AddAsync("CSV-OUT", 0);
        var low1 = await AddAsync("CSV-LOW", 2);

        var res = await _admin.GetAsync("/api/reports/stock/report.csv");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType?.MediaType);
        var text = await res.Content.ReadAsStringAsync();
        Assert.Contains($"Out of stock,{out1}", text, StringComparison.Ordinal);
        Assert.Contains($"Low stock,{low1}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_an_administrator_can_read_it()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _employee.GetAsync("/api/reports/stock")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _employee.GetAsync("/api/reports/stock/report.csv")).StatusCode);
    }
}
