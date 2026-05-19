using CommunityToolkit.Datasync.Client.Offline;
using CommunityToolkit.Datasync.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Networking;
using Ben.Data;
using Ben.Services.Auth;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.IO;

namespace Ben.Services;

public sealed record SyncIssueInfo(
    string? EntityType,
    string? EntityId,
    string? EntityKey,
    string? EntityTitle,
    int? StatusCode,
    string? ReasonPhrase,
    bool IsConflict);

public sealed class DatasyncSyncService : IDisposable
{
    private static readonly TimeSpan AutoSyncDebounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WriteGateBusyRetryInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan PendingChangesSyncInterval = TimeSpan.FromSeconds(30);
    private static readonly string SyncTraceLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ben",
        "sync-trace.log");
    private static readonly object SyncTraceGate = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectivity _connectivity;
    private readonly IUnifiedAuthService _unifiedAuthService;
    private readonly DatasyncOptions _options;
    private readonly ILogger<DatasyncSyncService> _logger;
    private readonly SqliteWriteCoordinator _sqliteWriteCoordinator;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly object _syncScheduleGate = new();
    private CancellationTokenSource? _periodicSyncCts;
    private Task? _periodicSyncTask;
    private bool _started;
    private bool _disposed;
    private CancellationTokenSource? _scheduledSyncCts;
    private int _pendingCountSnapshot;
    private long _suppressSyncUntilUnixMs;
    private SyncIssueInfo? _latestSyncIssue;

    public event EventHandler? SyncStarted;
    public event EventHandler? SyncCompleted;
    public event EventHandler<SyncIssueInfo>? SyncIssueDetected;

    public SyncIssueInfo? LatestSyncIssue => _latestSyncIssue;

    public DatasyncSyncService(
        IServiceScopeFactory scopeFactory,
        IConnectivity connectivity,
        IUnifiedAuthService unifiedAuthService,
        DatasyncOptions options,
        ILogger<DatasyncSyncService> logger,
        SqliteWriteCoordinator sqliteWriteCoordinator)
    {
        _scopeFactory = scopeFactory;
        _connectivity = connectivity;
        _unifiedAuthService = unifiedAuthService;
        _options = options;
        _logger = logger;
        _sqliteWriteCoordinator = sqliteWriteCoordinator;
    }

    /// <summary>
    /// Returns <c>true</c> when the user is signed in with any identity provider
    /// through the unified authentication runtime.
    /// </summary>
    private bool IsAnyAuthenticated => _unifiedAuthService.IsAuthenticated;

    public void Start()
    {
        if (_disposed)
        {
            _logger.LogWarning("Cannot start sync service after disposal.");
            return;
        }

        if (_started)
        {
            return;
        }

        if (_options.Endpoint == null)
        {
            return;
        }

        _started = true;
        _connectivity.ConnectivityChanged += OnConnectivityChanged;
        _periodicSyncCts = new CancellationTokenSource();
        _periodicSyncTask = RunPendingChangesSyncLoopAsync(_periodicSyncCts.Token);
        AppendSyncTrace("Start: sync service started.");
    }

    /// <summary>
    /// Cancel any in-progress sync and pause auto-sync. Used for sign-out cleanup.
    /// </summary>
    public async Task CancelAndDisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _logger.LogInformation("Canceling active sync and pausing sync service for sign-out");

        try
        {
            // Unsubscribe from connectivity events to prevent auto-sync attempts
            _connectivity.ConnectivityChanged -= OnConnectivityChanged;
            _started = false;
            _periodicSyncCts?.Cancel();

            // Try to cancel any in-progress sync operation
            if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                _logger.LogWarning("Sync operation did not complete within timeout during sign-out");
            }
            else
            {
                _logger.LogInformation("Sync operation canceled for sign-out");
                _syncLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during sync cancellation");
        }
        finally
        {
            if (_periodicSyncTask != null)
            {
                try
                {
                    await _periodicSyncTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected when canceling sign-out loop.
                }
            }

            _periodicSyncTask = null;
            _periodicSyncCts?.Dispose();
            _periodicSyncCts = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _started = false;
        _connectivity.ConnectivityChanged -= OnConnectivityChanged;

        CancellationTokenSource? scheduledSyncCts;
        lock (_syncScheduleGate)
        {
            scheduledSyncCts = _scheduledSyncCts;
            _scheduledSyncCts = null;
        }

        if (scheduledSyncCts != null)
        {
            scheduledSyncCts.Cancel();
            scheduledSyncCts.Dispose();
        }

        _periodicSyncCts?.Cancel();
        _periodicSyncCts?.Dispose();

        // Intentionally do not dispose _syncLock here.
        // Fire-and-forget work may still be unwinding during app shutdown,
        // and disposing the semaphore can throw ObjectDisposedException on close.
    }

    private async Task RunPendingChangesSyncLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PendingChangesSyncInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!_started || _disposed)
                {
                    continue;
                }

                if (_connectivity.NetworkAccess != NetworkAccess.Internet || !IsAnyAuthenticated)
                {
                    continue;
                }

                _logger.LogInformation("Periodic sync tick attempting sync.");

                await TrySyncAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when sign-out cancels the periodic loop.
        }
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        AppendSyncTrace($"ConnectivityChanged: NetworkAccess={e.NetworkAccess}.");
        if (e.NetworkAccess == NetworkAccess.Internet)
        {
            _ = TrySyncAsync();
        }
    }

    public Task TriggerSyncAsync()
    {
        AppendSyncTrace($"TriggerSyncAsync: scheduling sync in {AutoSyncDebounce.TotalMilliseconds}ms.");
        ScheduleSync(AutoSyncDebounce);
        return Task.CompletedTask;
    }

    public void SuppressSyncFor(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long targetUnixMs = nowUnixMs + (long)duration.TotalMilliseconds;

        while (true)
        {
            long currentUnixMs = Interlocked.Read(ref _suppressSyncUntilUnixMs);
            if (currentUnixMs >= targetUnixMs)
            {
                break;
            }

            long original = Interlocked.CompareExchange(ref _suppressSyncUntilUnixMs, targetUnixMs, currentUnixMs);
            if (original == currentUnixMs)
            {
                break;
            }
        }

        AppendSyncTrace($"SuppressSyncFor: suppressed until {DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _suppressSyncUntilUnixMs)):O}.");
    }

    /// <summary>
    /// Check if there are unsynced changes in the local database
    /// </summary>
    public async Task<bool> HasUnsyncedChangesAsync()
    {
        if (_disposed || !IsAnyAuthenticated)
        {
            return false;
        }

        var count = await GetUnsyncedChangesCountAsync();
        return count > 0;
    }

    /// <summary>
    /// Get the number of unsynced changes in the local database
    /// </summary>
    public async Task<int> GetUnsyncedChangesCountAsync()
    {
        if (_disposed)
        {
            return _pendingCountSnapshot;
        }

        bool lockAcquired = false;
        if (!await _syncLock.WaitAsync(0))
        {
            return _pendingCountSnapshot;
        }

        lockAcquired = true;

        try
        {
            if (_disposed || !IsAnyAuthenticated)
            {
                return 0;
            }

            using IServiceScope scope = _scopeFactory.CreateScope();
            PlannerDbContext context = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();

            // Query the Datasync operations queue to get pending changes
            var count = await context.DatasyncOperationsQueue.CountAsync();
            _pendingCountSnapshot = count;
            _logger.LogInformation("Found {Count} pending operations in queue", count);

            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check for unsynced changes.");
            return _pendingCountSnapshot;
        }
        finally
        {
            if (lockAcquired)
            {
                _syncLock.Release();
            }
        }
    }

    /// <summary>
    /// Attempt to sync any pending changes
    /// </summary>
    public async Task<bool> TrySyncNowAsync()
    {
        if (!IsAnyAuthenticated)
        {
            _logger.LogWarning("Cannot sync while not authenticated.");
            return false;
        }

        return await TrySyncAsync();
    }

    private void ScheduleSync(TimeSpan delay)
    {
        if (_disposed)
        {
            AppendSyncTrace("ScheduleSync: ignored because service is disposed.");
            return;
        }

        CancellationTokenSource cts = new();
        CancellationTokenSource? previous = null;

        lock (_syncScheduleGate)
        {
            if (_disposed)
            {
                cts.Dispose();
                return;
            }

            previous = _scheduledSyncCts;
            _scheduledSyncCts = cts;
        }

        if (previous != null)
        {
            previous.Cancel();
            previous.Dispose();
            AppendSyncTrace("ScheduleSync: canceled previous scheduled sync.");
        }

        AppendSyncTrace($"ScheduleSync: next sync scheduled in {delay.TotalMilliseconds}ms.");
        _ = RunScheduledSyncAsync(cts, delay);
    }

    private async Task RunScheduledSyncAsync(CancellationTokenSource cts, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cts.Token);
            }

            cts.Token.ThrowIfCancellationRequested();
            await TrySyncAsync();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_syncScheduleGate)
            {
                if (ReferenceEquals(_scheduledSyncCts, cts))
                {
                    _scheduledSyncCts = null;
                }
            }

            cts.Dispose();
        }
    }

    private async Task<bool> TrySyncAsync()
    {
        if (_options.Endpoint == null)
        {
            AppendSyncTrace("TrySyncAsync: skipped (endpoint not configured).");
            return false;
        }

        // Don't sync if user is not authenticated with any identity provider
        if (!IsAnyAuthenticated)
        {
            _logger.LogInformation("Skipping sync - user is not authenticated.");
            AppendSyncTrace("TrySyncAsync: skipped (not authenticated).");
            return false;
        }

        if (_connectivity.NetworkAccess != NetworkAccess.Internet)
        {
            AppendSyncTrace($"TrySyncAsync: skipped (network access: {_connectivity.NetworkAccess}).");
            return false;
        }

        long suppressedUntil = Interlocked.Read(ref _suppressSyncUntilUnixMs);
        long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (suppressedUntil > nowUnixMs)
        {
            AppendSyncTrace($"TrySyncAsync: skipped (suppressed until {DateTimeOffset.FromUnixTimeMilliseconds(suppressedUntil):O}).");
            ScheduleSync(TimeSpan.FromSeconds(2));
            return false;
        }

        bool lockAcquired = false;
        if (!await _syncLock.WaitAsync(0))
        {
            AppendSyncTrace("TrySyncAsync: skipped (sync lock busy).");
            return false;
        }

        lockAcquired = true;

        bool shouldScheduleRetry = false;
        AppendSyncTrace("TrySyncAsync: started.");
        SyncStarted?.Invoke(this, EventArgs.Empty);

        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            PlannerDbContext context = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
            await ConfigureSqliteLockBehaviorAsync(context);

            // Acquire the gate to serialize writes and prevent concurrent SQLite transactions.
            // Wait with 50ms timeout to remain interactive; if busy, skip sync and retry.
            bool gateAcquired = await _sqliteWriteCoordinator.TryWaitAsync(TimeSpan.FromMilliseconds(50));
            if (!gateAcquired)
            {
                _logger.LogInformation("Skipping sync because SQLite write gate is busy; scheduling quick retry.");
                AppendSyncTrace("TrySyncAsync: skipped (sqlite write gate busy); scheduling quick retry.");
                ScheduleSync(WriteGateBusyRetryInterval);
                return false;
            }

            try
            {
                // Gate is held throughout push/pull to prevent concurrent repository writes.
                // Repository SaveChangesAsync will block until gate is released in finally block.
                int pendingBeforeSync = await context.DatasyncOperationsQueue.CountAsync();
                _pendingCountSnapshot = pendingBeforeSync;
                bool hasLocalPendingChanges = pendingBeforeSync > 0;
                AppendSyncTrace($"TrySyncAsync: pendingBeforeSync={pendingBeforeSync}.");

                bool pushSucceeded = true;

                if (hasLocalPendingChanges)
                {
                    PushResult pushResult = await context.PushAsync();
                    pushSucceeded = pushResult.IsSuccessful;
                    AppendSyncTrace($"TrySyncAsync: push complete. success={pushResult.IsSuccessful}, failedRequests={pushResult.FailedRequests.Count}.");
                    if (!pushResult.IsSuccessful)
                    {
                        _logger.LogWarning("Datasync push completed with {Count} failed requests.",
                            pushResult.FailedRequests.Count);
                        AppendSyncTrace($"TrySyncAsync: push failedRequests={pushResult.FailedRequests.Count}.");

                        foreach (var failedRequest in pushResult.FailedRequests)
                        {
                            string detail = DescribeFailedRequest(failedRequest);
                            _logger.LogWarning("Datasync push failure detail: {FailedRequest}", detail);
                            AppendSyncTrace($"TrySyncAsync: push failure detail: {detail}");

                            if (TryCreateSyncIssueInfo(failedRequest, out SyncIssueInfo issue))
                            {
                                _latestSyncIssue = issue;
                                SyncIssueDetected?.Invoke(this, issue);
                            }
                        }
                    }

                    int pendingAfterPush = await context.DatasyncOperationsQueue.CountAsync();
                    _pendingCountSnapshot = pendingAfterPush;
                    AppendSyncTrace($"TrySyncAsync: pendingAfterPush={pendingAfterPush}.");
                    if (pendingAfterPush > 0)
                    {
                        bool hasFailures = pushResult.FailedRequests.Count > 0;
                        bool allFailuresAreConflicts = hasFailures;
                        if (allFailuresAreConflicts)
                        {
                            foreach (var failedRequest in pushResult.FailedRequests)
                            {
                                string detail = DescribeFailedRequest(failedRequest);
                                bool isConflict = detail.Contains("IsConflictStatusCode=True", StringComparison.OrdinalIgnoreCase)
                                    || detail.Contains("StatusCode=409", StringComparison.Ordinal);

                                if (!isConflict)
                                {
                                    allFailuresAreConflicts = false;
                                    break;
                                }
                            }
                        }

                        if (allFailuresAreConflicts)
                        {
                            AppendSyncTrace("TrySyncAsync: pending queue contains conflict-only failures after push; continuing to pull for conflict convergence.");
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Skipping pull because {Count} pending operations remain in queue after push.",
                                pendingAfterPush);

                            await LogQueueSnapshotAsync(context);
                            shouldScheduleRetry = true;
                            return false;
                        }
                    }
                }
                else
                {
                    AppendSyncTrace("TrySyncAsync: no local pending changes; skipping push and running pull-only.");
                }

                // Re-check suppression after push. A local save may have requested sync suppression
                // while this sync was in progress; skipping pull here releases the write gate sooner.
                long suppressedUntilAfterPush = Interlocked.Read(ref _suppressSyncUntilUnixMs);
                long nowAfterPushUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (suppressedUntilAfterPush > nowAfterPushUnixMs)
                {
                    AppendSyncTrace($"TrySyncAsync: skipping pull (suppressed until {DateTimeOffset.FromUnixTimeMilliseconds(suppressedUntilAfterPush):O}) after push.");
                    shouldScheduleRetry = true;
                    return false;
                }

                PullResult pullResult = await context.PullAsync();
                AppendSyncTrace($"TrySyncAsync: pull complete. success={pullResult.IsSuccessful}, failedRequests={pullResult.FailedRequests.Count}.");
                if (!pullResult.IsSuccessful)
                {
                    _logger.LogWarning("Datasync pull completed with {Count} failed requests.",
                        pullResult.FailedRequests.Count);
                    AppendSyncTrace($"TrySyncAsync: pull failedRequests={pullResult.FailedRequests.Count}.");

                    foreach (var failedRequest in pullResult.FailedRequests)
                    {
                        string detail = DescribeFailedRequest(failedRequest);
                        _logger.LogWarning("Datasync pull failure detail: {FailedRequest}", detail);
                        AppendSyncTrace($"TrySyncAsync: pull failure detail: {detail}");
                    }

                    shouldScheduleRetry = true;
                }

                int pendingAfterPull = await context.DatasyncOperationsQueue.CountAsync();
                _pendingCountSnapshot = pendingAfterPull;
                AppendSyncTrace($"TrySyncAsync: pendingAfterPull={pendingAfterPull}.");

                if (pendingAfterPull == 0)
                {
                    _latestSyncIssue = null;
                }

                if (pendingAfterPull > 0)
                {
                    shouldScheduleRetry = true;
                }

                bool success = pushSucceeded && pullResult.IsSuccessful;
                if (!success)
                {
                    shouldScheduleRetry = true;
                }

                return success;
            }
            finally
            {
                // Release the gate to allow repository writes to proceed.
                _sqliteWriteCoordinator.Release();
            }
        }
        catch (DatasyncException ex)
        {
            _logger.LogError(ex, "Datasync synchronization failed with DatasyncException: {Message}", ex.Message);
            AppendSyncTrace($"TrySyncAsync: DatasyncException: {ex.Message}.");

            using IServiceScope diagnosticsScope = _scopeFactory.CreateScope();
            PlannerDbContext diagnosticsContext = diagnosticsScope.ServiceProvider.GetRequiredService<PlannerDbContext>();
            await LogQueueSnapshotAsync(diagnosticsContext);
            shouldScheduleRetry = await RefreshPendingCountSnapshotAsync();
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Datasync synchronization failed.");
            AppendSyncTrace($"TrySyncAsync: Exception: {ex.Message}.");
            shouldScheduleRetry = await RefreshPendingCountSnapshotAsync();
            return false;
        }
        finally
        {
            if (lockAcquired)
            {
                _syncLock.Release();
            }

            SyncCompleted?.Invoke(this, EventArgs.Empty);
            AppendSyncTrace($"TrySyncAsync: completed. scheduleRetry={shouldScheduleRetry}, pendingSnapshot={_pendingCountSnapshot}.");

            if (shouldScheduleRetry)
            {
                ScheduleSync(RetryInterval);
            }
        }
    }

    private async Task<bool> RefreshPendingCountSnapshotAsync()
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            PlannerDbContext context = scope.ServiceProvider.GetRequiredService<PlannerDbContext>();
            await ConfigureSqliteLockBehaviorAsync(context);
            int count = await context.DatasyncOperationsQueue.CountAsync();
            _pendingCountSnapshot = count;
            return count > 0;
        }
        catch
        {
            return _pendingCountSnapshot > 0;
        }
    }

    private async Task LogQueueSnapshotAsync(PlannerDbContext context)
    {
        try
        {
            int totalPending = await context.DatasyncOperationsQueue.CountAsync();
            _logger.LogWarning("Datasync queue snapshot: {Count} total pending operations.", totalPending);

            var connection = context.Database.GetDbConnection();
            bool shouldClose = connection.State != System.Data.ConnectionState.Open;
            if (shouldClose)
            {
                await connection.OpenAsync();
            }

            try
            {
                List<string> columns = await GetQueueColumnsAsync(connection);
                if (columns.Count == 0)
                {
                    _logger.LogWarning("Datasync queue schema inspection returned no columns.");
                    return;
                }

                string orderColumn = columns.FirstOrDefault(static c =>
                        string.Equals(c, "Sequence", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c, "sequence", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c, "Id", StringComparison.OrdinalIgnoreCase))
                    ?? columns[0];

                string projection = string.Join(", ",
                    columns
                        .Take(12)
                        .Select(static c => $"[{c}]"));

                using var command = connection.CreateCommand();
                command.CommandText = $@"
                    SELECT {projection}
                    FROM [DatasyncOperationsQueue]
                    ORDER BY [{orderColumn}]
                    LIMIT 10;";

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var row = new StringBuilder();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        if (i > 0)
                        {
                            row.Append(", ");
                        }

                        string column = reader.GetName(i);
                        string value = reader.IsDBNull(i) ? "<null>" : reader.GetValue(i)?.ToString() ?? string.Empty;
                        row.Append(column).Append('=').Append(value);
                    }

                    _logger.LogWarning("Pending operation row: {Row}", row.ToString());
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log Datasync queue snapshot.");
        }
    }

    private static async Task<List<string>> GetQueueColumnsAsync(System.Data.Common.DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('DatasyncOperationsQueue');";

        using var reader = await command.ExecuteReaderAsync();
        List<string> columns = new();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static async Task ConfigureSqliteLockBehaviorAsync(PlannerDbContext context)
    {
        // Keep lock waits short so foreground local saves are not blocked behind long sync attempts.
        context.Database.SetCommandTimeout(2);
        await context.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=1000;");
    }

    private static string DescribeFailedRequest(object failedRequest)
    {
        if (failedRequest == null)
        {
            return "<null>";
        }

        Type requestType = failedRequest.GetType();
        PropertyInfo? keyProp = requestType.GetProperty("Key");
        PropertyInfo? valueProp = requestType.GetProperty("Value");

        if (keyProp != null && valueProp != null)
        {
            object? key = SafeGetValue(keyProp, failedRequest);
            object? value = SafeGetValue(valueProp, failedRequest);
            return $"Key={key ?? "<null>"}, Value={DescribeObject(value)}{TryReadContentStream(value)}";
        }

        return DescribeObject(failedRequest);
    }

    private static string DescribeObject(object? value)
    {
        if (value == null)
        {
            return "<null>";
        }

        Type type = value.GetType();
        PropertyInfo[] props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static prop => prop.GetIndexParameters().Length == 0)
            .ToArray();

        if (props.Length == 0)
        {
            return value.ToString() ?? type.FullName ?? type.Name;
        }

        var sb = new StringBuilder();
        sb.Append(type.Name).Append(" {");

        bool first = true;
        foreach (PropertyInfo prop in props)
        {
            object? propValue = SafeGetValue(prop, value);

            if (!ShouldLogProperty(prop.Name, propValue))
            {
                continue;
            }

            if (!first)
            {
                sb.Append(", ");
            }

            first = false;
            sb.Append(prop.Name).Append('=').Append(propValue ?? "<null>");
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string TryReadContentStream(object? serviceResponse)
    {
        if (serviceResponse == null)
        {
            return string.Empty;
        }

        try
        {
            PropertyInfo? streamProp = serviceResponse.GetType().GetProperty("ContentStream");
            if (streamProp == null)
            {
                return string.Empty;
            }

            if (streamProp.GetValue(serviceResponse) is not Stream stream || !stream.CanRead)
            {
                return string.Empty;
            }

            long originalPosition = 0;
            if (stream.CanSeek)
            {
                originalPosition = stream.Position;
                stream.Position = 0;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            string content = reader.ReadToEnd();

            if (stream.CanSeek)
            {
                stream.Position = originalPosition;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            return $", ResponseBody={content}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ShouldLogProperty(string name, object? value)
    {
        if (value == null)
        {
            return true;
        }

        if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or DateTime or DateTimeOffset or Guid)
        {
            return true;
        }

        if (value.GetType().IsEnum)
        {
            return true;
        }

        // Include common HTTP-ish members even when complex.
        return name.Contains("Status", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Reason", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Message", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Uri", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Content", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Request", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Method", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Header", StringComparison.OrdinalIgnoreCase);
    }

    private static object? SafeGetValue(PropertyInfo property, object source)
    {
        try
        {
            return property.GetValue(source);
        }
        catch
        {
            return "<unavailable>";
        }
    }

    private static bool TryCreateSyncIssueInfo(object failedRequest, out SyncIssueInfo info)
    {
        info = new SyncIssueInfo(null, null, null, null, null, null, false);

        try
        {
            object? value = failedRequest.GetType().GetProperty("Value")?.GetValue(failedRequest);
            if (value == null)
            {
                return false;
            }

            int? statusCode = TryGetStatusCode(value);
            bool isConflict = statusCode == 409 || TryGetBooleanProperty(value, "IsConflictStatusCode");
            string? reasonPhrase = value.GetType().GetProperty("ReasonPhrase")?.GetValue(value)?.ToString();

            string? responseBody = TryReadResponseBody(value);
            string? entityType = null;
            string? entityId = null;
            string? entityKey = null;
            string? entityTitle = null;

            if (!string.IsNullOrWhiteSpace(responseBody)
                && TryParseEntityDetailsFromJson(
                    responseBody!,
                    out string? parsedType,
                    out string? parsedId,
                    out string? parsedKey,
                    out string? parsedTitle))
            {
                entityType = parsedType;
                entityId = parsedId;
                entityKey = parsedKey;
                entityTitle = parsedTitle;
            }

            info = new SyncIssueInfo(entityType, entityId, entityKey, entityTitle, statusCode, reasonPhrase, isConflict);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int? TryGetStatusCode(object serviceResponse)
    {
        try
        {
            object? raw = serviceResponse.GetType().GetProperty("StatusCode")?.GetValue(serviceResponse);
            if (raw == null)
            {
                return null;
            }

            if (raw is int code)
            {
                return code;
            }

            if (int.TryParse(raw.ToString(), out int parsed))
            {
                return parsed;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetBooleanProperty(object source, string propertyName)
    {
        try
        {
            object? raw = source.GetType().GetProperty(propertyName)?.GetValue(source);
            return raw is bool flag && flag;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadResponseBody(object serviceResponse)
    {
        try
        {
            PropertyInfo? streamProp = serviceResponse.GetType().GetProperty("ContentStream");
            if (streamProp == null)
            {
                return null;
            }

            if (streamProp.GetValue(serviceResponse) is not Stream stream || !stream.CanRead)
            {
                return null;
            }

            long originalPosition = 0;
            if (stream.CanSeek)
            {
                originalPosition = stream.Position;
                stream.Position = 0;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            string content = reader.ReadToEnd();

            if (stream.CanSeek)
            {
                stream.Position = originalPosition;
            }

            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseEntityDetailsFromJson(
        string json,
        out string? entityType,
        out string? entityId,
        out string? entityKey,
        out string? entityTitle)
    {
        entityType = null;
        entityId = null;
        entityKey = null;
        entityTitle = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("id", out JsonElement idProp) && idProp.ValueKind == JsonValueKind.String)
            {
                entityId = idProp.GetString();
            }

            if (root.TryGetProperty("key", out JsonElement keyProp) && keyProp.ValueKind == JsonValueKind.String)
            {
                entityKey = keyProp.GetString();
            }

            if (root.TryGetProperty("title", out JsonElement titleProp) && titleProp.ValueKind == JsonValueKind.String)
            {
                entityTitle = titleProp.GetString();
            }

            if (root.TryGetProperty("parentTaskId", out _)
                || root.TryGetProperty("priority", out _)
                || root.TryGetProperty("status", out _))
            {
                entityType = "task";
            }
            else if (root.TryGetProperty("text", out _)
                && root.TryGetProperty("order", out _)
                && root.TryGetProperty("key", out _))
            {
                entityType = "note";
            }
            else if (root.TryGetProperty("normalizedName", out _)
                || (root.TryGetProperty("name", out _) && !root.TryGetProperty("key", out _)))
            {
                entityType = "project";
            }

            return !string.IsNullOrWhiteSpace(entityId)
                || !string.IsNullOrWhiteSpace(entityKey)
                || !string.IsNullOrWhiteSpace(entityTitle);
        }
        catch
        {
            return false;
        }
    }


    private static void AppendSyncTrace(string message)
    {
        try
        {
            string? directory = Path.GetDirectoryName(SyncTraceLogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string line = $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}";
            lock (SyncTraceGate)
            {
                File.AppendAllText(SyncTraceLogPath, line);
            }
        }
        catch
        {
            // Diagnostics must never throw.
        }
    }
}
