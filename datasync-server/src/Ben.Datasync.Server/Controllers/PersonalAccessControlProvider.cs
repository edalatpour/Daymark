using System;
using System.Linq.Expressions;
using System.Security.Claims;
using CommunityToolkit.Datasync.Server;
using CommunityToolkit.Datasync.Server.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ben.Datasync.Server
{
  public class PersonalAccessControlProvider<TEntity>(IHttpContextAccessor contextAccessor, ILogger<PersonalAccessControlProvider<TEntity>> logger) : IAccessControlProvider<TEntity>
    where TEntity : ITableData, IPersonalEntity
  {
    private const string UserRecordCheckedItemKey = "Ben.UserRecordChecked";

    // private string? UserId { get => contextAccessor.HttpContext?.User?.Identity?.Name; }
    private string? UserId
    {
      get => contextAccessor.HttpContext?.User?.FindFirst("email")?.Value
          ?? contextAccessor.HttpContext?.User?.FindFirst("preferred_username")?.Value
          ?? contextAccessor.HttpContext?.User?.Identity?.Name;
    }

    public Expression<Func<TEntity, bool>> GetDataView()
    {
      logger.LogInformation("GetDataView called. UserId: {UserId}", UserId ?? "(null)");
      return UserId is null ? x => false : x => x.UserId == UserId;
    }

    public async ValueTask<bool> IsAuthorizedAsync(TableOperation op, TEntity? entity, CancellationToken cancellationToken = default)
    {
      string? userId = UserId;
      logger.LogInformation("IsAuthorizedAsync called. Operation: {Operation}, UserId: {UserId}, Entity.UserId: {EntityUserId}",
        op, userId ?? "(null)", entity?.UserId ?? "(null)");

      if (string.IsNullOrWhiteSpace(userId))
      {
        return false;
      }

      await EnsureUserRecordExistsAsync(cancellationToken);
      return op is TableOperation.Create || op is TableOperation.Query || (entity?.UserId == userId);
    }

    public async ValueTask PreCommitHookAsync(TableOperation op, TEntity entity, CancellationToken cancellationToken = default)
    {
      var httpContext = contextAccessor.HttpContext;

      // Log HttpContext existence and basic request info
      logger.LogInformation("PreCommitHookAsync called. Operation: {Operation}", op);
      logger.LogInformation("HttpContext available: {HasContext}", httpContext != null);

      if (httpContext != null)
      {
        logger.LogInformation("Request Path: {Path}, Method: {Method}", httpContext.Request.Path, httpContext.Request.Method);

        // Log Authorization header (auth token)
        var authHeader = httpContext.Request.Headers.Authorization.FirstOrDefault();
        logger.LogInformation("Authorization Header: {AuthHeader}", authHeader ?? "(not provided)");

        logger.LogInformation("User exists: {HasUser}, IsAuthenticated: {IsAuthenticated}",
          httpContext.User != null, httpContext.User?.Identity?.IsAuthenticated ?? false);

        if (httpContext.User?.Identity != null)
        {
          logger.LogInformation("Identity Name: {Name}, AuthType: {AuthType}",
            httpContext.User.Identity.Name ?? "(null)",
            httpContext.User.Identity.AuthenticationType ?? "(null)");

          // Log all claims
          if (httpContext.User.Claims.Any())
          {
            foreach (var claim in httpContext.User.Claims)
            {
              logger.LogInformation("Claim - Type: {ClaimType}, Value: {ClaimValue}", claim.Type, claim.Value);
            }
          }
          else
          {
            logger.LogInformation("No claims found in user identity");
          }
        }
        else
        {
          logger.LogWarning("User.Identity is null");
        }
      }
      else
      {
        logger.LogWarning("HttpContext is null - authentication context unavailable");
      }

      string? userId = UserId;
      logger.LogInformation("UserId extracted from claims/identity: {UserId}", userId ?? "(null)");

      await EnsureUserRecordExistsAsync(cancellationToken);

      if (string.IsNullOrWhiteSpace(userId))
      {
        logger.LogWarning("UserId is null in PreCommitHookAsync. Skipping user assignment and relying on IsAuthorizedAsync to reject the operation.");
        return;
      }

      entity.UserId = userId;
    }

    public ValueTask PostCommitHookAsync(TableOperation op, TEntity entity, CancellationToken cancellationToken = default)
      => ValueTask.CompletedTask;

    private async ValueTask EnsureUserRecordExistsAsync(CancellationToken cancellationToken)
    {
      HttpContext? httpContext = contextAccessor.HttpContext;
      if (httpContext == null)
      {
        return;
      }

      if (httpContext.Items.ContainsKey(UserRecordCheckedItemKey))
      {
        return;
      }

      httpContext.Items[UserRecordCheckedItemKey] = true;

      string? externalId = httpContext.User?.FindFirst("sub")?.Value;
      string? identityProvider = ResolveIdentityProvider(httpContext.User);
      string? email = UserId;

      if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(identityProvider))
      {
        logger.LogWarning("Skipping Users upsert: missing ExternalId or IdentityProvider. ExternalId={ExternalId}, IdentityProvider={IdentityProvider}",
          externalId ?? "(null)",
          identityProvider ?? "(null)");
        return;
      }

      string normalizedExternalId = externalId.Length > 200 ? externalId[..200] : externalId;
      string normalizedIdentityProvider = identityProvider.Length > 50 ? identityProvider[..50] : identityProvider;
      string? normalizedEmail = string.IsNullOrWhiteSpace(email)
        ? null
        : (email.Length > 200 ? email[..200] : email);

      AppDbContext dbContext = httpContext.RequestServices.GetRequiredService<AppDbContext>();

      UserRecord? existing = await dbContext.Users
        .AsNoTracking()
        .FirstOrDefaultAsync(
          user => user.ExternalId == normalizedExternalId && user.IdentityProvider == normalizedIdentityProvider,
          cancellationToken);

      if (existing != null)
      {
        logger.LogInformation("Users record already exists for provider {IdentityProvider} and external id {ExternalId}",
          normalizedIdentityProvider,
          normalizedExternalId);
        return;
      }

      var newUser = new UserRecord
      {
        UserId = Guid.NewGuid(),
        ExternalId = normalizedExternalId,
        IdentityProvider = normalizedIdentityProvider,
        Email = normalizedEmail,
        CreatedAt = DateTime.UtcNow
      };

      dbContext.Users.Add(newUser);
      await dbContext.SaveChangesAsync(cancellationToken);

      logger.LogInformation("Inserted Users record. UserId={UserId}, IdentityProvider={IdentityProvider}, ExternalId={ExternalId}, Email={Email}",
        newUser.UserId,
        newUser.IdentityProvider,
        newUser.ExternalId,
        newUser.Email ?? "(null)");
    }

    private static string? ResolveIdentityProvider(ClaimsPrincipal? user)
    {
      if (user == null)
      {
        return null;
      }

      string? idpClaim = user.FindFirst("idp")?.Value;
      if (!string.IsNullOrWhiteSpace(idpClaim))
      {
        string fromIdp = idpClaim.ToLowerInvariant();
        if (fromIdp.Contains("apple"))
        {
          return "apple";
        }

        if (fromIdp.Contains("google"))
        {
          return "google";
        }

        if (fromIdp.Contains("microsoft") || fromIdp.Contains("live.com") || fromIdp.Contains("entra"))
        {
          return "microsoft";
        }
      }

      string? issuer = user.FindFirst("iss")?.Value;
      if (string.IsNullOrWhiteSpace(issuer))
      {
        return null;
      }

      string normalizedIssuer = issuer.ToLowerInvariant();
      if (normalizedIssuer.Contains("appleid.apple.com"))
      {
        return "apple";
      }

      if (normalizedIssuer.Contains("accounts.google.com"))
      {
        return "google";
      }

      if (normalizedIssuer.Contains("microsoftonline.com") || normalizedIssuer.Contains("ciamlogin.com"))
      {
        return "microsoft";
      }

      return null;
    }
  }

}