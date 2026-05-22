using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Networking;
using Ben.Data;
using Ben.Services;
using Ben.Services.Auth;
using Ben.Views;
using Ben.ViewModels;

namespace Ben;

public static class MauiProgram
{
    private const string PendingSignOutResetKey = "AuthLifecycle.PendingSignOutReset";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.ConfigureFonts(AppFontCatalog.ConfigureFonts);

#if DEBUG
        builder.Logging
            .AddDebug()
            .SetMinimumLevel(LogLevel.Information)
            .AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None)
            .AddFilter("Microsoft.Maui.Controls.Xaml.Diagnostics.BindingDiagnostics", LogLevel.None)
            .AddFilter("Microsoft.Maui.Controls.Style", LogLevel.None);
#endif

        RegisterServices(builder);

        var app = builder.Build();

        // Startup initialization must never crash app launch in TestFlight/App Store builds.
        InitializeStartupServicesSafely(app);

        return app;
    }

    static void RegisterServices(MauiAppBuilder builder)
    {
        string dbPath = Path.Combine(FileSystem.AppDataDirectory, "planner.datasync.db");
        string sqliteConnectionString = $"Filename={dbPath};Default Timeout=1";

        builder.Services.AddSingleton(new DatasyncOptions { Endpoint = new Uri(Constants.ServiceUri) });
        builder.Services.AddSingleton<IConnectivity>(Connectivity.Current);

        builder.Services.AddSingleton<MsalService>();
        builder.Services.AddSingleton<ExternalIdAuthService>();
        builder.Services.AddSingleton<IUnifiedAuthSessionStore, PreferencesUnifiedAuthSessionStore>();
        builder.Services.AddSingleton<IUnifiedAuthService, UnifiedAuthService>();
        builder.Services.AddSingleton<IAuthenticationLifecycleCoordinator, AuthenticationLifecycleCoordinator>();
        builder.Services.AddSingleton<IThemeIdentityService, NoOpThemeIdentityService>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<UserFontService>();
        builder.Services.AddSingleton<SqliteWriteCoordinator>();
        builder.Services.AddSingleton<ILocalDatabaseLifecycleService, LocalDatabaseLifecycleService>();
        builder.Services.AddSingleton<ICloudAccountService, CloudAccountService>();
        builder.Services.AddSingleton<DatasyncSyncService>();

        builder.Services.AddDbContext<LocalSchemaDbContext>(options => options.UseSqlite(sqliteConnectionString));
        builder.Services.AddDbContext<PlannerDbContext>((_, options) => options.UseSqlite(sqliteConnectionString));

        builder.Services.AddSingleton<PlannerRepository>();

        // Keep one host page and one view model alive so date navigation reuses existing views.
        builder.Services.AddSingleton<DailyViewModel>();
        builder.Services.AddSingleton<DailyHostPage>();
    }

    static void InitializeStartupServicesSafely(MauiApp app)
    {
        try
        {
            RecoverFromInterruptedSignOutIfNeeded();

            using var scope = app.Services.CreateScope();

            var pdb = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
            pdb.Database.EnsureCreated();
            EnsurePlannerSchemaUpToDate(pdb);
            // Persist WAL at the file level and keep write contention waits short.
            pdb.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            pdb.Database.ExecuteSqlRaw("PRAGMA busy_timeout=1000;");

            var ldb = scope.ServiceProvider.GetRequiredService<LocalSchemaDbContext>();
            LocalMigrationRunner.ApplyMigrations(ldb);

            var repo = scope.ServiceProvider.GetRequiredService<PlannerRepository>();
            repo.EnsureNoteOrderBackfillAsync().GetAwaiter().GetResult();

            var syncService = scope.ServiceProvider.GetRequiredService<DatasyncSyncService>();
            syncService.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Startup] Initialization failed: {ex}");
            Console.WriteLine($"[Startup] Initialization failed: {ex}");
            TryWriteStartupExceptionToFile(ex);
        }
    }

    static void RecoverFromInterruptedSignOutIfNeeded()
    {
        try
        {
            if (!Preferences.Default.Get(PendingSignOutResetKey, false))
            {
                return;
            }

            string dbPath = Path.Combine(FileSystem.AppDataDirectory, "planner.datasync.db");
            DeleteIfExists(dbPath);
            DeleteIfExists(dbPath + "-wal");
            DeleteIfExists(dbPath + "-shm");

            Preferences.Default.Set(PendingSignOutResetKey, false);
            Console.WriteLine("[Startup] Recovered from interrupted sign-out database reset.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Sign-out recovery failed: {ex.Message}");
        }
    }

    static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    static void EnsurePlannerSchemaUpToDate(PlannerDbContext db)
    {
        // Keep this routine additive only: create missing tables, columns, and indexes,
        // but never delete existing data.
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS Tasks (
                Id TEXT NOT NULL PRIMARY KEY,
                UpdatedAt TEXT NULL,
                Version TEXT NULL,
                Deleted INTEGER NOT NULL DEFAULT 0,
                [Key] TEXT NOT NULL,
                Status TEXT NOT NULL,
                Priority TEXT NOT NULL,
                [Order] INTEGER NOT NULL,
                Title TEXT NOT NULL,
                ParentTaskId TEXT NULL,
                OriginalTaskId TEXT NULL
            );
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS Notes (
                Id TEXT NOT NULL PRIMARY KEY,
                UpdatedAt TEXT NULL,
                Version TEXT NULL,
                Deleted INTEGER NOT NULL DEFAULT 0,
                [Key] TEXT NOT NULL,
                Text TEXT NOT NULL,
                [Order] INTEGER NOT NULL DEFAULT 0
            );
        ");

        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS Projects (
                Id TEXT NOT NULL PRIMARY KEY,
                UpdatedAt TEXT NULL,
                Version TEXT NULL,
                Deleted INTEGER NOT NULL DEFAULT 0,
                Name TEXT NOT NULL,
                NormalizedName TEXT NOT NULL
            );
        ");

        EnsureColumnExists(db, "Tasks", "ParentTaskId", "ALTER TABLE Tasks ADD COLUMN ParentTaskId TEXT;");
        EnsureColumnExists(db, "Tasks", "OriginalTaskId", "ALTER TABLE Tasks ADD COLUMN OriginalTaskId TEXT;");
        EnsureColumnExists(db, "Notes", "Order", "ALTER TABLE Notes ADD COLUMN [Order] INTEGER NOT NULL DEFAULT 0;");

        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Tasks_Key ON Tasks ([Key]);");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Notes_Key ON Notes ([Key]);");
        db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_Projects_NormalizedName ON Projects (NormalizedName);");
    }

    static void EnsureColumnExists(PlannerDbContext db, string tableName, string columnName, string alterSql)
    {
        using var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{tableName}');";

        bool hasColumn = false;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader[1]?.ToString(), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (!hasColumn)
        {
            db.Database.ExecuteSqlRaw(alterSql);
        }
    }

    static void TryWriteStartupExceptionToFile(Exception ex)
    {
        try
        {
            string path = Path.Combine(FileSystem.AppDataDirectory, "startup-init-errors.log");
            string line = $"[{DateTime.UtcNow:O}] {ex}\n";
            File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch
        {
            // Never throw from diagnostics.
        }
    }

}
