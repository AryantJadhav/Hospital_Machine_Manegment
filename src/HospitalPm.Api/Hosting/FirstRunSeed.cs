using System.Text.Json;
using System.Text.Json.Nodes;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Identity;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Hosting;

/// <summary>
/// Settings the installer collected, applied on the first start.
/// </summary>
public sealed class FirstRunOptions
{
    public const string Section = "FirstRun";

    public string? HospitalName { get; set; }

    public string? AdminUserName { get; set; }

    public string? AdminFullName { get; set; }

    /// <summary>
    /// Cleared from the settings file as soon as the account exists. It is
    /// only here because the installer has no other way to hand it over, and
    /// leaving a password on disk after it has been used is indefensible.
    /// </summary>
    public string? AdminPassword { get; set; }
}

/// <summary>
/// Creates the first administrator from what the installer was told, so a
/// hospital finishes the installer with a working login rather than a web
/// page asking them to invent one.
///
/// Does nothing if any user already exists. Reinstalling must not create a
/// second administrator, and must certainly not reset the first one's
/// password.
/// </summary>
public static class FirstRunSeed
{
    public static async Task ApplyAsync(WebApplication app, string settingsFile)
    {
        var options = app.Configuration.GetSection(FirstRunOptions.Section).Get<FirstRunOptions>();

        if (options is null ||
            string.IsNullOrWhiteSpace(options.AdminUserName) ||
            string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            return;
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalPmDbContext>();

        if (await db.Users.AnyAsync())
        {
            // Already set up. Clear the password anyway: it should not sit on
            // disk one restart longer than it has to.
            ClearAdminPassword(app, settingsFile);
            return;
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = options.AdminUserName.Trim(),
            FullName = string.IsNullOrWhiteSpace(options.AdminFullName)
                ? options.AdminUserName.Trim()
                : options.AdminFullName.Trim(),
            IsActive = true,
        };

        var created = await users.CreateAsync(user, options.AdminPassword);
        if (!created.Succeeded)
        {
            // Logged, never thrown. A weak password supplied at install time
            // must not stop the service from starting - the operator can
            // still create an account through the setup page.
            StartupLog.FirstRunAdminFailed(
                app.Logger,
                string.Join(" ", created.Errors.Select(e => e.Description)));
            return;
        }

        var roled = await users.AddToRoleAsync(user, Roles.Admin);
        if (!roled.Succeeded)
        {
            StartupLog.FirstRunAdminFailed(
                app.Logger, "the Admin role could not be assigned");
            return;
        }

        StartupLog.FirstRunAdminCreated(app.Logger, user.UserName!);
        ClearAdminPassword(app, settingsFile);
    }

    /// <summary>
    /// Removes the password from the settings file, leaving everything else
    /// alone. Rewritten through a JSON node tree rather than regenerated, so
    /// anything else the installer or an administrator put there survives.
    /// </summary>
    private static void ClearAdminPassword(WebApplication app, string settingsFile)
    {
        try
        {
            if (!File.Exists(settingsFile)) return;

            var root = JsonNode.Parse(File.ReadAllText(settingsFile)) as JsonObject;
            if (root?[FirstRunOptions.Section] is not JsonObject firstRun) return;

            if (!firstRun.ContainsKey(nameof(FirstRunOptions.AdminPassword))) return;

            firstRun.Remove(nameof(FirstRunOptions.AdminPassword));

            File.WriteAllText(
                settingsFile,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Worth knowing about - a password is still on disk - but never
            // worth refusing to start over.
            StartupLog.FirstRunPasswordNotCleared(app.Logger, settingsFile);
        }
    }
}
