// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Maui.Networking;
using Ben.Data;
using Ben.Models;
using Ben.Services;
using Ben.Views;
// using Microsoft.UI.Xaml.Controls;

#nullable enable

namespace Ben.ViewModels;

public class DailyViewModel : INotifyPropertyChanged
{
    private const int MaxProjectNameLength = 128;
    // private const string QuotesAssetPath = "Quotes/benjamin_franklin.csv";

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly PlannerRepository _repo;
    private readonly AuthenticationService _authService;
    private readonly ExternalIdAuthService _externalIdAuthService;
    private readonly DatasyncSyncService _syncService;
    private readonly PlannerDbContext _dbContext;
    private readonly LocalSchemaDbContext _schemaDbContext;
    private readonly IConnectivity _connectivity;
    private readonly SemaphoreSlim _refreshAfterSyncLock = new(1, 1);
    // private readonly List<string> _quotes = [];
    // private bool _quotesLoaded;
    private bool _isSyncing;
    private SyncIssueInfo? _lastSyncIssue;
    private string _currentProjectName = string.Empty;

    public DailyViewModel(PlannerRepository repo, AuthenticationService authService, ExternalIdAuthService externalIdAuthService, DatasyncSyncService syncService, PlannerDbContext dbContext, LocalSchemaDbContext schemaDbContext, IConnectivity connectivity)
    {
        _repo = repo;
        _authService = authService;
        _externalIdAuthService = externalIdAuthService;
        _syncService = syncService;
        _dbContext = dbContext;
        _schemaDbContext = schemaDbContext;
        _connectivity = connectivity;

        CurrentDate = DateTime.Today;
        _ = LoadDay(CurrentDate);

        // Subscribe to events
        _connectivity.ConnectivityChanged += OnConnectivityChanged;
        _authService.AuthenticationStateChanged += OnAuthenticationStateChanged;
        _externalIdAuthService.AuthenticationStateChanged += OnAuthenticationStateChanged;
        _syncService.SyncStarted += OnSyncStarted;
        _syncService.SyncCompleted += OnSyncCompleted;
        _syncService.SyncIssueDetected += OnSyncIssueDetected;

        // Initial update
        _ = UpdateStatus();

        if (IsAuthenticated)
        {
            _ = RestoreAuthenticatedSessionAsync();
        }
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        _ = UpdateStatus();
    }

    private void OnAuthenticationStateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(IsAuthenticated));
        UserAvatarSource = BuildUserAvatarSource();

        // Clear all data when signing out
        if (!IsAuthenticated)
        {
            CurrentDay.Tasks.Clear();
            CurrentDay.Notes.Clear();
            Console.WriteLine("Cleared UI data on sign-out");
        }

        _ = UpdateStatus();
    }

    private void OnSyncStarted(object? sender, EventArgs e)
    {
        _isSyncing = true;
        _ = UpdateStatus();
    }

    private async void OnSyncCompleted(object? sender, EventArgs e)
    {
        _isSyncing = false;
        await RefreshCurrentPageAfterSyncAsync();
        await UpdateStatus();
    }

    private void OnSyncIssueDetected(object? sender, SyncIssueInfo issue)
    {
        _lastSyncIssue = issue;
        _ = UpdateStatus();
    }

    private string _loginStatusText = "Sign in with Microsoft";
    public string LoginStatusText
    {
        get => _loginStatusText;
        set { _loginStatusText = value; OnPropertyChanged(); }
    }

    private string _syncStatusText = "No connectivity";
    public string SyncStatusText
    {
        get => _syncStatusText;
        set { _syncStatusText = value; OnPropertyChanged(); }
    }

    private bool _isOnline = false;
    public bool IsOnline
    {
        get => _isOnline;
        set { _isOnline = value; OnPropertyChanged(); }
    }

    private bool _isSyncClickable = false;
    public bool IsSyncClickable
    {
        get => _isSyncClickable;
        set { _isSyncClickable = value; OnPropertyChanged(); }
    }

    public bool IsAuthenticated
    {
        get => _authService.IsAuthenticated || _externalIdAuthService.IsAuthenticated;
    }

    private ImageSource? _userAvatarSource;
    private bool _isUserAvatarPhoto;
    public ImageSource UserAvatarSource
    {
        get
        {
            if (_userAvatarSource is null)
            {
                _userAvatarSource = BuildUserAvatarSource();
            }

            return _userAvatarSource;
        }
        private set
        {
            _userAvatarSource = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsUserAvatarPhoto));
            OnPropertyChanged(nameof(IsUserAvatarFallback));
        }
    }

    public bool IsUserAvatarPhoto => _isUserAvatarPhoto;

    public bool IsUserAvatarFallback => !_isUserAvatarPhoto;

    private ImageSource BuildUserAvatarSource()
    {
        if (_externalIdAuthService.IsAuthenticated)
        {
            _isUserAvatarPhoto = false;
            return "apple_512.png";
        }

        var path = _authService.ProfilePicturePath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            _isUserAvatarPhoto = true;
            return ImageSource.FromFile(path);
        }

        _isUserAvatarPhoto = false;
        return "microsoft.png";
    }

    private async Task UpdateStatus()
    {
        // Update online status
        IsOnline = _connectivity.NetworkAccess == NetworkAccess.Internet;

        // Update sync clickable state (only clickable when authenticated AND online)
        IsSyncClickable = IsAuthenticated && IsOnline;

        // Update login status
        if (_authService.IsAuthenticated)
        {
            LoginStatusText = _authService.UserEmail ?? "Sign out";
        }
        else if (_externalIdAuthService.IsAuthenticated)
        {
            var displayName = _externalIdAuthService.UserEmail
                ?? _externalIdAuthService.UserName
                ?? $"Signed in with {_externalIdAuthService.Provider}";
            LoginStatusText = displayName;
        }
        else
        {
            LoginStatusText = "Sign in with Microsoft";
        }

        if (!IsAuthenticated)
        {
            _lastSyncIssue = null;
            SyncStatusText = "Not signed in";
            return;
        }

        // Sync runs in the background; status should reflect connectivity and pending work,
        // not a potentially long-running transient operation.
        if (_connectivity.NetworkAccess != NetworkAccess.Internet)
        {
            var pendingCount = await _syncService.GetUnsyncedChangesCountAsync();
            if (pendingCount > 0)
            {
                SyncStatusText = BuildPendingSyncText(pendingCount);
            }
            else
            {
                _lastSyncIssue = null;
                SyncStatusText = "No connectivity";
            }
        }
        else
        {
            var pendingCount = await _syncService.GetUnsyncedChangesCountAsync();
            if (pendingCount > 0)
            {
                SyncStatusText = BuildPendingSyncText(pendingCount);
            }
            else
            {
                _lastSyncIssue = null;
                SyncStatusText = "Up to date";
            }
        }
    }

    private string BuildPendingSyncText(int pendingCount)
    {
        string baseText = pendingCount == 1
            ? "1 pending change"
            : $"{pendingCount} pending changes";

        SyncIssueInfo? issue = _syncService.LatestSyncIssue ?? _lastSyncIssue;
        if (issue == null)
        {
            return baseText;
        }

        string label = issue.IsConflict ? "conflict" : "failed";
        if (!string.IsNullOrWhiteSpace(issue.EntityTitle))
        {
            return $"{baseText} ({label}: {issue.EntityTitle})";
        }

        if (!string.IsNullOrWhiteSpace(issue.EntityKey))
        {
            return $"{baseText} ({label}: {issue.EntityKey})";
        }

        if (!string.IsNullOrWhiteSpace(issue.EntityId))
        {
            return $"{baseText} ({label}: {issue.EntityId})";
        }

        return baseText;
    }

    private async Task RestoreAuthenticatedSessionAsync()
    {
        try
        {
            _syncService.Start();
            await UpdateStatus();

            _ = _syncService.TrySyncNowAsync();
            await LoadPageAsync(CurrentDay?.Key ?? KeyConvention.ToDateKey(CurrentDate));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error restoring authenticated session: {ex.Message}");
        }
        finally
        {
            await UpdateStatus();
        }
    }

    private async Task InitializeSignedInUserAsync()
    {
        // Step 1: Recreate database to match current entities
        Console.WriteLine("Initializing database for new user");
        bool dbRecreated = await _dbContext.RecreateAndInitializeDatabaseAsync();
        if (!dbRecreated)
        {
            Console.WriteLine("Warning: Database recreation failed, but proceeding with sync");
        }

        // Step 2: Ensure schema tracking database is initialized with migrations
        Console.WriteLine("Applying schema migrations for new user");
        await _schemaDbContext.Database.EnsureCreatedAsync();
        LocalMigrationRunner.ApplyMigrations(_schemaDbContext);

        // Step 3: Reinitialize Datasync client with new user's JWT
        Console.WriteLine("Initializing Datasync client for new user");
        bool clientInitialized = await _dbContext.ReinitializeDatasyncClientAsync();
        if (!clientInitialized)
        {
            Console.WriteLine("Warning: Datasync client reinitialization failed");
        }

        // Step 4: Pull fresh data from server
        await RestoreAuthenticatedSessionAsync();
    }

    async Task RefreshCurrentPageAfterSyncAsync()
    {
        if (!await _refreshAfterSyncLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            string currentKey = CurrentDay?.Key ?? KeyConvention.ToDateKey(CurrentDate);
            DailyData refreshed = await _repo.LoadPageAsync(currentKey);

            if (CurrentDay == null)
            {
                CurrentDay = refreshed;
                return;
            }

            if (!string.Equals(CurrentDay.Key, currentKey, StringComparison.Ordinal))
            {
                return;
            }

            MergeTaskCollection(CurrentDay.Tasks, refreshed.Tasks);
            MergeNoteCollection(CurrentDay.Notes, refreshed.Notes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to refresh current page after sync: {ex.Message}");
        }
        finally
        {
            _refreshAfterSyncLock.Release();
        }
    }

    static void MergeTaskCollection(ObservableCollection<TaskItem> target, IEnumerable<TaskItem> source)
    {
        List<TaskItem> sourceList = source.ToList();
        if (AreTaskRowsEquivalent(target, sourceList))
        {
            return;
        }

        Dictionary<string, TaskItem> existingById = target
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToDictionary(item => item.Id, StringComparer.Ordinal);

        List<TaskItem> desiredOrder = new();
        foreach (TaskItem incoming in sourceList)
        {
            if (!string.IsNullOrWhiteSpace(incoming.Id)
                && existingById.TryGetValue(incoming.Id, out TaskItem? existing))
            {
                existing.UpdatedAt = incoming.UpdatedAt;
                existing.Version = incoming.Version;
                existing.Deleted = incoming.Deleted;
                existing.Key = incoming.Key;
                existing.Status = incoming.Status;
                existing.Priority = incoming.Priority;
                existing.Order = incoming.Order;
                existing.Title = incoming.Title;
                existing.ForwardedFromDate = incoming.ForwardedFromDate;
                existing.ParentTaskDate = incoming.ParentTaskDate;
                existing.ParentTaskId = incoming.ParentTaskId;
                existing.OriginalTaskId = incoming.OriginalTaskId;
                desiredOrder.Add(existing);
                continue;
            }

            desiredOrder.Add(incoming);
        }

        ApplyDesiredOrder(target, desiredOrder);
    }

    static void MergeNoteCollection(ObservableCollection<NoteItem> target, IEnumerable<NoteItem> source)
    {
        List<NoteItem> sourceList = source.ToList();
        if (AreNoteRowsEquivalent(target, sourceList))
        {
            return;
        }

        Dictionary<string, NoteItem> existingById = target
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToDictionary(item => item.Id, StringComparer.Ordinal);

        List<NoteItem> desiredOrder = new();
        foreach (NoteItem incoming in sourceList)
        {
            if (!string.IsNullOrWhiteSpace(incoming.Id)
                && existingById.TryGetValue(incoming.Id, out NoteItem? existing))
            {
                existing.UpdatedAt = incoming.UpdatedAt;
                existing.Version = incoming.Version;
                existing.Deleted = incoming.Deleted;
                existing.Key = incoming.Key;
                existing.Text = incoming.Text;
                existing.Order = incoming.Order;
                desiredOrder.Add(existing);
                continue;
            }

            desiredOrder.Add(incoming);
        }

        ApplyDesiredOrder(target, desiredOrder);
    }

    static bool AreTaskRowsEquivalent(IList<TaskItem> current, IList<TaskItem> incoming)
    {
        if (current.Count != incoming.Count)
        {
            return false;
        }

        for (int i = 0; i < current.Count; i++)
        {
            TaskItem left = current[i];
            TaskItem right = incoming[i];

            if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                || left.UpdatedAt != right.UpdatedAt
                || !string.Equals(left.Version, right.Version, StringComparison.Ordinal)
                || left.Deleted != right.Deleted
                || !string.Equals(left.Key, right.Key, StringComparison.Ordinal)
                || !string.Equals(left.Status, right.Status, StringComparison.Ordinal)
                || !string.Equals(left.Priority, right.Priority, StringComparison.Ordinal)
                || left.Order != right.Order
                || !string.Equals(left.Title, right.Title, StringComparison.Ordinal)
                || !string.Equals(left.ForwardedFromDate, right.ForwardedFromDate, StringComparison.Ordinal)
                || !string.Equals(left.ParentTaskDate, right.ParentTaskDate, StringComparison.Ordinal)
                || !string.Equals(left.ParentTaskId, right.ParentTaskId, StringComparison.Ordinal)
                || !string.Equals(left.OriginalTaskId, right.OriginalTaskId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    static bool AreNoteRowsEquivalent(IList<NoteItem> current, IList<NoteItem> incoming)
    {
        if (current.Count != incoming.Count)
        {
            return false;
        }

        for (int i = 0; i < current.Count; i++)
        {
            NoteItem left = current[i];
            NoteItem right = incoming[i];

            if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                || left.UpdatedAt != right.UpdatedAt
                || !string.Equals(left.Version, right.Version, StringComparison.Ordinal)
                || left.Deleted != right.Deleted
                || !string.Equals(left.Key, right.Key, StringComparison.Ordinal)
                || !string.Equals(left.Text, right.Text, StringComparison.Ordinal)
                || left.Order != right.Order)
            {
                return false;
            }
        }

        return true;
    }

    static void ApplyDesiredOrder<T>(ObservableCollection<T> target, List<T> desiredOrder)
    {
        for (int i = 0; i < desiredOrder.Count; i++)
        {
            T desiredItem = desiredOrder[i];
            if (i >= target.Count)
            {
                target.Add(desiredItem);
                continue;
            }

            if (ReferenceEquals(target[i], desiredItem))
            {
                continue;
            }

            int existingIndex = target.IndexOf(desiredItem);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, i);
            }
            else
            {
                target.Insert(i, desiredItem);
            }
        }

        while (target.Count > desiredOrder.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    DailyData _currentDay = new() { Key = KeyConvention.ToDateKey(DateTime.Today), Date = DateTime.Today };
    public DailyData CurrentDay
    {
        get => _currentDay;
        set
        {
            _currentDay = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsProjectPage));
            OnPropertyChanged(nameof(HeaderPrimaryText));
            OnPropertyChanged(nameof(HeaderSecondaryText));
            OnPropertyChanged(nameof(HeaderTertiaryText));
            OnPropertyChanged(nameof(ShowDateHeaderDetails));
            OnPropertyChanged(nameof(ShowDailyQuote));
        }
    }

    public bool IsProjectPage => KeyConvention.IsProjectKey(CurrentDay?.Key);

    public string HeaderPrimaryText
    {
        get
        {
            if (CurrentDay == null)
            {
                return string.Empty;
            }

            if (IsProjectPage)
            {
                return string.IsNullOrWhiteSpace(_currentProjectName) ? "Project" : _currentProjectName;
            }

            return CurrentDate.ToString("dd");
        }
    }

    public string HeaderSecondaryText => IsProjectPage ? string.Empty : CurrentDate.ToString("dddd");

    public string HeaderTertiaryText => IsProjectPage ? string.Empty : CurrentDate.ToString("MMMM yyyy");

    public bool ShowDateHeaderDetails => !IsProjectPage;

    private string _dailyQuote = string.Empty;
    public string DailyQuote
    {
        get => _dailyQuote;
        private set
        {
            if (string.Equals(_dailyQuote, value, StringComparison.Ordinal))
            {
                return;
            }

            _dailyQuote = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowDailyQuote));
        }
    }

    public bool ShowDailyQuote => ShowDateHeaderDetails && !string.IsNullOrWhiteSpace(DailyQuote);

    DateTime _currentDate;
    public DateTime CurrentDate
    {
        get => _currentDate;
        private set
        {
            DateTime normalized = value.Date;
            if (_currentDate == normalized)
            {
                return;
            }

            _currentDate = normalized;
            OnPropertyChanged();
        }
    }

    int _subPage = 0; // 0 = tasks, 1 = notes
    public int SubPage
    {
        get => _subPage;
        set { _subPage = value; OnPropertyChanged(); }
    }

    public async Task LoadDay(DateTime key)
    {
        await LoadPageAsync(KeyConvention.ToDateKey(key));
    }

    public async Task LoadPageAsync(string key)
    {
        if (KeyConvention.TryParseDateKey(key, out DateTime date))
        {
            CurrentDate = date;
        }

        _currentProjectName = await _repo.GetProjectNameByKeyAsync(key) ?? string.Empty;

        DailyData day = await _repo.LoadPageAsync(key);
        // day.Tasks.Add(new TaskItem { Status = "I", Priority = "A", Order = 1, Title = "The most important thing" });
        // day.Tasks.Add(new TaskItem { Status = "C", Priority = "A", Order = 2, Title = "Also important" });
        // day.Tasks.Add(new TaskItem { Status = "I", Priority = "A", Order = 1, Title = "The most important thing" });
        // day.Notes.Add(new NoteItem { Text = "I like this!"});
        // CurrentDay = day;
        // await UpdateDailyQuoteAsync(key);
        // CurrentDay = new DailyData
        // {
        //     Date = date,
        //     Tasks = new List<TaskItem>
        //     {
        //         new TaskItem { Status = "I", Priority = "A", Order = 1, Title = "The most important thing" },
        //         new TaskItem { Status = "C", Priority = "A", Order = 2, Title = "Also important" },
        //         new TaskItem { Status = " ", Priority = "B", Order = 1, Title = "Nice to have" },
        //         new TaskItem { Status = " ", Priority = "C", Order = 1, Title = "No big deal" }
        //     },
        //     Notes = new List<NoteItem>
        //     {
        //         new NoteItem{ Note = "I just thought of something." },
        //         new NoteItem{ Note = "Today is " + date.ToString("yyyy-MM-dd") + "." },
        //         new NoteItem{ Note = "I like turtles!" }
        //     }
        // };
    }

    // private async Task UpdateDailyQuoteAsync(string key)
    // {
    //     if (!KeyConvention.TryParseDateKey(key, out DateTime date))
    //     {
    //         DailyQuote = string.Empty;
    //         return;
    //     }

    //     await EnsureQuotesLoadedAsync();
    //     DailyQuote = FormatQuoteForDisplay(GetQuoteForDate(date));
    // }

    // private async Task EnsureQuotesLoadedAsync()
    // {
    //     if (_quotesLoaded)
    //     {
    //         return;
    //     }

    //     try
    //     {
    //         using Stream stream = await FileSystem.OpenAppPackageFileAsync(QuotesAssetPath);
    //         using StreamReader reader = new(stream);

    //         while (true)
    //         {
    //             string? line = await reader.ReadLineAsync();
    //             if (line is null)
    //             {
    //                 break;
    //             }

    //             if (string.IsNullOrWhiteSpace(line))
    //             {
    //                 continue;
    //             }

    //             string trimmed = line.Trim().Trim('\uFEFF');
    //             if (string.Equals(trimmed, "\"quote\"", StringComparison.OrdinalIgnoreCase)
    //                 || string.Equals(trimmed, "quote", StringComparison.OrdinalIgnoreCase))
    //             {
    //                 continue;
    //             }

    //             string quote = ParseCsvQuoteValue(trimmed);
    //             if (!string.IsNullOrWhiteSpace(quote))
    //             {
    //                 _quotes.Add(quote);
    //             }
    //         }
    //     }
    //     catch (Exception ex)
    //     {
    //         Console.WriteLine($"Failed to load quotes asset: {ex.Message}");
    //     }

    //     if (_quotes.Count == 0)
    //     {
    //         _quotes.Add("Well done is better than well said.");
    //     }

    //     _quotesLoaded = true;
    // }

    // private static string ParseCsvQuoteValue(string value)
    // {
    //     string text = value.Trim();
    //     if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
    //     {
    //         text = text[1..^1];
    //     }

    //     return text.Replace("\"\"", "\"").Trim();
    // }

    // private static string FormatQuoteForDisplay(string? text)
    // {
    //     if (string.IsNullOrWhiteSpace(text))
    //     {
    //         return string.Empty;
    //     }

    //     string trimmed = text.Trim();

    //     // Normalize common outer quote characters so display always uses smart quotes.
    //     if (trimmed.Length >= 2)
    //     {
    //         char first = trimmed[0];
    //         char last = trimmed[^1];
    //         bool hasOuterQuotes =
    //             (first == '"' && last == '"') ||
    //             (first == '\'' && last == '\'') ||
    //             (first == '“' && last == '”') ||
    //             (first == '‘' && last == '’');

    //         if (hasOuterQuotes)
    //         {
    //             trimmed = trimmed[1..^1].Trim();
    //         }
    //     }

    //     return $"“{trimmed}”";
    // }

    // private string GetQuoteForDate(DateTime date)
    // {
    //     string? holidayQuote = GetHolidayQuote(date);
    //     if (!string.IsNullOrWhiteSpace(holidayQuote))
    //     {
    //         return holidayQuote;
    //     }

    //     HashSet<string> reservedHolidayQuotes = GetReservedHolidayQuotes(date.Year);
    //     List<string> regularQuotes = _quotes
    //         .Where(q => !reservedHolidayQuotes.Contains(q))
    //         .ToList();

    //     if (regularQuotes.Count == 0)
    //     {
    //         int fallbackIndex = Math.Max(0, date.DayOfYear - 1) % _quotes.Count;
    //         return _quotes[fallbackIndex];
    //     }

    //     int nonHolidayOrdinal = GetNonHolidayOrdinalInYear(date);
    //     int index = nonHolidayOrdinal % regularQuotes.Count;
    //     return regularQuotes[index];
    // }

    // private HashSet<string> GetReservedHolidayQuotes(int year)
    // {
    //     HashSet<string> reserved = new(StringComparer.Ordinal);

    //     DateTime[] holidayDates =
    //     [
    //         new DateTime(year, 1, 1),
    //         new DateTime(year, 7, 4),
    //         new DateTime(year, 12, 25),
    //         GetThanksgivingDate(year)
    //     ];

    //     foreach (DateTime holidayDate in holidayDates)
    //     {
    //         string? holidayQuote = GetHolidayQuote(holidayDate);
    //         if (!string.IsNullOrWhiteSpace(holidayQuote))
    //         {
    //             reserved.Add(holidayQuote);
    //         }
    //     }

    //     return reserved;
    // }

    // private int GetNonHolidayOrdinalInYear(DateTime date)
    // {
    //     DateTime cursor = new(date.Year, 1, 1);
    //     int ordinal = -1;

    //     while (cursor <= date)
    //     {
    //         if (GetHolidayQuote(cursor) is null)
    //         {
    //             ordinal++;
    //         }

    //         cursor = cursor.AddDays(1);
    //     }

    //     return Math.Max(0, ordinal);
    // }

    // private string? GetHolidayQuote(DateTime date)
    // {
    //     if (date.Month == 1 && date.Day == 1)
    //     {
    //         return "Be at war with your vices, at peace with your neighbors, and let every new year find you a better man.";
    //     }

    //     if (date.Month == 7 && date.Day == 4)
    //     {
    //         return "Where liberty is, there is my country.";
    //     }

    //     if (date.Month == 12 && date.Day == 25)
    //     {
    //         return "A good conscience is a continual Christmas.";
    //     }

    //     if (IsThanksgiving(date))
    //     {
    //         return "When befriended, remember it: When you befriend, forget it.";
    //     }

    //     return null;
    // }

    // private static bool IsThanksgiving(DateTime date)
    // {
    //     if (date.Month != 11 || date.DayOfWeek != DayOfWeek.Thursday)
    //     {
    //         return false;
    //     }

    //     int thursdayCount = (date.Day - 1) / 7 + 1;
    //     return thursdayCount == 4;
    // }

    // private static DateTime GetThanksgivingDate(int year)
    // {
    //     DateTime date = new(year, 11, 1);
    //     while (date.DayOfWeek != DayOfWeek.Thursday)
    //     {
    //         date = date.AddDays(1);
    //     }

    //     return date.AddDays(21);
    // }

    public async Task AddTaskAsync(string text)
    {
        text = NormalizeTaskTitle(text);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var task = new TaskItem
        {
            Key = CurrentDay.Key,
            // Status = StatusEnum.NotStarted,
            Status = "NotStarted",
            Priority = "A",
            Order = GetSuggestedTaskOrder(CurrentDay.Key, "A"),
            Title = text
        };

        await _repo.AddTaskAsync(task);
        CurrentDay.Tasks.Add(task);
        SortTasksInMemory();
        await UpdateStatus();
    }

    public async Task AddTaskItemAsync(TaskItem task)
    {
        if (string.IsNullOrWhiteSpace(task.Title))
        {
            return;
        }

        task.Key = CurrentDay.Key;
        task.Priority = string.IsNullOrWhiteSpace(task.Priority) ? "A" : task.Priority;
        if (task.Order <= 0)
        {
            task.Order = GetSuggestedTaskOrder(task.Key, task.Priority, task);
        }

        await _repo.AddTaskAsync(task);
        CurrentDay.Tasks.Add(task);
        await ApplyTaskPlacementAsync(task, task.Priority, task.Order);
        await UpdateStatus();
    }

    public async Task SaveTaskDetailsLocallyAsync(
        TaskItem task,
        string title,
        string status,
        string priority,
        int order,
        bool isNewTask,
        string? forwardDestinationKey = null,
        ForwardedTaskSeed? forwardedTaskSeed = null,
        string? saveTraceId = null)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        string traceId = string.IsNullOrWhiteSpace(saveTraceId) ? "no-trace" : saveTraceId;

        if (task == null)
        {
            LogTaskSaveTiming(traceId, "vm.preclose.skipped", totalStopwatch.ElapsedMilliseconds, "reason=null-task");
            return;
        }

        string normalizedTitle = NormalizeTaskTitle(title);
        if (string.IsNullOrEmpty(normalizedTitle))
        {
            LogTaskSaveTiming(traceId, "vm.preclose.skipped", totalStopwatch.ElapsedMilliseconds, "reason=empty-title");
            return;
        }

        task.Key = CurrentDay?.Key ?? task.Key;
        task.Title = normalizedTitle;
        task.Status = string.IsNullOrWhiteSpace(status) ? "NotStarted" : status;
        task.Priority = string.IsNullOrWhiteSpace(priority) ? "A" : priority;
        task.Order = Math.Max(1, order);

        bool shouldForward = string.Equals(task.Status, "Forwarded", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(forwardDestinationKey);

        Console.WriteLine($"TaskSaveTiming[{traceId}] vm.preclose.begin isNewTask={isNewTask} status={task.Status} shouldForward={shouldForward}");

        if (isNewTask)
        {
            Stopwatch addStopwatch = Stopwatch.StartNew();
            await _repo.AddTaskAsync(task, triggerSync: false);
            LogTaskSaveTiming(traceId, "vm.preclose.add-task", addStopwatch.ElapsedMilliseconds);
        }
        else if (CurrentDay?.Tasks != null && CurrentDay.Tasks.IndexOf(task) >= 0)
        {
            Stopwatch placementStopwatch = Stopwatch.StartNew();
            await ApplyTaskPlacementAsync(task, task.Priority, task.Order, triggerSync: false, saveTraceId: traceId);
            LogTaskSaveTiming(traceId, "vm.preclose.apply-placement", placementStopwatch.ElapsedMilliseconds);
        }
        else
        {
            Stopwatch updateStopwatch = Stopwatch.StartNew();
            await _repo.UpdateTaskAsync(task, triggerSync: false);
            LogTaskSaveTiming(traceId, "vm.preclose.update-task", updateStopwatch.ElapsedMilliseconds);
        }

        if (shouldForward)
        {
            Stopwatch forwardStopwatch = Stopwatch.StartNew();
            await _repo.ForwardTaskAsync(
                task,
                forwardDestinationKey!,
                triggerSync: false,
                forwardedTaskSeed: forwardedTaskSeed);
            LogTaskSaveTiming(traceId, "vm.preclose.forward", forwardStopwatch.ElapsedMilliseconds);
        }

        LogTaskSaveTiming(traceId, "vm.preclose.total", totalStopwatch.ElapsedMilliseconds);
    }

    public void SuppressSyncForLocalSave(TimeSpan duration)
    {
        _syncService.SuppressSyncFor(duration);
    }

    public async Task CompleteTaskSaveAfterCloseAsync(TaskItem task, string priority, int order, bool isNewTask)
    {
        bool shouldRunPostSaveFlow = false;
        string pageKey = CurrentDay?.Key ?? KeyConvention.ToDateKey(CurrentDate);

        try
        {
            if (CurrentDay?.Tasks == null)
            {
                return;
            }

            if (isNewTask && CurrentDay.Tasks.IndexOf(task) < 0)
            {
                CurrentDay.Tasks.Add(task);
            }

            task.Priority = string.IsNullOrWhiteSpace(priority) ? "A" : priority;
            task.Order = Math.Max(1, order);
            SortTasksInMemory();

            await UpdateStatus();
            shouldRunPostSaveFlow = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Task post-close save completion failed: {ex.Message}");
        }

        if (shouldRunPostSaveFlow)
        {
            await RunPostLocalSaveFlowAsync(pageKey, reloadCurrentPage: false);
        }
    }

    static void LogTaskSaveTiming(string traceId, string step, long elapsedMilliseconds, string? details = null)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            Console.WriteLine($"TaskSaveTiming[{traceId}] {step} {elapsedMilliseconds}ms");
            return;
        }

        Console.WriteLine($"TaskSaveTiming[{traceId}] {step} {elapsedMilliseconds}ms {details}");
    }

    public Task<string?> GetTaskKeyByIdAsync(string taskId)
    {
        return _repo.GetTaskKeyByIdAsync(taskId);
    }

    static string NormalizeTaskTitle(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text
            .Replace("\u00A0", " ")
            .Replace("\u200B", " ")
            .Replace("\uFEFF", " ")
            .Trim();
    }

    public async Task UpdateTaskAsync(TaskItem task)
    {
        await _repo.UpdateTaskAsync(task);
        SortTasksInMemory();
        await UpdateStatus();
    }

    public async Task UpdateTaskFromDetailsAsync(TaskItem task, string title, string status, string priority, int order)
    {
        if (task == null)
        {
            return;
        }

        string normalizedTitle = NormalizeTaskTitle(title);
        if (string.IsNullOrEmpty(normalizedTitle))
        {
            return;
        }

        string requestedStatus = string.IsNullOrWhiteSpace(status) ? "NotStarted" : status;
        string requestedPriority = string.IsNullOrWhiteSpace(priority) ? "A" : priority;
        int requestedOrder = Math.Max(1, order);

        task.Title = normalizedTitle;
        task.Status = requestedStatus;

        if (CurrentDay?.Tasks == null || CurrentDay.Tasks.Count == 0 || CurrentDay.Tasks.IndexOf(task) < 0)
        {
            await SaveTaskDirectAsync(task, requestedPriority, requestedOrder);
        }
        else
        {
            await ApplyTaskPlacementAsync(task, requestedPriority, requestedOrder);
        }

        await UpdateStatus();
    }

    public async Task SaveNoteDetailsLocallyAsync(NoteItem note, string text, bool isNewNote)
    {
        if (note == null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        note.Key = CurrentDay?.Key ?? note.Key;
        note.Text = text;

        if (isNewNote)
        {
            note.Order = note.Order > 0 ? note.Order : GetNextNoteOrder();
            await _repo.AddNoteAsync(note, triggerSync: false);
            return;
        }

        await _repo.UpdateNoteAsync(note, triggerSync: false);
    }

    public async Task CompleteNoteSaveAfterCloseAsync(NoteItem note, bool isNewNote)
    {
        bool shouldRunPostSaveFlow = false;
        string pageKey = CurrentDay?.Key ?? KeyConvention.ToDateKey(CurrentDate);

        try
        {
            if (CurrentDay?.Notes != null && isNewNote && CurrentDay.Notes.IndexOf(note) < 0)
            {
                CurrentDay.Notes.Add(note);
            }

            await UpdateStatus();
            shouldRunPostSaveFlow = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Note post-close save completion failed: {ex.Message}");
        }

        if (shouldRunPostSaveFlow)
        {
            await RunPostLocalSaveFlowAsync(pageKey, reloadCurrentPage: false);
        }
    }

    public Task<List<ProjectItem>> GetProjectsAsync()
    {
        return _repo.GetProjectsAsync();
    }

    public Task<List<NoteSearchResult>> SearchNotesAsync(
        string searchText,
        CancellationToken cancellationToken = default,
        int maxResults = 100)
    {
        return _repo.SearchNotesAsync(searchText, cancellationToken, maxResults);
    }

    public Task<string> GetPageDisplayAsync(string? key)
    {
        return _repo.GetPageDisplayAsync(key);
    }

    public Task<(int Count, List<string> SqlStatements)> BuildCatchUpForwardSqlPreviewAsync(string destinationDateKey)
    {
        return _repo.BuildCatchUpForwardSqlPreviewAsync(destinationDateKey);
    }

    public async Task<(int CandidateCount, int ExecutedStatements)> ExecuteCatchUpAsync(string destinationDateKey)
    {
        var result = await _repo.ExecuteCatchUpForwardSqlAsync(destinationDateKey);

        await RunPostLocalSaveFlowAsync(destinationDateKey, reloadCurrentPage: true);

        return result;
    }

    async Task RunPostLocalSaveFlowAsync(string pageKey, bool reloadCurrentPage)
    {
        string resolvedKey = string.IsNullOrWhiteSpace(pageKey)
            ? (CurrentDay?.Key ?? KeyConvention.ToDateKey(CurrentDate))
            : pageKey;

        bool isCurrentPage = string.Equals(CurrentDay?.Key, resolvedKey, StringComparison.Ordinal);
        if (reloadCurrentPage || !isCurrentPage)
        {
            await LoadPageAsync(resolvedKey);
        }

        _repo.TriggerSync();
        await UpdateStatus();
    }

    public async Task<(bool Success, string ErrorMessage, ProjectItem? Project)> TryCreateProjectAsync(string projectName)
    {
        string displayName = KeyConvention.NormalizeProjectDisplayName(projectName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return (false, "Please enter a project name.", null);
        }

        if (displayName.Length > MaxProjectNameLength)
        {
            return (false, $"Project name must be {MaxProjectNameLength} characters or fewer.", null);
        }

        string normalizedName = KeyConvention.NormalizeProjectName(displayName);
        if (await _repo.ProjectExistsAsync(normalizedName))
        {
            return (false, "A project with that name already exists.", null);
        }

        var project = new ProjectItem
        {
            Name = displayName,
            NormalizedName = normalizedName
        };

        try
        {
            await _repo.AddProjectAsync(project);
            return (true, string.Empty, project);
        }
        catch (DbUpdateException)
        {
            return (false, "A project with that name already exists.", null);
        }
    }

    public async Task<(bool Success, string ErrorMessage)> TryRenameProjectAsync(ProjectItem project, string newProjectName)
    {
        if (project == null)
        {
            return (false, "Please select a project to edit.");
        }

        string displayName = KeyConvention.NormalizeProjectDisplayName(newProjectName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return (false, "Please enter a project name.");
        }

        if (displayName.Length > MaxProjectNameLength)
        {
            return (false, $"Project name must be {MaxProjectNameLength} characters or fewer.");
        }

        string normalizedName = KeyConvention.NormalizeProjectName(displayName);
        if (await _repo.ProjectExistsAsync(normalizedName, project.Id))
        {
            return (false, "A project with that name already exists.");
        }

        project.Name = displayName;
        project.NormalizedName = normalizedName;

        try
        {
            await _repo.UpdateProjectAsync(project);

            if (CurrentDay != null
                && KeyConvention.TryGetProjectId(CurrentDay.Key, out string currentProjectId)
                && string.Equals(currentProjectId, project.Id, StringComparison.Ordinal))
            {
                _currentProjectName = displayName;
                OnPropertyChanged(nameof(HeaderPrimaryText));
            }

            return (true, string.Empty);
        }
        catch (DbUpdateException)
        {
            return (false, "A project with that name already exists.");
        }
    }

    public Task NavigateToPageAsync(string key)
    {
        return LoadPageAsync(key);
    }

    public async Task ReorderTaskAsync(TaskItem source, TaskItem target)
    {
        if (source == null || target == null)
        {
            return;
        }

        var tasks = CurrentDay?.Tasks;
        if (tasks == null || tasks.Count == 0)
        {
            return;
        }

        int sourceIndex = tasks.IndexOf(source);
        int targetIndex = tasks.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            return;
        }

        // Unplanned tasks are not valid drop targets for reorder.
        if (string.IsNullOrWhiteSpace(target.Priority) && target.Order == 0)
        {
            return;
        }

        int requestedOrder = sourceIndex < targetIndex ? target.Order + 1 : target.Order;
        string requestedPriority = string.IsNullOrWhiteSpace(target.Priority) ? "A" : target.Priority;
        await ApplyTaskPlacementAsync(source, requestedPriority, requestedOrder);
        await UpdateStatus();
    }

    async Task ApplyTaskPlacementAsync(
        TaskItem task,
        string priority,
        int order,
        bool triggerSync = true,
        string? saveTraceId = null)
    {
        if (task == null || CurrentDay?.Tasks == null)
        {
            return;
        }

        bool shouldLog = !string.IsNullOrWhiteSpace(saveTraceId);
        string traceId = shouldLog ? saveTraceId! : "no-trace";
        Stopwatch totalStopwatch = shouldLog ? Stopwatch.StartNew() : null!;

        string requestedPriority = string.IsNullOrWhiteSpace(priority) ? "A" : priority;
        int requestedOrder = Math.Max(1, order);

        Stopwatch placementStopwatch = shouldLog ? Stopwatch.StartNew() : null!;
        PlaceTaskInMemory(task, requestedPriority, requestedOrder);
        if (shouldLog)
        {
            LogTaskSaveTiming(traceId, "vm.preclose.apply-placement.place-in-memory", placementStopwatch.ElapsedMilliseconds);
        }

        Stopwatch reorderStopwatch = shouldLog ? Stopwatch.StartNew() : null!;
        int changedTaskCount = await UpdateTaskOrderAsync(new[] { task }, triggerSync, saveTraceId);
        if (shouldLog)
        {
            LogTaskSaveTiming(traceId, "vm.preclose.apply-placement.persist-order", reorderStopwatch.ElapsedMilliseconds, $"changedTasks={changedTaskCount}");
        }

        Stopwatch sortStopwatch = shouldLog ? Stopwatch.StartNew() : null!;
        SortTasksInMemory();
        if (shouldLog)
        {
            LogTaskSaveTiming(traceId, "vm.preclose.apply-placement.sort-in-memory", sortStopwatch.ElapsedMilliseconds);
            LogTaskSaveTiming(traceId, "vm.preclose.apply-placement.total", totalStopwatch.ElapsedMilliseconds, $"changedTasks={changedTaskCount}");
        }
    }

    async Task SaveTaskDirectAsync(TaskItem task, string priority, int order, bool triggerSync = true)
    {
        task.Priority = string.IsNullOrWhiteSpace(priority) ? "A" : priority;
        task.Order = Math.Max(1, order);
        await _repo.UpdateTaskAsync(task, triggerSync);
        SortTasksInMemory();
    }

    void PlaceTaskInMemory(TaskItem task, string requestedPriority, int requestedOrder)
    {
        task.Priority = requestedPriority;

        List<TaskItem> orderedWithoutTask = CurrentDay.Tasks
            .Where(candidate => !ReferenceEquals(candidate, task))
            .OrderBy(candidate => GetPriorityRank(candidate.Priority))
            .ThenBy(candidate => candidate.Order)
            .ThenBy(candidate => candidate.Id)
            .ToList();

        List<int> targetPriorityIndexes = orderedWithoutTask
            .Select((candidate, index) => (candidate, index))
            .Where(tuple => string.Equals(tuple.candidate.Priority, requestedPriority, StringComparison.Ordinal))
            .Select(tuple => tuple.index)
            .ToList();

        int insertionIndex;
        if (targetPriorityIndexes.Count > 0)
        {
            int insertionPositionInPriority = Math.Min(requestedOrder, targetPriorityIndexes.Count + 1) - 1;
            insertionIndex = insertionPositionInPriority >= targetPriorityIndexes.Count
                ? targetPriorityIndexes[^1] + 1
                : targetPriorityIndexes[insertionPositionInPriority];
        }
        else
        {
            int targetRank = GetPriorityRank(requestedPriority);
            insertionIndex = orderedWithoutTask.FindIndex(candidate => GetPriorityRank(candidate.Priority) > targetRank);
            if (insertionIndex < 0)
            {
                insertionIndex = orderedWithoutTask.Count;
            }
        }

        orderedWithoutTask.Insert(insertionIndex, task);

        ApplyDesiredOrder(CurrentDay.Tasks, orderedWithoutTask);
    }

    public async Task DeleteNoteAsync(TaskItem task)
    {
        await _repo.DeleteTaskAsync(task);
        CurrentDay.Tasks.Remove(task);
        SortTasksInMemory();
        await UpdateStatus();
    }

    public async Task AddNoteAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var note = new NoteItem
        {
            Key = CurrentDay.Key,
            Text = text,
            Order = GetNextNoteOrder()
        };

        await _repo.AddNoteAsync(note);
        CurrentDay.Notes.Add(note);
        await UpdateStatus();
    }

    public async Task UpdateNoteAsync(NoteItem note)
    {
        await _repo.UpdateNoteAsync(note);
        await UpdateStatus();
    }

    public async Task DeleteNoteAsync(NoteItem note)
    {
        await _repo.DeleteNoteAsync(note);
        CurrentDay.Notes.Remove(note);
        await UpdateStatus();
    }

    public async Task GoForwardAsync()
    {
        if (SubPage == 0)
        {
            SubPage = 1;
            return;
        }

        SubPage = 0;
        await NavigatePageAsync(1);
    }

    public async Task GoBackwardAsync()
    {
        if (SubPage == 1)
        {
            SubPage = 0;
            return;
        }

        SubPage = 1;
        await NavigatePageAsync(-1);
    }

    public async Task NavigatePageAsync(int direction)
    {
        if (direction == 0)
        {
            return;
        }

        string currentKey = CurrentDay?.Key ?? KeyConvention.ToDateKey(CurrentDate);
        if (KeyConvention.TryParseDateKey(currentKey, out DateTime currentDate))
        {
            await LoadDay(currentDate.AddDays(direction > 0 ? 1 : -1));
            return;
        }

        if (KeyConvention.IsProjectKey(currentKey))
        {
            List<string> projectKeys = await _repo.GetProjectKeysAsync();
            int currentIndex = projectKeys.FindIndex(key => string.Equals(key, currentKey, StringComparison.OrdinalIgnoreCase));

            if (projectKeys.Count > 0 && currentIndex >= 0)
            {
                int targetIndex = currentIndex + (direction > 0 ? 1 : -1);
                if (targetIndex >= 0 && targetIndex < projectKeys.Count)
                {
                    await LoadPageAsync(projectKeys[targetIndex]);
                    return;
                }

                if (direction > 0)
                {
                    string firstDate = await _repo.GetEarliestNonEmptyDateKeyAsync() ?? KeyConvention.ToDateKey(DateTime.Today);
                    await LoadPageAsync(firstDate);
                    return;
                }

                string latestDate = await _repo.GetLatestNonEmptyDateKeyAsync() ?? KeyConvention.ToDateKey(DateTime.Today);
                await LoadPageAsync(latestDate);
                return;
            }
        }

        await LoadDay(DateTime.Today);
    }

    void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public async Task ToggleAuthenticationAsync()
    {
        if (_authService.IsAuthenticated)
        {
            // Sign out with full cleanup for multi-user support (Microsoft / MSAL flow)
            await _authService.SignOutWithCleanupAsync(_syncService, _dbContext, _schemaDbContext);
            await UpdateStatus();
            return;
        }

        if (_externalIdAuthService.IsAuthenticated)
        {
            // Sign out from External ID (Apple) with full local sync cleanup.
            await _syncService.CancelAndDisposeAsync();

            _dbContext.ChangeTracker.Clear();
            _schemaDbContext.ChangeTracker.Clear();

            var plannerConnection = _dbContext.Database.GetDbConnection();
            if (plannerConnection.State != System.Data.ConnectionState.Closed)
            {
                plannerConnection.Close();
            }

            var schemaConnection = _schemaDbContext.Database.GetDbConnection();
            if (schemaConnection.State != System.Data.ConnectionState.Closed)
            {
                schemaConnection.Close();
            }

            _ = await _dbContext.DeleteDatabaseFileAsync();
            _externalIdAuthService.SignOut();
            await UpdateStatus();
            return;
        }

        // Sign in with Microsoft (existing MSAL flow — unchanged)
        var result = await _authService.SignInAsync();
        if (result != null)
        {
            try
            {
                await InitializeSignedInUserAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during sign-in initialization: {ex.Message}");
            }
        }

        await UpdateStatus();
    }

    /// <summary>
    /// Signs in with Apple via Microsoft Entra External ID using WebAuthenticator.
    /// On success the authenticated user's identity is stored in Preferences and
    /// the UI is refreshed via <see cref="ExternalIdAuthService.AuthenticationStateChanged"/>.
    /// </summary>
    public async Task SignInWithAppleAsync()
    {
        try
        {
            var identity = await _externalIdAuthService.AuthenticateAsync();
            if (identity == null)
            {
                // Null is returned when the user cancels the browser or an error
                // is handled inside ExternalIdAuthService (already logged there).
                Console.WriteLine("[Auth] Sign in with Apple did not complete (cancelled or handled error).");
            }
            else
            {
                await InitializeSignedInUserAsync();
            }
        }
        catch (Exception ex)
        {
            // Unexpected unhandled error (e.g. ArgumentException from misconfiguration)
            Console.WriteLine($"[Auth] Unexpected error during Sign in with Apple: {ex.Message}");
        }
        finally
        {
            await UpdateStatus();
        }
    }

    public async Task ForceSyncAsync()
    {
        if (!IsAuthenticated)
        {
            return;
        }

        if (_isSyncing)
        {
            return;
        }

        // SyncStarted/SyncCompleted events are the source of truth for in-progress state.
        _repo.TriggerSync();
        await UpdateStatus();
    }

    public async Task<bool> TryNavigateToLatestSyncIssueAsync()
    {
        SyncIssueInfo? issue = _syncService.LatestSyncIssue ?? _lastSyncIssue;
        if (issue == null)
        {
            return false;
        }

        string? targetKey = issue.EntityKey;
        if (string.IsNullOrWhiteSpace(targetKey) && !string.IsNullOrWhiteSpace(issue.EntityId))
        {
            string? entityType = issue.EntityType?.Trim().ToLowerInvariant();

            if (entityType == "task")
            {
                targetKey = await _repo.GetTaskKeyByIdAsync(issue.EntityId);
            }
            else if (entityType == "note")
            {
                targetKey = await _repo.GetNoteKeyByIdAsync(issue.EntityId);
            }
            else if (entityType == "project")
            {
                targetKey = await _repo.GetProjectKeyByIdAsync(issue.EntityId);
            }
            else
            {
                targetKey = await _repo.GetTaskKeyByIdAsync(issue.EntityId)
                    ?? await _repo.GetNoteKeyByIdAsync(issue.EntityId)
                    ?? await _repo.GetProjectKeyByIdAsync(issue.EntityId);
            }
        }

        if (string.IsNullOrWhiteSpace(targetKey))
        {
            return false;
        }

        if (string.Equals(CurrentDay?.Key, targetKey, StringComparison.Ordinal))
        {
            return true;
        }

        await LoadPageAsync(targetKey);
        return true;
    }

    int GetNextNoteOrder()
    {
        int order = 1;
        foreach (NoteItem note in CurrentDay.Notes)
        {
            order++;
        }

        return order;
    }

    public (int Min, int Max) GetTaskOrderRange(string key, string priority, TaskItem? excludeTask)
    {
        if (CurrentDay?.Tasks == null)
        {
            return (1, 1);
        }

        string normalizedPriority = string.IsNullOrWhiteSpace(priority) ? "A" : priority;
        int samePriorityCount = CurrentDay.Tasks.Count(task =>
            !ReferenceEquals(task, excludeTask)
            && string.Equals(task.Key, key, StringComparison.Ordinal)
            && string.Equals(task.Priority, normalizedPriority, StringComparison.OrdinalIgnoreCase));

        return (1, Math.Max(1, samePriorityCount + 1));
    }

    public (int Min, int Max) GetTaskOrderRange(string key, string priority)
    {
        return GetTaskOrderRange(key, priority, excludeTask: null);
    }

    public int GetSuggestedTaskOrder(string key, string priority, TaskItem? excludeTask)
    {
        return GetTaskOrderRange(key, priority, excludeTask).Max;
    }

    public int GetSuggestedTaskOrder(string key, string priority)
    {
        return GetSuggestedTaskOrder(key, priority, excludeTask: null);
    }

    async Task<int> UpdateTaskOrderAsync(
        IEnumerable<TaskItem>? additionallyChanged = null,
        bool triggerSync = true,
        string? saveTraceId = null)
    {
        bool shouldLog = !string.IsNullOrWhiteSpace(saveTraceId);
        string traceId = shouldLog ? saveTraceId! : "no-trace";
        Stopwatch totalStopwatch = shouldLog ? Stopwatch.StartNew() : null!;

        List<TaskItem> changedTasks = new();
        HashSet<string> changedIds = new(StringComparer.Ordinal);

        if (additionallyChanged != null)
        {
            foreach (TaskItem task in additionallyChanged)
            {
                if (task == null)
                {
                    continue;
                }

                if (changedIds.Add(task.Id))
                {
                    changedTasks.Add(task);
                }
            }
        }

        var orderByPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (TaskItem task in CurrentDay.Tasks)
        {
            string priorityKey = task.Priority ?? string.Empty;

            if (string.IsNullOrWhiteSpace(priorityKey))
            {
                if (task.Order != 0)
                {
                    task.Order = 0;
                    if (changedIds.Add(task.Id))
                    {
                        changedTasks.Add(task);
                    }
                }

                continue;
            }

            orderByPriority.TryGetValue(priorityKey, out int current);
            int nextOrder = current + 1;
            orderByPriority[priorityKey] = nextOrder;

            if (task.Order != nextOrder)
            {
                task.Order = nextOrder;
                if (changedIds.Add(task.Id))
                {
                    changedTasks.Add(task);
                }
            }
        }

        if (shouldLog)
        {
            LogTaskSaveTiming(traceId, "vm.preclose.apply-placement.update-order.calculate", totalStopwatch.ElapsedMilliseconds, $"changedTasks={changedTasks.Count}");
        }

        Stopwatch persistStopwatch = shouldLog ? Stopwatch.StartNew() : null!;
        await _repo.UpdateTasksAsync(changedTasks, triggerSync, saveTraceId);

        if (shouldLog)
        {
            LogTaskSaveTiming(traceId, "vm.preclose.apply-placement.update-order.repo-update", persistStopwatch.ElapsedMilliseconds, $"changedTasks={changedTasks.Count}");
            LogTaskSaveTiming(traceId, "vm.preclose.apply-placement.update-order.total", totalStopwatch.ElapsedMilliseconds, $"changedTasks={changedTasks.Count}");
        }

        return changedTasks.Count;
    }

    static int GetPriorityRank(string? priority)
    {
        return priority?.ToUpperInvariant() switch
        {
            "A" => 0,
            "B" => 1,
            "C" => 2,
            _ => 3
        };
    }

    void SortTasksInMemory()
    {
        if (CurrentDay?.Tasks == null || CurrentDay.Tasks.Count < 2)
        {
            return;
        }

        var sorted = CurrentDay.Tasks
            .OrderBy(task => GetPriorityRank(task.Priority))
            .ThenBy(task => task.Order)
            .ThenBy(task => task.Id)
            .ToList();

        ApplyDesiredOrder(CurrentDay.Tasks, sorted);
    }
}

// public partial class DailyViewModel(AppDbContext context, IAlertService alertService) : ObservableRecipient
// {
//     [ObservableProperty]
//     public partial bool IsRefreshing { get; set; }

//     [ObservableProperty]
//     public partial ConcurrentObservableCollection<TaskItem> Items { get; set; } = [];

//     [RelayCommand]
//     public async Task RefreshItemsAsync(CancellationToken cancellationToken = default)
//     {
//         if (IsRefreshing)
//         {
//             return;
//         }

//         try
//         {
//             await context.SynchronizeAsync(cancellationToken);
//             List<TaskItem> items = await context.TaskItems.ToListAsync(cancellationToken);
//             Items.ReplaceAll(items);
//         }
//         catch (Exception ex)
//         {
//             await alertService.ShowErrorAlertAsync("RefreshItems", ex.Message);
//         }
//         finally
//         {
//             IsRefreshing = false;
//         }
//     }

//     [RelayCommand]
//     public async Task UpdateItemAsync(string itemId, CancellationToken cancellationToken = default)
//     {
//         try
//         {
//             // TaskItem? item = await context.TaskItems.FindAsync([itemId], cancellationToken);
//             // if (item is not null)
//             // {
//             //     item.Status = !item.Status;
//             //     _ = context.TaskItems.Update(item);
//             //     _ = Items.ReplaceIf(x => x.Id == itemId, item);
//             //     _ = await context.SaveChangesAsync(cancellationToken);
//             // }
//         }
//         catch (Exception ex)
//         {
//             await alertService.ShowErrorAlertAsync("UpdateItem", ex.Message);
//         }
//     }

//     [RelayCommand]
//     public async Task AddItemAsync(string text, CancellationToken cancellationToken = default)
//     {
//         try
//         {
//             TaskItem item = new() { Title = text };
//             _ = context.TaskItems.Add(item);
//             _ = await context.SaveChangesAsync(cancellationToken);
//             Items.Add(item);
//         }
//         catch (Exception ex)
//         {
//             await alertService.ShowErrorAlertAsync("AddItem", ex.Message);
//         }
//     }
// }
