using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Api.Notifications;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>The hub that carries a new service request to the screens of those who are looking. No database, no network.</summary>
public sealed class AlertHubTests
{
    private static ServiceRequestAlert Alert(int id) =>
        new(id, $"WO-{id}", 1, "BME-1", "Ventilator", "ICU", "High", "Alarm will not stop", "Staff nurse", DateTime.UtcNow);

    [Fact]
    public async Task Everyone_who_is_listening_is_told_of_a_new_request()
    {
        var hub = new AlertHub();
        using var first = hub.Subscribe();
        using var second = hub.Subscribe();
        Assert.Equal(2, hub.Listeners);

        hub.Publish(Alert(1));

        Assert.Equal(1, (await first.Reader.ReadAsync()).Id);
        Assert.Equal(1, (await second.Reader.ReadAsync()).Id);
    }

    [Fact]
    public async Task Someone_who_has_stopped_listening_hears_nothing_and_their_stream_ends()
    {
        var hub = new AlertHub();
        var gone = hub.Subscribe();
        using var stays = hub.Subscribe();

        gone.Dispose();
        Assert.Equal(1, hub.Listeners);

        hub.Publish(Alert(2));

        Assert.Equal(2, (await stays.Reader.ReadAsync()).Id);
        Assert.False(await gone.Reader.WaitToReadAsync());      // closed, not left waiting for ever
    }

    [Fact]
    public void A_request_with_nobody_listening_is_not_an_error()
    {
        var hub = new AlertHub();
        hub.Publish(Alert(3));
        Assert.Equal(0, hub.Listeners);
    }

    [Fact]
    public async Task A_screen_that_falls_far_behind_loses_the_oldest_and_never_holds_anything_up()
    {
        var hub = new AlertHub();
        using var slow = hub.Subscribe();

        for (var i = 1; i <= 80; i++)
        {
            hub.Publish(Alert(i));        // returns at once whatever the reader is doing
        }

        var got = new List<int>();
        while (slow.Reader.TryRead(out var a))
        {
            got.Add(a.Id);
        }

        Assert.Equal(50, got.Count);
        Assert.Equal(31, got[0]);          // the oldest thirty were dropped
        Assert.Equal(80, got[^1]);         // the newest is there
        await Task.CompletedTask;
    }
}

/// <summary>
/// The pop-up for a new service request: a department places one, and the biomedical engineers and the head who are signed in
/// are told at once. Nobody else is.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ServiceRequestAlertTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "RequestAlert2026!";

    private ApiFactory _factory = null!;
    private string _suffix = null!;
    private int _machineId;
    private HttpClient _ward = null!;
    private HttpClient _engineer = null!;
    private HttpClient _head = null!;
    private HttpClient _it = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        int departmentId;
        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"al-{_suffix}", Name = $"Alert type {_suffix}" };
            var department = new Location { Code = $"ALD-{_suffix}", Name = $"Alert ward {_suffix}", Level = LocationLevel.Department };
            db.EquipmentTypes.Add(type);
            db.Locations.Add(department);
            await db.SaveChangesAsync();

            var room = new Location { Code = $"ALR-{_suffix}", Name = $"Alert room {_suffix}", Level = LocationLevel.Room, ParentId = department.Id };
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            var machine = new Domain.Assets.Equipment
            {
                AssetTag = $"ALM-{_suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
            };
            db.Equipment.Add(machine);
            await db.SaveChangesAsync();
            _machineId = machine.Id;
            departmentId = department.Id;
        }

        _head = (await SignInAsync("al-head", Roles.BmeHead)).Client;
        _engineer = (await SignInAsync("al-eng", Roles.BmeEngineer)).Client;
        _it = (await SignInAsync("al-it", Roles.ItAdmin)).Client;
        int wardId;
        (_ward, wardId) = await SignInAsync("al-ward", Roles.DepartmentUser);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PutAsJsonAsync($"/api/users/{wardId}/departments", new { locationIds = new[] { departmentId } })).StatusCode);
    }

    private async Task<(HttpClient Client, int UserId)> SignInAsync(string prefix, string role)
    {
        int userId;
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
            userId = user.Id;
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (client, userId);
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _ward?.Dispose();
        _engineer?.Dispose();
        _head?.Dispose();
        _it?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>An open stream, read a line at a time.</summary>
    private sealed class Listener(HttpResponseMessage response, StreamReader reader) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;

        /// <summary>The next "service-request" event's data, or null if none arrives within the time.</summary>
        public async Task<JsonElement?> NextAsync(TimeSpan within)
        {
            using var cts = new CancellationTokenSource(within);
            try
            {
                var name = string.Empty;
                while (true)
                {
                    var line = await reader.ReadLineAsync(cts.Token);
                    if (line is null)
                    {
                        return null;
                    }

                    if (line.StartsWith("event:", StringComparison.Ordinal))
                    {
                        name = line[6..].Trim();
                    }
                    else if (line.StartsWith("data:", StringComparison.Ordinal) && name == "service-request")
                    {
                        return JsonDocument.Parse(line[5..].Trim()).RootElement.Clone();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            reader.Dispose();
            Response.Dispose();
        }
    }

    private static async Task<Listener> ListenAsync(HttpClient who)
    {
        var res = await who.GetAsync("/api/notifications/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        return new Listener(res, new StreamReader(await res.Content.ReadAsStreamAsync()));
    }

    private Task<HttpResponseMessage> PlaceAsync(HttpClient who, string fault, int priority = 30) =>
        who.PostAsJsonAsync("/api/work-orders", new { equipmentId = _machineId, faultDescription = fault, priority });

    [Fact]
    public async Task A_request_from_a_department_pops_up_for_the_engineer_and_the_head_with_what_they_need_to_open_it()
    {
        using var engineer = await ListenAsync(_engineer);
        using var head = await ListenAsync(_head);

        var placed = await PlaceAsync(_ward, "The alarm will not stop and the screen flickers");
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var created = await placed.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var listener in new[] { engineer, head })
        {
            var alert = await listener.NextAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(alert);
            Assert.Equal(created.GetProperty("id").GetInt32(), alert!.Value.GetProperty("id").GetInt32());
            Assert.Equal(created.GetProperty("number").GetString(), alert.Value.GetProperty("number").GetString());
            Assert.Equal(_machineId, alert.Value.GetProperty("equipmentId").GetInt32());
            Assert.Equal($"ALM-{_suffix}".ToUpperInvariant(), alert.Value.GetProperty("assetTag").GetString());
            Assert.Equal($"Alert type {_suffix}", alert.Value.GetProperty("machine").GetString());
            Assert.Equal($"Alert room {_suffix}", alert.Value.GetProperty("location").GetString());
            Assert.Equal("High", alert.Value.GetProperty("priority").GetString());
            Assert.Equal("The alarm will not stop and the screen flickers", alert.Value.GetProperty("fault").GetString());
            Assert.Equal("al-ward person", alert.Value.GetProperty("reportedBy").GetString());
        }
    }

    [Fact]
    public async Task A_long_fault_is_shortened_and_kept_on_one_line()
    {
        using var engineer = await ListenAsync(_engineer);

        Assert.Equal(HttpStatusCode.Created, (await PlaceAsync(_ward, "line one\nline two " + new string('x', 400))).StatusCode);

        var alert = await engineer.NextAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(alert);
        var fault = alert!.Value.GetProperty("fault").GetString()!;
        Assert.DoesNotContain('\n', fault);
        Assert.True(fault.Length <= 161);
        Assert.StartsWith("line one line two", fault, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_an_engineer_places_does_not_pop_up_and_the_next_one_from_a_department_still_does()
    {
        using var other = await ListenAsync(_head);

        // An engineer reporting a fault is already the one dealing with it: nobody is nudged.
        Assert.Equal(HttpStatusCode.Created, (await PlaceAsync(_engineer, "Found it dead on the round")).StatusCode);
        Assert.Null(await other.NextAsync(TimeSpan.FromMilliseconds(1500)));

        Assert.Equal(HttpStatusCode.Created, (await PlaceAsync(_ward, "Now the ward has one")).StatusCode);
        var alert = await other.NextAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(alert);
        Assert.Equal("Now the ward has one", alert!.Value.GetProperty("fault").GetString());
    }

    [Fact]
    public async Task Only_those_who_work_on_faults_may_listen()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.GetAsync("/api/notifications/stream", HttpCompletionOption.ResponseHeadersRead)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _it.GetAsync("/api/notifications/stream", HttpCompletionOption.ResponseHeadersRead)).StatusCode);

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications/stream", HttpCompletionOption.ResponseHeadersRead)).StatusCode);
    }

    [Fact]
    public async Task A_request_is_saved_whether_or_not_anyone_is_listening()
    {
        // Nobody has the stream open: the request is written all the same, and is in the list.
        var placed = await PlaceAsync(_ward, "Nobody is watching");
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var id = (await placed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var seen = await _engineer.GetFromJsonAsync<JsonElement>($"/api/work-orders/{id}");
        Assert.Equal("Nobody is watching", seen.GetProperty("faultDescription").GetString());
    }
}
