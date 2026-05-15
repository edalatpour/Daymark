using System;
using CommunityToolkit.Datasync.Client.Authentication;
using CommunityToolkit.Datasync.Client.Http;
using CommunityToolkit.Datasync.Client.Offline;
using Microsoft.EntityFrameworkCore;
using Ben.Models;
using Ben.Services;
using Ben.Services.Auth;

namespace Ben.Data;

public class PlannerDbContext : OfflineDbContext
{
    private readonly DatasyncOptions _options;
    private readonly IUnifiedAuthService _unifiedAuthService;

    public PlannerDbContext(
        DbContextOptions<PlannerDbContext> options,
        DatasyncOptions datasyncOptions,
        IUnifiedAuthService unifiedAuthService)
        : base(options)
    {
        _options = datasyncOptions;
        _unifiedAuthService = unifiedAuthService;
    }

    public DbSet<TaskItem> Tasks { get; set; }
    public DbSet<NoteItem> Notes { get; set; }
    public DbSet<ProjectItem> Projects { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TaskItem>()
            .HasIndex(t => t.Key);

        modelBuilder.Entity<NoteItem>()
            .HasIndex(n => n.Key);

        modelBuilder.Entity<ProjectItem>()
            .HasIndex(project => project.NormalizedName)
            .IsUnique();
    }


    protected override void OnDatasyncInitialization(DatasyncOfflineOptionsBuilder optionsBuilder)
    {
        if (_options.Endpoint == null)
        {
            return;
        }

        // Datasync always requests tokens through the unified auth runtime.
        HttpClientOptions clientOptions = new()
        {
            Endpoint = _options.Endpoint,
            Timeout = TimeSpan.FromSeconds(30),
            HttpPipeline = new[] { new GenericAuthenticationProvider(GetUnifiedAuthenticationTokenAsync) }
        };
        optionsBuilder.UseHttpClientOptions(clientOptions);

        optionsBuilder.Entity<TaskItem>(cfg =>
        {
            cfg.Endpoint = new Uri("/tables/taskitem", UriKind.Relative);
            cfg.ConflictResolver = new ClientWinsConflictResolver();
        });

        optionsBuilder.Entity<NoteItem>(cfg =>
        {
            cfg.Endpoint = new Uri("/tables/noteitem", UriKind.Relative);
            cfg.ConflictResolver = new ClientWinsConflictResolver();
        });

        optionsBuilder.Entity<ProjectItem>(cfg =>
        {
            cfg.Endpoint = new Uri("/tables/projectitem", UriKind.Relative);
            cfg.ConflictResolver = new ClientWinsConflictResolver();
        });
    }

    /// <summary>
    /// Returns an <see cref="AuthenticationToken"/> from the active provider via the
    /// unified authentication service.
    /// Called by the Datasync <see cref="GenericAuthenticationProvider"/> on every
    /// outgoing HTTP request so the backend can identify and filter data for the user.
    /// </summary>
    private Task<AuthenticationToken> GetUnifiedAuthenticationTokenAsync(CancellationToken cancellationToken)
    {
        return _unifiedAuthService.GetAuthenticationTokenAsync(cancellationToken);
    }

    /// <summary>
    /// Delete the SQLite database file from storage.
    /// Used when signing out to ensure complete data isolation between users.
    /// </summary>
    public async Task<bool> DeleteDatabaseFileAsync()
    {
        try
        {
            string dbPath = Path.Combine(FileSystem.AppDataDirectory, "planner.datasync.db");

            await Database.CloseConnectionAsync();
            await Database.EnsureDeletedAsync();

            DeleteIfExists(dbPath);
            DeleteIfExists(dbPath + "-wal");
            DeleteIfExists(dbPath + "-shm");

            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to delete database file - access denied: {ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to delete database file - file in use: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to delete database file: {ex.Message}");
            return false;
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Recreate the database to match current EF Core entity definitions.
    /// Called after database deletion during sign-in to ensure schema is up-to-date.
    /// </summary>
    public async Task<bool> RecreateAndInitializeDatabaseAsync()
    {
        try
        {
            await Database.EnsureCreatedAsync();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to recreate database: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reinitialize the Datasync client with the new user's authentication token.
    /// Called after successful sign-in to set up Datasync for the new user.
    /// The OfflineDbContext will automatically use the updated JWT from GetAuthenticationTokenAsync.
    /// </summary>
    public async Task<bool> ReinitializeDatasyncClientAsync()
    {
        try
        {
            // Trigger Datasync initialization with the new auth token
            // OfflineDbContext uses lazy initialization, so accessing any Datasync operations
            // will trigger OnDatasyncInitialization with the current JWT from AuthenticationService

            // Force initialization by calling GetService which triggers the setup
            var dbConnection = Database.GetDbConnection();
            if (dbConnection.State != System.Data.ConnectionState.Open)
            {
                await dbConnection.OpenAsync();
            }
            dbConnection.Close();

            System.Diagnostics.Debug.WriteLine("Datasync client reinitialized for new user authentication");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to reinitialize Datasync client: {ex.Message}");
            return false;
        }
    }
}

