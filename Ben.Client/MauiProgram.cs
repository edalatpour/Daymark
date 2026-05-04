using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Networking;
using Ben.Data;
using Ben.Services;
using Ben.Views;
using Ben.ViewModels;

namespace Ben;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.ConfigureFonts(AppFontCatalog.ConfigureFonts);

#if DEBUG
        builder.Logging.AddDebug();
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

        builder.Services.AddSingleton(new DatasyncOptions { Endpoint = new Uri(Constants.ServiceUri) });
        builder.Services.AddSingleton<IConnectivity>(Connectivity.Current);

        builder.Services.AddSingleton<AuthenticationService>();
        builder.Services.AddSingleton<ExternalIdAuthService>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<UserFontService>();
        builder.Services.AddSingleton<DatasyncSyncService>();

        builder.Services.AddDbContext<LocalSchemaDbContext>(options => options.UseSqlite("Filename=" + dbPath));
        builder.Services.AddDbContext<PlannerDbContext>((_, options) => options.UseSqlite("Filename=" + dbPath));

        builder.Services.AddSingleton<PlannerRepository>();

        // Keep one host page and one view model alive so date navigation reuses existing views.
        builder.Services.AddSingleton<DailyViewModel>();
        builder.Services.AddSingleton<DailyHostPage>();
    }

    static void InitializeStartupServicesSafely(MauiApp app)
    {
        try
        {
            using var scope = app.Services.CreateScope();

            var pdb = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
            EnsurePlannerSchema(pdb);
            pdb.Database.EnsureCreated();
            // Persist WAL at the file level and keep write contention waits short.
            pdb.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            pdb.Database.ExecuteSqlRaw("PRAGMA busy_timeout=1000;");

            var ldb = scope.ServiceProvider.GetRequiredService<LocalSchemaDbContext>();
            LocalMigrationRunner.ApplyMigrations(ldb);

            var repo = scope.ServiceProvider.GetRequiredService<PlannerRepository>();
            repo.EnsureNoteOrderBackfillAsync().GetAwaiter().GetResult();

            var syncService = scope.ServiceProvider.GetRequiredService<DatasyncSyncService>();
            syncService.Start();
            _ = syncService.TrySyncNowAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Startup] Initialization failed: {ex}");
            Console.WriteLine($"[Startup] Initialization failed: {ex}");
            TryWriteStartupExceptionToFile(ex);
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

    static void EnsurePlannerSchema(PlannerDbContext db)
    {
        bool hasTasks = TableExists(db, "Tasks");
        bool hasNotes = TableExists(db, "Notes");
        bool hasProjects = TableExists(db, "Projects");

        if (hasTasks && hasNotes && hasProjects)
        {
            return;
        }

        bool hasAnyPlannerTables = hasTasks || hasNotes || hasProjects;
        bool hasAnyTables = DatabaseHasAnyUserTables(db);

        // Recover from first-run partial schema where only SchemaInfo exists.
        if (!hasAnyPlannerTables && hasAnyTables)
        {
            db.Database.EnsureDeleted();
            return;
        }
    }

    static bool TableExists(DbContext db, string tableName)
    {
        using var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = $name;
        ";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        object? value = command.ExecuteScalar();
        long count = Convert.ToInt64(value ?? 0);
        return count > 0;
    }

    static bool DatabaseHasAnyUserTables(DbContext db)
    {
        using var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%';
        ";

        object? value = command.ExecuteScalar();
        long count = Convert.ToInt64(value ?? 0);
        return count > 0;
    }
}
