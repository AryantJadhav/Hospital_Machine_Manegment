using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HospitalPm.Api.Auth;
using HospitalPm.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Every route in the API, asked by every kind of user, and the answer checked against what the
/// permission table says it should be. A new route that is wired to the wrong permission, left open,
/// or that throws on a bare request fails here without anyone having to remember to write a test for it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed partial class RouteAccessMatrixTests(PostgresFixture fixture, ITestOutputHelper output) : IAsyncLifetime, IDisposable
{
    private const string Password = "RouteMatrix2026!";
    private const string NoSuchNumber = "2000000000";

    private ApiFactory _factory = null!;
    private readonly Dictionary<string, HttpClient> _clients = [];
    private HttpClient _anonymous = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _anonymous = _factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        foreach (var role in Roles.All)
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider
                    .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
                var user = new Infrastructure.Identity.ApplicationUser
                {
                    UserName = $"matrix-{role}-{suffix}", FullName = $"Matrix {role}", IsActive = true,
                };
                Assert.True((await users.CreateAsync(user, Password)).Succeeded);
                await users.AddToRoleAsync(user, role);
            }

            var client = _factory.CreateClient();
            var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = $"matrix-{role}-{suffix}", password = Password });
            login.EnsureSuccessStatusCode();
            var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
            _clients[role] = client;
        }
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _factory?.Dispose();

    [GeneratedRegex(@"\{([^}:]+)(:[^}]*)?\}")]
    private static partial Regex RouteParameter();

    /// <summary>Routes that act on the machine itself (a backup, an update, a restore): never run for real, only refused.</summary>
    private static bool HasSideEffectsOnTheInstallation(string path) =>
        path.Contains("/backups/run", StringComparison.Ordinal)
        || path.Contains("/restore", StringComparison.Ordinal)
        || path.StartsWith("/api/admin/update", StringComparison.Ordinal);

    /// <summary>
    /// Where a person who is allowed in is still told no by the route itself: the staff names list
    /// (for someone who picks nobody), and the printed service report, which carries the prices a
    /// person from another department is not shown.
    /// </summary>
    private static bool RefusesAllowedPeopleByDesign(string method, string pattern, string role) =>
        (pattern == "/api/people" && role is Roles.BmeEngineer or Roles.DepartmentUser)
        || (method == "GET" && pattern == "/api/reports/work-orders/{id:int}/report.pdf" && role == Roles.DepartmentUser);

    private sealed record Route(string Method, string Pattern, string Path, List<PermissionRequirement> Requirements, bool Upload);

    private IEnumerable<Route> Routes()
    {
        foreach (var e in _factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>())
        {
            var pattern = e.RoutePattern.RawText ?? string.Empty;
            if (!pattern.StartsWith("/api/", StringComparison.Ordinal) || e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                continue;
            }

            var requirements = e.Metadata.OfType<AuthorizationPolicy>()
                .SelectMany(p => p.Requirements).OfType<PermissionRequirement>().ToList();
            var path = RouteParameter().Replace(pattern, m => m.Groups[2].Success && m.Groups[2].Value == ":int" ? NoSuchNumber : "zz-no-such");

            // A route that reads a file wants a form, and is asked with one: the framework turns away any
            // other kind of body before it looks at who is asking.
            var upload = e.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IAcceptsMetadata>()
                ?.ContentTypes.Any(c => c.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase)) == true;

            foreach (var method in e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                yield return new Route(method, pattern, path, requirements, upload);
            }
        }
    }

    private static HttpRequestMessage Request(Route r)
    {
        var req = new HttpRequestMessage(new HttpMethod(r.Method), r.Path);
        if (r.Upload)
        {
            req.Content = new MultipartFormDataContent { { new ByteArrayContent([0]), "file", "empty.bin" } };
        }
        else if (r.Method is "POST" or "PUT" or "PATCH")
        {
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return req;
    }

    [Fact]
    public async Task Every_route_gives_every_kind_of_user_the_answer_the_permission_table_says()
    {
        var routes = Routes().ToList();
        Assert.True(routes.Count > 100, $"Expected the whole API, found {routes.Count} routes.");

        var problems = new List<string>();
        var asked = 0;

        foreach (var r in routes)
        {
            // Not signed in: every one of these must say so.
            using (var res = await _anonymous.SendAsync(Request(r)))
            {
                asked++;
                if (res.StatusCode != HttpStatusCode.Unauthorized)
                {
                    problems.Add($"{r.Method} {r.Pattern}: not signed in got {(int)res.StatusCode}, expected 401");
                }
            }

            foreach (var role in Roles.All)
            {
                var held = RolePermissions.For(role);
                var mayEnter = r.Requirements.All(req => req.AnyOf.Any(held.Contains));

                // Allowed in is only run when it cannot change the installation itself.
                if (mayEnter && HasSideEffectsOnTheInstallation(r.Path))
                {
                    continue;
                }

                using var res = await _clients[role].SendAsync(Request(r));
                asked++;
                var code = (int)res.StatusCode;

                if (code >= 500)
                {
                    problems.Add($"{r.Method} {r.Pattern} as {role}: SERVER ERROR {code}");
                }
                else if (!mayEnter && res.StatusCode != HttpStatusCode.Forbidden)
                {
                    problems.Add($"{r.Method} {r.Pattern} as {role}: should be refused (403), got {code}");
                }
                else if (mayEnter && res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                         && !RefusesAllowedPeopleByDesign(r.Method, r.Pattern, role))
                {
                    problems.Add($"{r.Method} {r.Pattern} as {role}: should be let in, got {code}");
                }
            }
        }

        output.WriteLine($"{routes.Count} routes x {Roles.All.Count} kinds of user + not signed in = {asked} requests.");
        Assert.True(problems.Count == 0, $"{problems.Count} of {asked} answers were wrong:\n" + string.Join("\n", problems));
    }

    [Fact]
    public async Task A_file_upload_is_refused_before_it_is_read_for_anyone_not_allowed_to_send_it()
    {
        // The import routes read a spreadsheet from the request. Someone who may not import must be
        // told so whatever they send: a real upload, not just an empty body.
        string[] routes =
        [
            "/api/equipment/import/validate", "/api/equipment/import/report", "/api/equipment/import/commit",
            "/api/equipment/import/locations/validate", "/api/equipment/import/locations/report", "/api/equipment/import/locations/commit",
        ];

        foreach (var route in routes)
        {
            foreach (var role in new[] { Roles.ItAdmin, Roles.BmeEngineer, Roles.DepartmentUser })
            {
                using var form = new MultipartFormDataContent();
                var file = new ByteArrayContent([1, 2, 3, 4]);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                form.Add(file, "file", "sheet.xlsx");

                using var res = await _clients[role].PostAsync(route, form);
                Assert.True(res.StatusCode == HttpStatusCode.Forbidden, $"{route} as {role} with a real upload got {(int)res.StatusCode}, expected 403");
            }

            using var anon = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "sheet.xlsx" } };
            Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.PostAsync(route, anon)).StatusCode);
        }
    }

    [Fact]
    public void No_route_is_left_without_a_permission_unless_every_signed_in_person_is_meant_to_reach_it()
    {
        // Routes that ask only for being signed in. Reading what they hold is deliberate for each: who
        // you are, the names of the sections, the licence banner, the feature switches, signing out,
        // and the printed reports that check what they hold inside.
        string[] openToAnySignedInPerson =
        [
            "POST /api/auth/logout", "GET /api/auth/me", "GET /api/access/catalog", "GET /api/people",
            "GET /api/features", "GET /api/licence/banner",
        ];

        var withoutPermission = Routes()
            .Where(r => r.Requirements.Count == 0)
            .Select(r => $"{r.Method} {r.Pattern}")
            .Distinct().Order(StringComparer.Ordinal).ToList();

        Assert.Equal(openToAnySignedInPerson.Order(StringComparer.Ordinal), withoutPermission);
    }
}
