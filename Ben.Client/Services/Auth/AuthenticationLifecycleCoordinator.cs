using Ben.Data;
using Microsoft.EntityFrameworkCore;

namespace Ben.Services.Auth;

public sealed class AuthenticationLifecycleCoordinator : IAuthenticationLifecycleCoordinator
{
    private readonly DatasyncSyncService _syncService;
    private readonly PlannerDbContext _plannerDbContext;
    private readonly LocalSchemaDbContext _schemaDbContext;
    private readonly AuthenticationService _authenticationService;
    private readonly ExternalIdAuthService _externalIdAuthService;

    public AuthenticationLifecycleCoordinator(
        DatasyncSyncService syncService,
        PlannerDbContext plannerDbContext,
        LocalSchemaDbContext schemaDbContext,
        AuthenticationService authenticationService,
        ExternalIdAuthService externalIdAuthService)
    {
        _syncService = syncService;
        _plannerDbContext = plannerDbContext;
        _schemaDbContext = schemaDbContext;
        _authenticationService = authenticationService;
        _externalIdAuthService = externalIdAuthService;
    }

    public async Task<bool> InitializeSignedInUserAsync()
    {
        try
        {
            bool dbRecreated = await _plannerDbContext.RecreateAndInitializeDatabaseAsync();
            if (!dbRecreated)
            {
                Console.WriteLine("Warning: Database recreation failed, but proceeding with sync initialization.");
            }

            await _schemaDbContext.Database.EnsureCreatedAsync();
            LocalMigrationRunner.ApplyMigrations(_schemaDbContext);

            bool clientInitialized = await _plannerDbContext.ReinitializeDatasyncClientAsync();
            if (!clientInitialized)
            {
                Console.WriteLine("Warning: Datasync client reinitialization failed.");
            }

            _syncService.Start();
            _ = _syncService.TrySyncNowAsync();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Authentication lifecycle initialization failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SignOutWithCleanupAsync()
    {
        try
        {
            await _syncService.CancelAndDisposeAsync();

            _plannerDbContext.ChangeTracker.Clear();
            _schemaDbContext.ChangeTracker.Clear();

            var plannerConnection = _plannerDbContext.Database.GetDbConnection();
            if (plannerConnection.State != System.Data.ConnectionState.Closed)
            {
                plannerConnection.Close();
            }

            var schemaConnection = _schemaDbContext.Database.GetDbConnection();
            if (schemaConnection.State != System.Data.ConnectionState.Closed)
            {
                schemaConnection.Close();
            }

            bool dbDeleted = await _plannerDbContext.DeleteDatabaseFileAsync();
            if (!dbDeleted)
            {
                Console.WriteLine("Warning: Database file deletion failed, but proceeding with sign-out.");
            }

            if (_authenticationService.IsAuthenticated)
            {
                await _authenticationService.SignOutAsync();
            }

            if (_externalIdAuthService.IsAuthenticated)
            {
                _externalIdAuthService.SignOut();
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Authentication lifecycle sign-out failed: {ex.Message}");
            return false;
        }
    }
}
