using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>The rule for a password: at least five characters, with upper case, lower case and a digit.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class PasswordRuleTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private ApiFactory _factory = null!;
    private HttpClient _developer = null!;
    private string _suffix = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser { UserName = $"pw-dev-{_suffix}", FullName = "Password dev", IsActive = true };
            Assert.True((await users.CreateAsync(user, "Start2026Ok!")).Succeeded);
            await users.AddToRoleAsync(user, Roles.Developer);
        }

        _developer = _factory.CreateClient();
        var login = await _developer.PostAsJsonAsync("/api/auth/login", new { userName = $"pw-dev-{_suffix}", password = "Start2026Ok!" });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        _developer.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _factory?.Dispose();

    private Task<HttpResponseMessage> CreateWithAsync(string tag, string password) =>
        _developer.PostAsJsonAsync("/api/users", new
        {
            userName = $"pw-{tag}-{_suffix}", fullName = $"Pw {tag}", staffCode = (string?)null, role = Roles.BmeEngineer, password,
        });

    [Fact]
    public async Task Five_characters_with_upper_lower_and_a_digit_is_enough()
    {
        Assert.Equal(HttpStatusCode.Created, (await CreateWithAsync("five", "Ab1de")).StatusCode);

        // And the account really signs in with it.
        var login = await _factory.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new { userName = $"pw-five-{_suffix}", password = "Ab1de" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Four_characters_is_refused_and_the_message_says_what_the_rule_is()
    {
        var res = await CreateWithAsync("four", "Ab1d");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("5", await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("abcde1")] // no upper case
    [InlineData("ABCDE1")] // no lower case
    [InlineData("Abcdef")] // no digit
    public async Task A_password_still_needs_upper_case_lower_case_and_a_digit(string password)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateWithAsync($"rule{password.Length}{password[..2]}", password)).StatusCode);
    }
}
