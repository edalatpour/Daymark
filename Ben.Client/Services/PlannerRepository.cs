using System.Collections.ObjectModel;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Ben.Data;
using Ben.Models;

namespace Ben.Services;

public class PlannerRepository
{
    private readonly PlannerDbContext _db;
    private readonly DatasyncSyncService _syncService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _dbContextLock = new(1, 1);

    public PlannerRepository(PlannerDbContext db, DatasyncSyncService syncService, IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _syncService = syncService;
        _scopeFactory = scopeFactory;
    }

    public Task<DailyData> LoadDayAsync(DateTime key)
    {
        return LoadPageAsync(KeyConvention.ToDateKey(key));
    }

    public async Task<DailyData> LoadPageAsync(string key)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        PlannerDbContext context = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
        context.ChangeTracker.Clear();

        var tasks = await context.Tasks
            .AsNoTracking()
            .Where(t => t.Key == key)
            .OrderBy(t => t.Priority == "A" ? 0
                : t.Priority == "B" ? 1
                : t.Priority == "C" ? 2
                : 3)
            .ThenBy(t => t.Order)
            .ThenBy(t => t.Id)
            .ToListAsync();

        var parentTaskIds = tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.ParentTaskId))
            .Select(task => task.ParentTaskId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var originalTaskIds = tasks
            .Where(task => !string.IsNullOrWhiteSpace(task.OriginalTaskId))
            .Select(task => task.OriginalTaskId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var allReferencedIds = parentTaskIds
            .Union(originalTaskIds, StringComparer.Ordinal)
            .ToList();

        Dictionary<string, string> keyById = new(StringComparer.Ordinal);
        if (allReferencedIds.Count > 0)
        {
            var referencedKeys = await context.Tasks
                .AsNoTracking()
                .Where(task => allReferencedIds.Contains(task.Id))
                .Select(task => new { task.Id, task.Key })
                .ToListAsync();

            keyById = referencedKeys.ToDictionary(task => task.Id, task => task.Key, StringComparer.Ordinal);
        }

        Dictionary<string, string> projectNamesById = new(StringComparer.Ordinal);
        List<string> referencedProjectIds = keyById.Values
            .Select(GetProjectId)
            .Where(projectId => !string.IsNullOrWhiteSpace(projectId))
            .Distinct(StringComparer.Ordinal)
            .ToList()!;

        if (referencedProjectIds.Count > 0)
        {
            var referencedProjects = await context.Projects
                .AsNoTracking()
                .Where(project => !project.Deleted && referencedProjectIds.Contains(project.Id))
                .Select(project => new { project.Id, project.Name })
                .ToListAsync();

            projectNamesById = referencedProjects.ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);
        }

        foreach (var task in tasks)
        {
            // Task list shows the original task's date
            if (!string.IsNullOrWhiteSpace(task.OriginalTaskId)
                && keyById.TryGetValue(task.OriginalTaskId, out string? originalKey))
            {
                string originalDateText = ToPageDisplay(originalKey, projectNamesById);
                task.ForwardedFromDate = string.IsNullOrWhiteSpace(originalDateText) ? null : $"{originalDateText}";
            }
            else if (!string.IsNullOrWhiteSpace(task.ParentTaskId)
                && keyById.TryGetValue(task.ParentTaskId, out string? parentKeyFallback))
            {
                string parentDateText = ToPageDisplay(parentKeyFallback, projectNamesById);
                task.ForwardedFromDate = string.IsNullOrWhiteSpace(parentDateText) ? null : $"{parentDateText}";
            }
            else
            {
                task.ForwardedFromDate = null;
            }

            // Store parent task date separately for task details
            if (!string.IsNullOrWhiteSpace(task.ParentTaskId)
                && keyById.TryGetValue(task.ParentTaskId, out string? parentKey))
            {
                string parentDateText = ToPageDisplay(parentKey, projectNamesById);
                task.ParentTaskDate = string.IsNullOrWhiteSpace(parentDateText) ? null : parentDateText;
            }
            else
            {
                task.ParentTaskDate = null;
            }
        }

        var notes = await context.Notes
            .AsNoTracking()
            .Where(n => n.Key == key)
            .OrderBy(n => n.Order)
            .ThenBy(n => n.Id)
            .ToListAsync();

        return new DailyData
        {
            Key = key,
            Date = KeyConvention.TryParseDateKey(key, out DateTime date) ? date : DateTime.Today,
            Tasks = new ObservableCollection<TaskItem>(tasks),
            Notes = new ObservableCollection<NoteItem>(notes)
        };
    }

    public async Task AddTaskAsync(TaskItem task, bool triggerSync = true)
    {
        await _dbContextLock.WaitAsync();
        try
        {
            _db.Tasks.Add(task);
            await _db.SaveChangesAsync();
        }
        finally
        {
            _dbContextLock.Release();
        }

        if (triggerSync)
        {
            _ = _syncService.TriggerSyncAsync();
        }
    }

    public void TriggerSync()
    {
        _ = _syncService.TriggerSyncAsync();
    }

    public Task<string?> GetTaskKeyByIdAsync(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult<string?>(null);
        }

        return _db.Tasks
            .Where(task => task.Id == taskId)
            .Select(task => task.Key)
            .FirstOrDefaultAsync();
    }

    public Task<string?> GetNoteKeyByIdAsync(string noteId)
    {
        if (string.IsNullOrWhiteSpace(noteId))
        {
            return Task.FromResult<string?>(null);
        }

        return _db.Notes
            .Where(note => note.Id == noteId)
            .Select(note => note.Key)
            .FirstOrDefaultAsync();
    }

    public async Task<string?> GetProjectKeyByIdAsync(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        string? id = await _db.Projects
            .Where(project => !project.Deleted && project.Id == projectId)
            .Select(project => project.Id)
            .FirstOrDefaultAsync();

        return string.IsNullOrWhiteSpace(id)
            ? null
            : KeyConvention.ToProjectKey(id);
    }

    public Task<List<string>> GetProjectKeysAsync()
    {
        return _db.Projects
            .Where(project => !project.Deleted)
            .OrderBy(project => project.Name)
            .Select(project => KeyConvention.ToProjectKey(project.Id))
            .ToListAsync();
    }

    public Task<string?> GetProjectNameByKeyAsync(string? key)
    {
        if (!KeyConvention.TryGetProjectId(key, out string projectId))
        {
            return Task.FromResult<string?>(null);
        }

        return _db.Projects
            .Where(project => !project.Deleted && project.Id == projectId)
            .Select(project => project.Name)
            .FirstOrDefaultAsync();
    }

    public async Task<string> GetPageDisplayAsync(string? key)
    {
        if (KeyConvention.TryParseDateKey(key, out _))
        {
            return KeyConvention.ToShortPageDisplay(key);
        }

        string? projectName = await GetProjectNameByKeyAsync(key);
        return KeyConvention.ToShortPageDisplay(key, projectName);
    }

    public async Task<List<ProjectItem>> GetProjectsAsync()
    {
        return await _db.Projects
            .Where(project => !project.Deleted)
            .OrderBy(project => project.Name)
            .ThenBy(project => project.Id)
            .ToListAsync();
    }

    public Task<bool> ProjectExistsAsync(string normalizedName)
    {
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return Task.FromResult(false);
        }

        return _db.Projects.AnyAsync(project => !project.Deleted && project.NormalizedName == normalizedName);
    }

    public Task<bool> ProjectExistsAsync(string normalizedName, string? excludedProjectId)
    {
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return Task.FromResult(false);
        }

        return _db.Projects.AnyAsync(project =>
            !project.Deleted
            && project.NormalizedName == normalizedName
            && project.Id != excludedProjectId);
    }

    public async Task AddProjectAsync(ProjectItem project)
    {
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
        TriggerSync();
    }

    public async Task UpdateProjectAsync(ProjectItem project)
    {
        _db.Projects.Update(project);
        await _db.SaveChangesAsync();
        TriggerSync();
    }

    public async Task<string?> GetEarliestNonEmptyDateKeyAsync()
    {
        List<string> taskDateKeys = await _db.Tasks
            .Where(task => task.Key.StartsWith(KeyConvention.DatePrefix))
            .Select(task => task.Key)
            .Distinct()
            .ToListAsync();

        List<string> noteDateKeys = await _db.Notes
            .Where(note => note.Key.StartsWith(KeyConvention.DatePrefix))
            .Select(note => note.Key)
            .Distinct()
            .ToListAsync();

        return taskDateKeys
            .Concat(noteDateKeys)
            .Where(KeyConvention.IsDateKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public async Task<string?> GetLatestNonEmptyDateKeyAsync()
    {
        List<string> taskDateKeys = await _db.Tasks
            .Where(task => task.Key.StartsWith(KeyConvention.DatePrefix))
            .Select(task => task.Key)
            .Distinct()
            .ToListAsync();

        List<string> noteDateKeys = await _db.Notes
            .Where(note => note.Key.StartsWith(KeyConvention.DatePrefix))
            .Select(note => note.Key)
            .Distinct()
            .ToListAsync();

        return taskDateKeys
            .Concat(noteDateKeys)
            .Where(KeyConvention.IsDateKey)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(key => key, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public async Task UpdateTaskAsync(TaskItem task, bool triggerSync = true)
    {
        await _dbContextLock.WaitAsync();
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                // Preserve forward semantics: if a forwarded parent is edited, remove existing children.
                await DeleteChildTasksInternalAsync(task.Id);

                _db.Tasks.Update(task);
                await _db.SaveChangesAsync();

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        finally
        {
            _dbContextLock.Release();
        }


        if (triggerSync)
        {
            TriggerSync();
        }
    }

    public async Task UpdateTasksAsync(IEnumerable<TaskItem> tasks, bool triggerSync = true)
    {
        List<TaskItem> uniqueTasks = tasks
            .Where(task => task != null)
            .GroupBy(task => task.Id)
            .Select(group => group.First())
            .ToList();

        if (uniqueTasks.Count == 0)
        {
            return;
        }

        await _dbContextLock.WaitAsync();
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                // Preserve forward semantics for all updated parents, then persist once.
                foreach (TaskItem task in uniqueTasks)
                {
                    await DeleteChildTasksInternalAsync(task.Id);
                }

                _db.Tasks.UpdateRange(uniqueTasks);
                await _db.SaveChangesAsync();

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        finally
        {
            _dbContextLock.Release();
        }


        if (triggerSync)
        {
            TriggerSync();
        }
    }

    public async Task ForwardTaskAsync(
        TaskItem sourceTask,
        string destinationKey,
        bool triggerSync = true,
        ForwardedTaskSeed? forwardedTaskSeed = null)
    {
        // Quick validation before starting transaction
        if (sourceTask == null || string.IsNullOrWhiteSpace(destinationKey) ||
            string.Equals(sourceTask.Key, destinationKey, StringComparison.Ordinal))
        {
            return;
        }

        await _dbContextLock.WaitAsync();
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                // Delete any existing child tasks to prevent duplicates
                // when a previously forwarded task is edited and forwarded again
                await DeleteChildTasksInternalAsync(sourceTask.Id);

                if (!TryQueueForwardTask(
                    sourceTask,
                    destinationKey,
                    out _,
                    forwardedTaskSeed))
                {
                    return;
                }

                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        finally
        {
            _dbContextLock.Release();
        }


        if (triggerSync)
        {
            TriggerSync();
        }
    }

    bool TryQueueForwardTask(
        TaskItem sourceTask,
        string destinationKey,
        out TaskItem? forwardedTask,
        ForwardedTaskSeed? forwardedTaskSeed = null)
    {
        forwardedTask = null;

        if (sourceTask == null || string.IsNullOrWhiteSpace(destinationKey))
        {
            return false;
        }

        if (string.Equals(sourceTask.Key, destinationKey, StringComparison.Ordinal))
        {
            return false;
        }

        string copiedStatus = string.IsNullOrWhiteSpace(forwardedTaskSeed?.Status)
            ? sourceTask.Status
            : forwardedTaskSeed.Value.Status!;
        string copiedPriority = forwardedTaskSeed?.Priority ?? string.Empty;
        int copiedOrder = forwardedTaskSeed?.Order ?? 0;
        copiedOrder = Math.Max(0, copiedOrder);

        sourceTask.Status = "Forwarded";

        _db.Tasks.Update(sourceTask);

        forwardedTask = CreateForwardedTask(sourceTask, destinationKey, copiedStatus, copiedPriority, copiedOrder);
        _db.Tasks.Add(forwardedTask);

        return true;
    }

    static TaskItem CreateForwardedTask(
        TaskItem sourceTask,
        string destinationKey,
        string status,
        string priority,
        int order)
    {
        return new TaskItem
        {
            Title = sourceTask.Title,
            Key = destinationKey,
            Status = status,
            Priority = priority,
            Order = order,
            ParentTaskId = sourceTask.Id,
            OriginalTaskId = sourceTask.OriginalTaskId ?? sourceTask.Id
        };
    }

    public async Task<(int Count, List<string> SqlStatements)> BuildCatchUpForwardSqlPreviewAsync(string destinationDateKey)
    {
        if (!KeyConvention.TryParseDateKey(destinationDateKey, out DateTime destinationDate))
        {
            return (0, []);
        }

        string destinationDateValue = destinationDate.ToString(KeyConvention.DateFormat);

        List<TaskItem> sourceTasks = await _db.Tasks
            .FromSqlInterpolated($@"
                SELECT [Id], [UpdatedAt], [Version], [Deleted], [Key], [Status], [Priority], [Order], [Title], [ParentTaskId], [OriginalTaskId]
                FROM [Tasks]
                WHERE [Key] LIKE {KeyConvention.DatePrefix + "%"}
                  AND substr([Key], {KeyConvention.DatePrefix.Length + 1}) < {destinationDateValue}
                  AND [Status] IN ('NotStarted', 'InProgress')
                  AND [Deleted] = 0
                ORDER BY [Key], [Priority], [Order], [Id]
            ")
            .AsNoTracking()
            .ToListAsync();

        Console.WriteLine($"CatchUp preview: {sourceTasks.Count} open task(s) before {destinationDateValue}.");

        List<string> statements = new(capacity: sourceTasks.Count);

        foreach (TaskItem task in sourceTasks)
        {
            string sourceTaskIdLiteral = ToSqlLiteral(task.Id);
            string destinationKeyLiteral = ToSqlLiteral(destinationDateKey);
            string newTaskIdLiteral = ToSqlLiteral(Guid.NewGuid().ToString("N"));

            statements.Add(
                                $@"UPDATE Tasks
SET [Status] = 'Forwarded'
WHERE [Id] = '{sourceTaskIdLiteral}'
    AND [Status] IN ('NotStarted', 'InProgress')
    AND [Deleted] = 0;

INSERT INTO Tasks ([Id], [UpdatedAt], [Version], [Deleted], [Key], [Status], [Priority], [Order], [Title], [ParentTaskId], [OriginalTaskId])
SELECT '{newTaskIdLiteral}', NULL, NULL, 0, '{destinationKeyLiteral}', '{ToSqlLiteral(task.Status)}', '', 0, [Title], [Id], COALESCE([OriginalTaskId], [Id])
FROM Tasks
WHERE [Id] = '{sourceTaskIdLiteral}'
    AND [Status] = 'Forwarded'
    AND changes() > 0;");
        }

        Console.WriteLine($"CatchUp preview destination key: {destinationDateKey}, generated SQL statements: {statements.Count}.");

        return (sourceTasks.Count, statements);
    }

    public async Task<(int CandidateCount, int ExecutedStatements)> ExecuteCatchUpForwardSqlAsync(string destinationDateKey)
    {
        if (!KeyConvention.TryParseDateKey(destinationDateKey, out DateTime destinationDate))
        {
            return (0, 0);
        }

        string destinationDateValue = destinationDate.ToString(KeyConvention.DateFormat);

        List<TaskItem> sourceTasks = await _db.Tasks
            .FromSqlInterpolated($@"
                SELECT [Id], [UpdatedAt], [Version], [Deleted], [Key], [Status], [Priority], [Order], [Title], [ParentTaskId], [OriginalTaskId]
                FROM [Tasks]
                WHERE [Key] LIKE {KeyConvention.DatePrefix + "%"}
                  AND substr([Key], {KeyConvention.DatePrefix.Length + 1}) < {destinationDateValue}
                  AND [Status] IN ('NotStarted', 'InProgress')
                  AND [Deleted] = 0
                ORDER BY [Key], [Priority], [Order], [Id]
            ")
            .ToListAsync();

        if (sourceTasks.Count == 0)
        {
            return (0, 0);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();

        try
        {
            int forwardedCount = 0;

            foreach (TaskItem task in sourceTasks)
            {
                if (TryQueueForwardTask(task, destinationDateKey, out _))
                {
                    forwardedCount++;
                }
            }

            await _db.SaveChangesAsync();

            await transaction.CommitAsync();
            Console.WriteLine($"CatchUp execute: saved {sourceTasks.Count} source update(s) and {forwardedCount} forwarded task insert(s) for destination {destinationDateKey}.");
            return (sourceTasks.Count, forwardedCount);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task DeleteTaskAsync(TaskItem task)
    {
        await _dbContextLock.WaitAsync();
        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                // Clean up any child tasks (forwarded tasks) before deleting the parent
                await DeleteChildTasksInternalAsync(task.Id);

                _db.Tasks.Remove(task);
                await _db.SaveChangesAsync();

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        finally
        {
            _dbContextLock.Release();
        }


        TriggerSync();
    }

    /// <summary>
    /// Deletes all child tasks (tasks where ParentTaskId == parentTaskId) from the database.
    /// This is used to clean up forwarded tasks when a parent task is updated or re-forwarded.
    /// Callers are expected to persist via SaveChanges within their surrounding transaction.
    /// </summary>
    private async Task DeleteChildTasksInternalAsync(string parentTaskId)
    {
        if (string.IsNullOrWhiteSpace(parentTaskId))
        {
            return;
        }

        // IMPORTANT: Keep this as tracked EF deletes (no ExecuteDelete/direct SQL).
        // OfflineDbContext must see entity deletes so Datasync queues matching delete operations.
        List<TaskItem> childTasks = await _db.Tasks
            .Where(task => task.ParentTaskId == parentTaskId)
            .ToListAsync();

        if (childTasks.Count == 0)
        {
            return;
        }

        _db.Tasks.RemoveRange(childTasks);
    }

    public async Task AddNoteAsync(NoteItem note, bool triggerSync = true)
    {
        await _dbContextLock.WaitAsync();
        try
        {
            _db.Notes.Add(note);
            await _db.SaveChangesAsync();
        }
        finally
        {
            _dbContextLock.Release();
        }

        if (triggerSync)
        {
            TriggerSync();
        }
    }

    public async Task UpdateNoteAsync(NoteItem note, bool triggerSync = true)
    {
        await _dbContextLock.WaitAsync();
        try
        {
            _db.Notes.Update(note);
            await _db.SaveChangesAsync();
        }
        finally
        {
            _dbContextLock.Release();
        }

        if (triggerSync)
        {
            TriggerSync();
        }
    }

    public async Task DeleteNoteAsync(NoteItem note)
    {
        await _dbContextLock.WaitAsync();
        try
        {
            _db.Notes.Remove(note);
            await _db.SaveChangesAsync();
        }
        finally
        {
            _dbContextLock.Release();
        }

        TriggerSync();
    }

    public async Task<List<NoteSearchResult>> SearchNotesAsync(
        string searchText,
        CancellationToken cancellationToken = default,
        int maxResults = 100)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return [];
        }

        int cappedResults = Math.Clamp(maxResults, 1, 500);
        string normalizedSearch = searchText.Trim().ToLowerInvariant();

        List<NoteItem> matchingNotes = await _db.Notes
            .AsNoTracking()
            .Where(note =>
                !note.Deleted
                && note.Key.StartsWith(KeyConvention.DatePrefix)
                && note.Text != null
                && note.Text.ToLower().Contains(normalizedSearch))
            .OrderByDescending(note => note.Key)
            .ThenBy(note => note.Order)
            .ThenBy(note => note.Id)
            .Take(cappedResults)
            .ToListAsync(cancellationToken);

        List<NoteSearchResult> results = [];

        foreach (NoteItem note in matchingNotes)
        {
            if (!KeyConvention.TryParseDateKey(note.Key, out DateTime date))
            {
                continue;
            }

            results.Add(new NoteSearchResult
            {
                NoteId = note.Id,
                DateKey = note.Key,
                Date = date,
                Order = note.Order,
                Text = note.Text
            });
        }

        return results;
    }

    public async Task EnsureNoteOrderBackfillAsync()
    {
        if (!await TableExistsAsync("Notes"))
        {
            return;
        }

        await EnsureNoteOrderColumnAsync();

        if (!await _db.Notes.AnyAsync())
        {
            return;
        }

        bool hasNonZeroOrder = await _db.Notes.AnyAsync(note => note.Order != 0);
        if (hasNonZeroOrder)
        {
            return;
        }

        List<NoteItem> notes = await _db.Notes
            .OrderBy(note => note.Key)
            .ThenBy(note => note.Id)
            .ToListAsync();

        string currentKey = string.Empty;
        int order = 0;

        foreach (NoteItem note in notes)
        {
            if (note.Key != currentKey)
            {
                currentKey = note.Key;
                order = 1;
            }

            note.Order = order++;
        }

        await _db.SaveChangesAsync();
    }

    async Task EnsureNoteOrderColumnAsync()
    {
        var connection = _db.Database.GetDbConnection();
        bool shouldClose = connection.State != ConnectionState.Open;

        if (shouldClose)
        {
            await connection.OpenAsync();
        }

        try
        {
            if (!await TableExistsAsync("Notes", connection))
            {
                return;
            }

            bool hasOrder = false;

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info('Notes');";
                using var reader = await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    string name = reader.GetString(reader.GetOrdinal("name"));
                    if (string.Equals(name, "Order", StringComparison.OrdinalIgnoreCase))
                    {
                        hasOrder = true;
                        break;
                    }
                }
            }

            if (!hasOrder)
            {
                using var alterCommand = connection.CreateCommand();
                alterCommand.CommandText = "ALTER TABLE Notes ADD COLUMN [Order] INTEGER NOT NULL DEFAULT 0;";
                await alterCommand.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    async Task<bool> TableExistsAsync(string tableName, System.Data.Common.DbConnection? existingConnection = null)
    {
        var connection = existingConnection ?? _db.Database.GetDbConnection();
        bool shouldClose = existingConnection == null && connection.State != ConnectionState.Open;

        if (shouldClose)
        {
            await connection.OpenAsync();
        }

        try
        {
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

            object? value = await command.ExecuteScalarAsync();
            long count = Convert.ToInt64(value ?? 0);
            return count > 0;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    static string? GetProjectId(string? key)
    {
        return KeyConvention.TryGetProjectId(key, out string projectId)
            ? projectId
            : null;
    }

    static string ToPageDisplay(string? key, IReadOnlyDictionary<string, string> projectNamesById)
    {
        if (KeyConvention.TryGetProjectId(key, out string projectId)
            && projectNamesById.TryGetValue(projectId, out string? projectName)
            && projectName is not null)
        {
            return KeyConvention.ToShortPageDisplay(key, projectName);
        }

        return KeyConvention.ToShortPageDisplay(key);
    }

    static string ToSqlLiteral(string? value)
    {
        return (value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
    }
}
