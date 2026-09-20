using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>The everyday check of a machine: what is asked, what is kept, what the round shows.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class DiagnosisTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "Diagnose2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;
    private int _typeId;
    private int _otherTypeId;
    private int _wardId;
    private int _roomId;
    private int _elsewhereId;
    private int _templateId;
    private int _versionId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"dx-{_suffix}", Name = $"Diag type {_suffix}" };
            var other = new EquipmentType { Code = $"dy-{_suffix}", Name = $"Other type {_suffix}" };
            db.EquipmentTypes.AddRange(type, other);

            var building = new Location { Code = $"DB-{_suffix}", Name = $"Block {_suffix}", Level = LocationLevel.Building };
            db.Locations.Add(building);
            await db.SaveChangesAsync();

            var ward = new Location
            {
                Code = $"DW-{_suffix}", Name = $"Ward {_suffix}", Level = LocationLevel.Department, ParentId = building.Id,
            };
            db.Locations.Add(ward);
            await db.SaveChangesAsync();

            var room = new Location
            {
                Code = $"DR-{_suffix}", Name = $"Bay {_suffix}", Level = LocationLevel.Room, ParentId = ward.Id,
            };
            var elsewhere = new Location
            {
                Code = $"DE-{_suffix}", Name = $"Elsewhere {_suffix}", Level = LocationLevel.Department, ParentId = building.Id,
            };
            db.Locations.AddRange(room, elsewhere);
            await db.SaveChangesAsync();

            _typeId = type.Id;
            _otherTypeId = other.Id;
            _wardId = ward.Id;
            _roomId = room.Id;
            _elsewhereId = elsewhere.Id;
        }

        _admin = await SignInAsync("dx-admin", Domain.Identity.Roles.Admin);
        _employee = await SignInAsync("dx-emp", Domain.Identity.Roles.Employee);

        // A daily check, written and published the way the hospital would.
        var created = await _admin.PostAsJsonAsync("/api/checklists", new
        {
            equipmentTypeId = _typeId, code = $"DAILY-{_suffix}", name = "Daily check", kind = 20,
        });
        created.EnsureSuccessStatusCode();
        _templateId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var draft = await _admin.PutAsJsonAsync($"/api/checklists/{_templateId}/draft", new
        {
            definition = new
            {
                sections = new[]
                {
                    new
                    {
                        title = "Look and listen",
                        items = new object[]
                        {
                            new { key = "powers_on", label = "Powers on", type = 10, required = true },
                            new { key = "pressure", label = "Pressure", type = 30, required = true, unit = "kPa", min = 10, max = 20 },
                        },
                    },
                },
            },
        });
        draft.EnsureSuccessStatusCode();
        (await _admin.PostAsJsonAsync($"/api/checklists/{_templateId}/publish", new { })).EnsureSuccessStatusCode();

        var form = await _employee.GetFromJsonAsync<JsonElement>($"/api/checklists/{_templateId}/versions");
        _versionId = form.EnumerateArray().First(v => v.GetProperty("status").GetInt32() == 20).GetProperty("id").GetInt32();
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

    private async Task<int> MachineAsync(string tag, int locationId, int? typeId = null)
    {
        var created = await _admin.PostAsJsonAsync("/api/equipment", new
        {
            assetTag = $"{tag}-{_suffix}", equipmentTypeId = typeId ?? _typeId, locationId,
        });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private object Body(string powersOn, string pressure, int outcome, Guid? submission = null) => new
    {
        checklistTemplateVersionId = _versionId,
        answers = new Dictionary<string, object>
        {
            ["powers_on"] = new { value = powersOn },
            ["pressure"] = new { value = pressure },
        },
        outcome,
        notes = "checked",
        clientSubmissionId = submission,
    };

    [Fact]
    public async Task The_form_is_the_published_daily_check_for_the_machines_type()
    {
        var id = await MachineAsync("DX1", _roomId);

        var form = await _employee.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}/diagnosis/form");

        Assert.Equal("Daily check", form.GetProperty("checklistName").GetString());
        Assert.Equal(_versionId, form.GetProperty("checklistTemplateVersionId").GetInt32());
        Assert.Equal(2, form.GetProperty("definition").GetProperty("sections")[0].GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_type_with_no_daily_check_says_so()
    {
        var id = await MachineAsync("DX2", _roomId, _otherTypeId);

        var response = await _employee.GetAsync($"/api/equipment/{id}/diagnosis/form");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("No daily check", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_employee_records_a_diagnosis_and_it_shows_in_the_history()
    {
        var id = await MachineAsync("DX3", _roomId);

        var recorded = await _employee.PostAsJsonAsync(
            $"/api/equipment/{id}/diagnoses", Body("fail", "25", outcome: 20));
        recorded.EnsureSuccessStatusCode();
        var result = await recorded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("failedCheckCount").GetInt32());
        Assert.Equal(1, result.GetProperty("outOfRangeCount").GetInt32());

        var history = await _employee.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}/diagnoses");
        Assert.Equal(1, history.GetArrayLength());
        Assert.Equal(20, history[0].GetProperty("outcome").GetInt32());
        Assert.Equal("dx-emp person", history[0].GetProperty("performedBy").GetString());
        Assert.Equal($"Bay {_suffix}", history[0].GetProperty("location").GetString());

        var one = await _employee.GetFromJsonAsync<JsonElement>(
            $"/api/diagnoses/{history[0].GetProperty("id").GetInt32()}");
        Assert.Equal("fail", one.GetProperty("answers").GetProperty("powers_on").GetProperty("value").GetString());
    }

    [Fact]
    public async Task An_incomplete_check_or_a_wrong_checklist_is_refused()
    {
        var id = await MachineAsync("DX4", _roomId);

        var missing = await _employee.PostAsJsonAsync($"/api/equipment/{id}/diagnoses", new
        {
            checklistTemplateVersionId = _versionId,
            answers = new Dictionary<string, object> { ["powers_on"] = new { value = "pass" } },
            outcome = 10,
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var otherType = await MachineAsync("DX4B", _roomId, _otherTypeId);
        var wrongType = await _employee.PostAsJsonAsync(
            $"/api/equipment/{otherType}/diagnoses", Body("pass", "15", outcome: 10));
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);

        var badOutcome = await _employee.PostAsJsonAsync(
            $"/api/equipment/{id}/diagnoses", Body("pass", "15", outcome: 99));
        Assert.Equal(HttpStatusCode.BadRequest, badOutcome.StatusCode);

        Assert.Equal(0, (await _employee.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}/diagnoses")).GetArrayLength());
    }

    [Fact]
    public async Task A_retry_with_the_same_submission_is_not_recorded_twice()
    {
        var id = await MachineAsync("DX5", _roomId);
        var submission = Guid.NewGuid();

        (await _employee.PostAsJsonAsync($"/api/equipment/{id}/diagnoses", Body("pass", "15", 10, submission)))
            .EnsureSuccessStatusCode();
        var again = await _employee.PostAsJsonAsync($"/api/equipment/{id}/diagnoses", Body("pass", "15", 10, submission));
        again.EnsureSuccessStatusCode();
        Assert.True((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean());

        Assert.Equal(1, (await _employee.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}/diagnoses")).GetArrayLength());
    }

    [Fact]
    public async Task A_recorded_diagnosis_cannot_be_changed_or_deleted()
    {
        var id = await MachineAsync("DX6", _roomId);
        (await _employee.PostAsJsonAsync($"/api/equipment/{id}/diagnoses", Body("pass", "15", 10)))
            .EnsureSuccessStatusCode();

        await using var db = fixture.CreateContext();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE diagnosis SET outcome = 30 WHERE equipment_id = {0}", id));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM diagnosis WHERE equipment_id = {0}", id));
    }

    [Fact]
    public async Task The_round_lists_a_places_machines_and_who_has_been_checked_today()
    {
        var checkedId = await MachineAsync("DX7A", _roomId);
        var notYetId = await MachineAsync("DX7B", _wardId);
        var noChecklistId = await MachineAsync("DX7C", _roomId, _otherTypeId);
        var awayId = await MachineAsync("DX7D", _elsewhereId);

        (await _employee.PostAsJsonAsync($"/api/equipment/{checkedId}/diagnoses", Body("pass", "15", 10)))
            .EnsureSuccessStatusCode();

        // The ward, which holds the bay too, but not the department beside it.
        var round = await _employee.GetFromJsonAsync<JsonElement>($"/api/diagnosis/rounds?locationId={_wardId}");
        var items = round.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetInt32());

        Assert.Contains(checkedId, items.Keys);
        Assert.Contains(notYetId, items.Keys);
        Assert.Contains(noChecklistId, items.Keys);
        Assert.DoesNotContain(awayId, items.Keys);

        Assert.Equal(10, items[checkedId].GetProperty("checkedToday").GetProperty("outcome").GetInt32());
        Assert.Equal(JsonValueKind.Null, items[notYetId].GetProperty("checkedToday").ValueKind);
        Assert.True(items[notYetId].GetProperty("hasChecklist").GetBoolean());
        Assert.False(items[noChecklistId].GetProperty("hasChecklist").GetBoolean());
    }

    [Fact]
    public async Task A_diagnosis_checklist_cannot_be_used_for_a_pm_schedule()
    {
        var id = await MachineAsync("DX8", _roomId);

        var schedule = await _admin.PostAsJsonAsync("/api/pm/schedules", new
        {
            equipmentId = id, checklistTemplateId = _templateId,
            frequency = 10, intervalDays = 0, anchorDate = "2027-01-01", graceDays = 3,
        });

        Assert.Equal(HttpStatusCode.BadRequest, schedule.StatusCode);
        Assert.Contains("Unknown checklist", await schedule.Content.ReadAsStringAsync());
    }
}
