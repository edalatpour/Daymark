using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ben.Datasync.Server
{
    [Authorize]
    [ApiController]
    [Route("account")]
    [ApiExplorerSettings(IgnoreApi = false)]
    public sealed class AccountController : ControllerBase
    {
        private readonly AppDbContext _dbContext;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<AccountController> _logger;

        public AccountController(
            AppDbContext dbContext,
            IHttpContextAccessor httpContextAccessor,
            ILogger<AccountController> logger)
        {
            _dbContext = dbContext;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        [HttpPost("delete-cloud-data")]
        public async Task<IActionResult> DeleteCloudDataAsync(CancellationToken cancellationToken)
        {
            HttpContext? httpContext = _httpContextAccessor.HttpContext;
            if (httpContext?.User?.Identity?.IsAuthenticated != true)
            {
                return Unauthorized();
            }

            string? externalId = httpContext.User.FindFirst("sub")?.Value;
            string? identityProvider = ResolveIdentityProvider(httpContext.User);

            if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(identityProvider))
            {
                return BadRequest(new
                {
                    status = "invalid_identity",
                    message = "Cannot resolve identity provider and external id for this account deletion request."
                });
            }

            string normalizedExternalId = externalId.Length > 200 ? externalId[..200] : externalId;
            string normalizedIdentityProvider = identityProvider.Length > 50 ? identityProvider[..50] : identityProvider;

            UserRecord? userRecord = await _dbContext.Users
                .FirstOrDefaultAsync(
                    user => user.ExternalId == normalizedExternalId && user.IdentityProvider == normalizedIdentityProvider,
                    cancellationToken);

            string? canonicalUserId = userRecord?.UserId.ToString();

            if (string.IsNullOrWhiteSpace(canonicalUserId))
            {
                return BadRequest(new
                {
                    status = "invalid_identity",
                    message = "Cannot resolve a canonical user record for cloud data deletion."
                });
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            int tasksDeleted = await _dbContext.TaskItems
                .Where(item => item.UserId == canonicalUserId)
                .ExecuteDeleteAsync(cancellationToken);

            int notesDeleted = await _dbContext.NoteItems
                .Where(item => item.UserId == canonicalUserId)
                .ExecuteDeleteAsync(cancellationToken);

            int projectsDeleted = await _dbContext.ProjectItems
                .Where(item => item.UserId == canonicalUserId)
                .ExecuteDeleteAsync(cancellationToken);

            int usersDeleted = await _dbContext.Users
                .Where(user => user.UserId == userRecord!.UserId)
                .ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Delete-cloud-data succeeded. Provider={IdentityProvider}, CanonicalUserId={CanonicalUserId}, TasksDeleted={TasksDeleted}, NotesDeleted={NotesDeleted}, ProjectsDeleted={ProjectsDeleted}, UsersDeleted={UsersDeleted}",
                normalizedIdentityProvider,
                canonicalUserId ?? "(null)",
                tasksDeleted,
                notesDeleted,
                projectsDeleted,
                usersDeleted);

            return Ok(new
            {
                status = "deleted",
                message = "Cloud data deleted for the authenticated account.",
                hasUserRecord = userRecord != null,
                canonicalUserId,
                identityProvider = normalizedIdentityProvider,
                email = userRecord?.Email,
                deletedCounts = new
                {
                    tasks = tasksDeleted,
                    notes = notesDeleted,
                    projects = projectsDeleted,
                    users = usersDeleted
                }
            });
        }

        private static string? ResolveIdentityProvider(ClaimsPrincipal user)
        {
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