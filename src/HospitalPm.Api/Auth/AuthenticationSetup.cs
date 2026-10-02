using HospitalPm.Infrastructure.Identity;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HospitalPm.Api.Auth;

public static class AuthenticationSetup
{
    public static IServiceCollection AddHospitalPmAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        string dataDirectory)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.TryAddSingletonTimeProvider();

        services.AddSingleton(sp => new SigningKeyProvider(
            dataDirectory,
            sp.GetService<ILogger<SigningKeyProvider>>()));

        services.AddIdentityCore<ApplicationUser>(o =>
            {
                // A hospital PC is on a LAN anyone in the building can reach,
                // and there is no MFA here. Lockout is the only brake on
                // sustained password guessing.
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.AllowedForNewUsers = true;

                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireNonAlphanumeric = false;

                // No mail server is assumed on an air-gapped install, so
                // email confirmation cannot be a precondition for sign-in.
                o.SignIn.RequireConfirmedEmail = false;
                o.User.RequireUniqueEmail = false;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<HospitalPmDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.AddScoped<TokenService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured through DI rather than inside AddJwtBearer's callback.
        // Calling services.BuildServiceProvider() there would construct a
        // second container, giving this a different SigningKeyProvider
        // instance than the rest of the app — tokens would be signed with
        // one key and validated against another.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<SigningKeyProvider, IOptions<JwtOptions>>((o, keys, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;

                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = keys.Key,

                    // Default is five minutes, which silently extends every
                    // token's life past its stated expiry. On an install
                    // where all clocks are the same machine's, zero is
                    // correct and makes AccessTokenMinutes mean what it says.
                    ClockSkew = TimeSpan.Zero,
                };
            });

        services.AddAuthorization();
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

        return services;
    }

    private static IServiceCollection TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(d => d.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }

        return services;
    }
}
