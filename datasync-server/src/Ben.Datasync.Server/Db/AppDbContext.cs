// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Diagnostics.CodeAnalysis;

namespace Ben.Datasync.Server

{

    public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
    {
        public DbSet<TaskItem> TaskItems => Set<TaskItem>();
        public DbSet<NoteItem> NoteItems => Set<NoteItem>();
        public DbSet<ProjectItem> ProjectItems => Set<ProjectItem>();
        public DbSet<UserRecord> Users => Set<UserRecord>();

        // public DbSet<TodoList> TodoLists => Set<TodoList>();

        public async Task InitializeDatabaseAsync()
        {
            // EnsureCreatedAsync creates the full current schema for a brand-new database.
            // It returns true only when the database (and its tables) were just created.
            bool isNewDatabase = await Database.EnsureCreatedAsync();

            if (isNewDatabase)
            {
                // The schema is already up-to-date via EnsureCreated.
                // Seed all known migration IDs so MigrateAsync treats them as already applied.
                foreach (var migrationId in Database.GetMigrations())
                {
                    await Database.ExecuteSqlRawAsync(
                        "INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES ({0}, {1})",
                        migrationId, "10.0.3");
                }
            }

            // Apply any migrations not yet recorded in history:
            //   - New databases:  all migrations are seeded above; this is a no-op.
            //   - Existing databases that already have history: applies only pending ones.
            //   - Existing databases with no history (transition from EnsureCreated):
            //     run migration.sql against the database first to establish the baseline.
            await Database.MigrateAsync();

            // Defensive fallback for environments where migration discovery/history can drift.
            // Keeps project sync functional even if AddProjects is not picked up as pending.
            await Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID(N'[ProjectItems]') IS NULL
BEGIN
    CREATE TABLE [ProjectItems]
    (
        [Id] nvarchar(450) NOT NULL,
        [Deleted] bit NOT NULL,
        [UpdatedAt] datetimeoffset NULL,
        [Version] rowversion NOT NULL,
        [UserId] nvarchar(256) NOT NULL,
        [Name] nvarchar(128) NOT NULL,
        [NormalizedName] nvarchar(128) NOT NULL,
        CONSTRAINT [PK_ProjectItems] PRIMARY KEY ([Id])
    );
END;

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
    WHERE c.object_id = OBJECT_ID(N'[ProjectItems]')
      AND c.name = N'UserId'
      AND t.name = N'nvarchar'
      AND c.max_length = -1
)
BEGIN
    UPDATE [ProjectItems]
    SET [UserId] = LEFT([UserId], 256)
    WHERE LEN([UserId]) > 256;

    ALTER TABLE [ProjectItems] ALTER COLUMN [UserId] nvarchar(256) NOT NULL;
END;

IF EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[ProjectItems]') AND name = N'IX_ProjectItems_Key'
)
BEGIN
    DROP INDEX [IX_ProjectItems_Key] ON [ProjectItems];
END;

IF COL_LENGTH(N'ProjectItems', N'Key') IS NOT NULL
BEGIN
    ALTER TABLE [ProjectItems] DROP COLUMN [Key];
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[ProjectItems]') AND name = N'IX_ProjectItems_UpdatedAt_Deleted'
)
BEGIN
    CREATE INDEX [IX_ProjectItems_UpdatedAt_Deleted] ON [ProjectItems] ([UpdatedAt], [Deleted]);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'[ProjectItems]') AND name = N'IX_ProjectItems_UserId_NormalizedName'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ProjectItems_UserId_NormalizedName] ON [ProjectItems] ([UserId], [NormalizedName]);
END;

UPDATE taskItems
SET [Key] = N'project:' + projectItems.[Id]
FROM [TaskItems] taskItems
INNER JOIN [ProjectItems] projectItems
        ON projectItems.[UserId] = taskItems.[UserId]
     AND projectItems.[NormalizedName] = UPPER(LTRIM(RTRIM(SUBSTRING(taskItems.[Key], LEN(N'project:') + 1, 4000))))
WHERE taskItems.[Key] LIKE N'project:%'
    AND taskItems.[Key] <> N'project:' + projectItems.[Id];

UPDATE noteItems
SET [Key] = N'project:' + projectItems.[Id]
FROM [NoteItems] noteItems
INNER JOIN [ProjectItems] projectItems
        ON projectItems.[UserId] = noteItems.[UserId]
     AND projectItems.[NormalizedName] = UPPER(LTRIM(RTRIM(SUBSTRING(noteItems.[Key], LEN(N'project:') + 1, 4000))))
WHERE noteItems.[Key] LIKE N'project:%'
    AND noteItems.[Key] <> N'project:' + projectItems.[Id];

IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM [__EFMigrationsHistory]
       WHERE [MigrationId] = N'20260314154500_AddProjects'
   )
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260314154500_AddProjects', N'10.0.3');
END;

IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM [__EFMigrationsHistory]
       WHERE [MigrationId] = N'20260315031000_RemoveProjectKeyColumn'
   )
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260315031000_RemoveProjectKeyColumn', N'10.0.3');
END;
");

            const string datasyncTrigger = @"
            CREATE OR ALTER TRIGGER [dbo].[{0}_datasync] ON [dbo].[{0}] AFTER INSERT, UPDATE AS
            BEGIN
                SET NOCOUNT ON;
                UPDATE
                    [dbo].[{0}]
                SET
                    [UpdatedAt] = SYSUTCDATETIME()
                WHERE
                    [Id] IN (SELECT [Id] FROM INSERTED);
            END
        "
            ;

            // Install the above trigger to set the UpdatedAt field automatically on insert or update.
            foreach (IEntityType table in Model.GetEntityTypes())
            {
                string sql = string.Format(datasyncTrigger, table.GetTableName());
                _ = await Database.ExecuteSqlRawAsync(sql);
            }
        }

        [SuppressMessage("Style", "IDE0058:Expression value is never used", Justification = "Model builder ignores return value.")]
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Tells EF Core that the TodoItem entity has a trigger.
            modelBuilder.Entity<TaskItem>()
                .ToTable(tb => tb.HasTrigger("TaskItem_datasync"));

            // Tells EF Core that the TodoList entity has a trigger.
            modelBuilder.Entity<NoteItem>()
                .ToTable(tb => tb.HasTrigger("NoteItem_datasync"));

            modelBuilder.Entity<ProjectItem>()
                .ToTable(tb => tb.HasTrigger("ProjectItem_datasync"));

            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<TaskItem>()
                .HasOne(t => t.ParentTask)
                .WithMany()
                .HasForeignKey(t => t.ParentTaskId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<TaskItem>()
                .HasIndex(t => t.Key);

            modelBuilder.Entity<TaskItem>()
                .HasOne(t => t.OriginalTask)
                .WithMany()
                .HasForeignKey(t => t.OriginalTaskId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<NoteItem>()
                .HasIndex(n => n.Key);

            modelBuilder.Entity<ProjectItem>()
                .HasIndex(project => new { project.UserId, project.NormalizedName })
                .IsUnique();

            modelBuilder.Entity<ProjectItem>()
                .Property(project => project.UserId)
                .HasMaxLength(256);

            modelBuilder.Entity<UserRecord>()
                .ToTable("Users")
                .HasKey(user => user.UserId);

            modelBuilder.Entity<UserRecord>()
                .Property(user => user.ExternalId)
                .HasMaxLength(200)
                .IsRequired();

            modelBuilder.Entity<UserRecord>()
                .Property(user => user.IdentityProvider)
                .HasMaxLength(50)
                .IsRequired();

            modelBuilder.Entity<UserRecord>()
                .Property(user => user.Email)
                .HasMaxLength(200);

            modelBuilder.Entity<UserRecord>()
                .Property(user => user.CreatedAt)
                .HasColumnType("datetime2(7)");

        }
    }

}

