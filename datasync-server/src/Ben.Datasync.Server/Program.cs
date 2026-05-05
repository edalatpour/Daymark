// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using CommunityToolkit.Datasync.Server;
using CommunityToolkit.Datasync.Server.NSwag;
using CommunityToolkit.Datasync.Server.OpenApi;
using CommunityToolkit.Datasync.Server.Swashbuckle;
using System.IdentityModel.Tokens.Jwt;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Ben.Datasync.Server;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new ApplicationException("DefaultConnection is not set");

string? swaggerDriver = builder.Configuration["Swagger:Driver"];
bool nswagEnabled = swaggerDriver?.Equals("NSwag", StringComparison.InvariantCultureIgnoreCase) == true;
bool swashbuckleEnabled = swaggerDriver?.Equals("Swashbuckle", StringComparison.InvariantCultureIgnoreCase) == true;
bool openApiEnabled = swaggerDriver?.Equals("NET9", StringComparison.InvariantCultureIgnoreCase) == true;

AuthSchemeSettings legacyAuth = LoadAuthSchemeSettings(builder.Configuration, "AzureAd");
AuthSchemeSettings ciamAuth = LoadAuthSchemeSettings(builder.Configuration, "AzureAdCiam");

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = "DynamicJwt";
        options.DefaultChallengeScheme = "DynamicJwt";
    })
    .AddPolicyScheme("DynamicJwt", "Select JWT scheme by issuer", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            string? authorization = context.Request.Headers.Authorization;
            string? token = ExtractBearerToken(authorization);
            string? issuer = GetUnvalidatedIssuer(token);
            string? audience = GetUnvalidatedAudience(token);

            string selectedScheme = "LegacyJwt";

            if (!string.IsNullOrWhiteSpace(issuer)
                && issuer.Contains("ciamlogin.com", StringComparison.OrdinalIgnoreCase))
            {
                selectedScheme = "CiamJwt";
            }

            ILogger selectorLogger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Ben.Datasync.Server.AuthSchemeSelector");

            selectorLogger.LogInformation(
                "Auth scheme {Scheme} selected for {Method} {Path}. UnvalidatedIssuer={Issuer}, UnvalidatedAudience={Audience}",
                selectedScheme,
                context.Request.Method,
                context.Request.Path,
                issuer ?? "<null>",
                audience ?? "<null>");

            return selectedScheme;
        };
    })
    .AddJwtBearer("LegacyJwt", options =>
    {
        ConfigureJwtBearer(options, legacyAuth, "LegacyJwt");
    })
    .AddJwtBearer("CiamJwt", options =>
    {
        ConfigureJwtBearer(options, ciamAuth, "CiamJwt");
    });

builder.Services.AddHttpContextAccessor();
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(connectionString);
    options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
});
builder.Services.AddDatasyncServices();
builder.Services.AddControllers();

if (nswagEnabled)
{
    _ = builder.Services.AddOpenApiDocument(options => options.AddDatasyncProcessor());
}

if (swashbuckleEnabled)
{
    _ = builder.Services.AddEndpointsApiExplorer();
    _ = builder.Services.AddSwaggerGen(options => options.AddDatasyncControllers());
}

if (openApiEnabled)
{
    // Explicit API Explorer configuration
    _ = builder.Services.AddEndpointsApiExplorer();
    _ = builder.Services.AddOpenApi(options => options.AddDatasyncTransformers());
}

WebApplication app = builder.Build();

// Initialize the database
using (IServiceScope scope = app.Services.CreateScope())
{
    AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    ILogger<Program> logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        List<string> appliedBefore = [.. await context.Database.GetAppliedMigrationsAsync()];
        List<string> pendingBefore = [.. await context.Database.GetPendingMigrationsAsync()];

        logger.LogInformation("EF migrations before initialization. Applied: {AppliedCount} ({AppliedMigrations}); Pending: {PendingCount} ({PendingMigrations})",
            appliedBefore.Count,
            appliedBefore.Count == 0 ? "none" : string.Join(", ", appliedBefore),
            pendingBefore.Count,
            pendingBefore.Count == 0 ? "none" : string.Join(", ", pendingBefore));

        await context.InitializeDatabaseAsync();

        List<string> appliedAfter = [.. await context.Database.GetAppliedMigrationsAsync()];
        List<string> pendingAfter = [.. await context.Database.GetPendingMigrationsAsync()];

        logger.LogInformation("EF migrations after initialization. Applied: {AppliedCount} ({AppliedMigrations}); Pending: {PendingCount} ({PendingMigrations})",
            appliedAfter.Count,
            appliedAfter.Count == 0 ? "none" : string.Join(", ", appliedAfter),
            pendingAfter.Count,
            pendingAfter.Count == 0 ? "none" : string.Join(", ", pendingAfter));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialization failed. Continuing startup without blocking the web process.");
    }
}

// Respect X-Forwarded-Proto from ACA ingress (and any other reverse proxy).
// This prevents UseHttpsRedirection from looping when TLS is terminated upstream.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseHttpsRedirection();

if (nswagEnabled)
{
    _ = app.UseOpenApi().UseSwaggerUI();
}

if (swashbuckleEnabled)
{
    _ = app.UseSwagger().UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

if (openApiEnabled)
{
    _ = app.MapOpenApi(pattern: "swagger/{documentName}/swagger.json");
    _ = app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Ben.Datasync.Server v1"));
}

app.Run();

static AuthSchemeSettings LoadAuthSchemeSettings(IConfiguration configuration, string sectionName)
{
    string authority = configuration[$"{sectionName}:Authority"]
        ?? throw new ApplicationException($"{sectionName}:Authority is not set");
    string? configuredAudience = configuration[$"{sectionName}:Audience"];
    string? clientId = configuration[$"{sectionName}:ClientId"];
    string[] validAudiences = [.. new[]
    {
        configuredAudience,
        clientId,
        string.IsNullOrWhiteSpace(clientId) ? null : $"api://{clientId}"
    }
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Cast<string>()];

    if (validAudiences.Length == 0)
    {
        throw new ApplicationException($"At least one of {sectionName}:Audience or {sectionName}:ClientId must be set");
    }

    return new AuthSchemeSettings(authority, validAudiences);
}

static void ConfigureJwtBearer(JwtBearerOptions options, AuthSchemeSettings settings, string schemeName)
{
    options.Authority = settings.Authority;
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidAudiences = settings.ValidAudiences
    };

    // For multi-tenant Microsoft authorities (common/organizations), metadata exposes
    // a templated issuer. Accept concrete tenant issuers from login.microsoftonline.com.
    if (string.Equals(schemeName, "LegacyJwt", StringComparison.Ordinal)
        && IsMicrosoftMultiTenantAuthority(settings.Authority))
    {
        options.TokenValidationParameters.IssuerValidator = static (issuer, _, _) =>
        {
            if (IsMicrosoftTenantIssuer(issuer))
            {
                return issuer;
            }

            throw new SecurityTokenInvalidIssuerException(
                $"LegacyJwt issuer '{issuer}' is not a supported Microsoft Entra tenant issuer.");
        };
    }

    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            ILogger logger = context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Ben.Datasync.Server.JwtAuth");

            string issuer = (context.SecurityToken as JwtSecurityToken)?.Issuer ?? "<unknown>";
            string audience = (context.SecurityToken as JwtSecurityToken)?.Audiences.FirstOrDefault() ?? "<unknown>";
            string subject = context.Principal?.FindFirst("sub")?.Value
                ?? context.Principal?.Identity?.Name
                ?? "<unknown>";

            logger.LogInformation(
                "JWT validated for scheme {Scheme}. Issuer={Issuer}, Audience={Audience}, Subject={Subject}",
                schemeName,
                issuer,
                audience,
                subject);

            return Task.CompletedTask;
        },
        OnAuthenticationFailed = context =>
        {
            ILogger logger = context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Ben.Datasync.Server.JwtAuth");

            string? authorization = context.Request.Headers.Authorization;
            string? token = ExtractBearerToken(authorization);

            logger.LogWarning(
                context.Exception,
                "JWT authentication failed for scheme {Scheme}. Authority={Authority}, AllowedAudiences={AllowedAudiences}, UnvalidatedIssuer={Issuer}, UnvalidatedAudience={Audience}",
                schemeName,
                settings.Authority,
                string.Join(",", settings.ValidAudiences),
                GetUnvalidatedIssuer(token) ?? "<null>",
                GetUnvalidatedAudience(token) ?? "<null>");

            return Task.CompletedTask;
        },
        OnChallenge = context =>
        {
            ILogger logger = context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Ben.Datasync.Server.JwtAuth");

            logger.LogWarning(
                "JWT challenge for scheme {Scheme}. Error={Error}, ErrorDescription={ErrorDescription}",
                schemeName,
                context.Error ?? "<none>",
                context.ErrorDescription ?? "<none>");

            return Task.CompletedTask;
        }
    };
}

static string? ExtractBearerToken(string? authorizationHeader)
{
    if (string.IsNullOrWhiteSpace(authorizationHeader)
        || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    return authorizationHeader["Bearer ".Length..].Trim();
}

static string? GetUnvalidatedIssuer(string? token)
{
    if (string.IsNullOrWhiteSpace(token))
    {
        return null;
    }

    try
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        return jwt.Issuer;
    }
    catch
    {
        return null;
    }
}

static string? GetUnvalidatedAudience(string? token)
{
    if (string.IsNullOrWhiteSpace(token))
    {
        return null;
    }

    try
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        return string.Join(",", jwt.Audiences);
    }
    catch
    {
        return null;
    }
}

static bool IsMicrosoftMultiTenantAuthority(string authority)
{
    if (string.IsNullOrWhiteSpace(authority))
    {
        return false;
    }

    return authority.Contains("login.microsoftonline.com/common/", StringComparison.OrdinalIgnoreCase)
        || authority.Contains("login.microsoftonline.com/organizations/", StringComparison.OrdinalIgnoreCase);
}

static bool IsMicrosoftTenantIssuer(string? issuer)
{
    if (string.IsNullOrWhiteSpace(issuer))
    {
        return false;
    }

    return Regex.IsMatch(
        issuer,
        "^https://login\\.microsoftonline\\.com/[0-9a-fA-F-]{36}/v2\\.0/?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));
}

sealed record AuthSchemeSettings(string Authority, string[] ValidAudiences);
