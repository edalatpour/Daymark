namespace Ben.Views;

using System.Diagnostics;
using Ben.Models;
using Ben.Services;
using Ben.ViewModels;

public partial class TaskDetailsPage : ContentPage
{
    static readonly string[] StatusValues = { "NotStarted", "InProgress", "Completed", "Forwarded", "Deleted" };
    static readonly string[] PriorityValues = { "A", "B", "C" };
    private static readonly TimeSpan EditSessionSyncSuppression = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SaveCloseSyncSuppression = TimeSpan.FromSeconds(8);

    private readonly DailyViewModel _viewModel;
    private readonly TaskItem _task;
    private readonly bool _isNewTask;
    private readonly bool _useSuggestedOrderDefaults;
    private int _minOrder = 1;
    private int _maxOrder = 1;
    private int _order;
    private int _priorityIndex;
    private string _selectedStatus = "NotStarted";
    private string? _selectedForwardKey;
    private bool _isSaving;

    public TaskDetailsPage(DailyViewModel viewModel, TaskItem? task = null)
    {
        InitializeComponent();
        _viewModel = viewModel;

        if (task == null)
        {
            _isNewTask = true;
            _task = new TaskItem
            {
                Key = viewModel.CurrentDay?.Key ?? KeyConvention.ToDateKey(DateTime.Today),
                Status = "NotStarted",
                Priority = "A",
                Order = 1
            };
        }
        else
        {
            _isNewTask = false;
            _task = task;
        }

        _useSuggestedOrderDefaults = _isNewTask || IsUnplannedTask(_task);

        // Populate form fields
        TitleEntry.Text = _task.Title;
        _ = InitializeParentTaskDateAsync();

        _selectedStatus = Array.IndexOf(StatusValues, _task.Status) >= 0 ? _task.Status : "NotStarted";
        UpdateStatusSelection();

        _priorityIndex = Array.IndexOf(PriorityValues, _task.Priority);
        if (_priorityIndex < 0)
        {
            _priorityIndex = 0;
        }

        UpdatePriorityUi();

        _order = _task.Order > 0 ? _task.Order : 1;
        RefreshOrderForPriority();
        _ = InitializeForwardTargetsAsync();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Suppress background sync while the user is actively editing to reduce
        // sqlite gate contention when Save is pressed.
        _viewModel.SuppressSyncForLocalSave(EditSessionSyncSuppression);

        // DispatchDelayed ensures the modal transition is complete before
        // requesting focus, which is required on iOS for the keyboard to appear.
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () =>
        {
            TitleEntry.Focus();
            if (!_isNewTask && TitleEntry.Text?.Length > 0)
            {
                TitleEntry.CursorPosition = TitleEntry.Text.Length;
                TitleEntry.SelectionLength = 0;
            }
        });
    }

    async Task InitializeParentTaskDateAsync()
    {
        ParentTaskDateRow.IsVisible = false;
        ParentTaskDateLabel.Text = string.Empty;

        if (_isNewTask || string.IsNullOrWhiteSpace(_task.ParentTaskId))
        {
            return;
        }

        string? parentKey = await _viewModel.GetTaskKeyByIdAsync(_task.ParentTaskId);
        string parentDateValue = await _viewModel.GetPageDisplayAsync(parentKey);
        string parentDateText = !string.IsNullOrWhiteSpace(parentDateValue)
            ? $"Forwarded from {parentDateValue}."
            : "Forwarded.";

        if (!string.IsNullOrWhiteSpace(_task.OriginalTaskId)
            && _task.OriginalTaskId != _task.ParentTaskId)
        {
            string? originalKey = await _viewModel.GetTaskKeyByIdAsync(_task.OriginalTaskId);
            string originalDateValue = await _viewModel.GetPageDisplayAsync(originalKey);
            if (!string.IsNullOrWhiteSpace(originalDateValue))
            {
                parentDateText += $" Originally created on {originalDateValue}.";
            }
        }

        ParentTaskDateLabel.Text = parentDateText;
        ParentTaskDateRow.IsVisible = true;
    }

    void OnStatusSelected(object sender, TappedEventArgs e)
    {
        _selectedStatus = e.Parameter?.ToString() ?? "NotStarted";
        UpdateStatusSelection();
    }

    void UpdateStatusSelection()
    {
        if (Application.Current?.Resources == null)
        {
            return;
        }

        var ink = (Color)Application.Current.Resources["Ink"];
        var writingPaper = (Color)Application.Current.Resources["WritingPaper"];
        var line = (Color)Application.Current.Resources["Line"];

        ApplyStatusVisual(StatusBorderNotStarted, _selectedStatus == "NotStarted", ink, writingPaper, line);
        ApplyStatusVisual(StatusBorderInProgress, _selectedStatus == "InProgress", ink, writingPaper, line);
        ApplyStatusVisual(StatusBorderCompleted, _selectedStatus == "Completed", ink, writingPaper, line);
        ApplyStatusVisual(StatusBorderForwarded, _selectedStatus == "Forwarded", ink, writingPaper, line);
        ApplyStatusVisual(StatusBorderDeleted, _selectedStatus == "Deleted", ink, writingPaper, line);

        bool showForwardedTarget = _selectedStatus == "Forwarded";
        ForwardingTargetHost.IsVisible = showForwardedTarget;

        if (!showForwardedTarget)
        {
            _selectedForwardKey = null;
        }
    }

    static void ApplyStatusVisual(Border border, bool isSelected, Color darkColor, Color lightColor, Color line)
    {
        border.BackgroundColor = isSelected ? darkColor : lightColor;
        border.Stroke = line;

        Color textColor = isSelected ? lightColor : Colors.Black;

        if (border.Content is Layout layout)
        {
            foreach (IView child in layout.Children)
            {
                if (child is Label label)
                {
                    label.TextColor = textColor;
                }
            }
        }
    }

    void OnOrderDown(object sender, EventArgs e)
    {
        if (_order > _minOrder)
        {
            _order--;
            UpdateOrderUi();
        }
    }

    void OnOrderUp(object sender, EventArgs e)
    {
        if (_order < _maxOrder)
        {
            _order++;
            UpdateOrderUi();
        }
    }

    void OnPriorityUp(object sender, EventArgs e)
    {
        if (_priorityIndex > 0)
        {
            _priorityIndex--;
            UpdatePriorityUi();
            RefreshOrderForPriority();
        }
    }

    void OnPriorityDown(object sender, EventArgs e)
    {
        if (_priorityIndex < PriorityValues.Length - 1)
        {
            _priorityIndex++;
            UpdatePriorityUi();
            RefreshOrderForPriority();
        }
    }

    async void OnSaveClicked(object sender, EventArgs e)
    {
        if (_isSaving)
        {
            return;
        }

        _isSaving = true;
        SaveButton.IsEnabled = false;
        SaveButton.Opacity = 0.5;

        try
        {
            string saveTraceId = Guid.NewGuid().ToString("N")[..8];
            Stopwatch preCloseStopwatch = Stopwatch.StartNew();

            Console.WriteLine($"TaskSaveTiming[{saveTraceId}] page.preclose.begin isNewTask={_isNewTask}");

            string title = TitleEntry.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(title))
            {
                LogTaskSaveTiming(saveTraceId, "page.preclose.validation", preCloseStopwatch.ElapsedMilliseconds, "result=missing-title");
                await DisplayAlertAsync("Validation", "Please enter a task title.", "OK");
                return;
            }

            string? forwardDestinationKey = null;
            ForwardedTaskSeed? forwardedTaskSeed = null;

            if (_selectedStatus == "Forwarded")
            {
                forwardedTaskSeed = new ForwardedTaskSeed(_task.Status, string.Empty, 0);

                forwardDestinationKey = GetForwardDestinationKey();
                if (string.IsNullOrWhiteSpace(forwardDestinationKey))
                {
                    LogTaskSaveTiming(saveTraceId, "page.preclose.validation", preCloseStopwatch.ElapsedMilliseconds, "result=missing-forward-target");
                    await DisplayAlertAsync("Validation", "Please select a destination page.", "OK");
                    return;
                }

                if (string.Equals(forwardDestinationKey, _task.Key, StringComparison.Ordinal))
                {
                    LogTaskSaveTiming(saveTraceId, "page.preclose.validation", preCloseStopwatch.ElapsedMilliseconds, "result=forward-target-same-page");
                    await DisplayAlertAsync("Validation", "Please select a different page to forward this task to.", "OK");
                    return;
                }
            }

            string selectedPriority = PriorityValues[_priorityIndex];
            _order = Math.Clamp(_order, _minOrder, _maxOrder);

            // Local save should complete before close, but sync should stay out of the way.
            Stopwatch suppressStopwatch = Stopwatch.StartNew();
            _viewModel.SuppressSyncForLocalSave(SaveCloseSyncSuppression);
            LogTaskSaveTiming(saveTraceId, "page.preclose.suppress-sync", suppressStopwatch.ElapsedMilliseconds);

            // Save to local database before closing the modal.
            await _viewModel.SaveTaskDetailsLocallyAsync(
                _task,
                title,
                _selectedStatus,
                selectedPriority,
                _order,
                _isNewTask,
                forwardDestinationKey,
                forwardedTaskSeed,
                saveTraceId);
            LogTaskSaveTiming(saveTraceId, "page.preclose.local-save-complete", preCloseStopwatch.ElapsedMilliseconds);

            // Close the modal immediately after local save.
            Stopwatch closeStopwatch = Stopwatch.StartNew();
            await Navigation.PopModalAsync();
            LogTaskSaveTiming(saveTraceId, "page.preclose.modal-close", closeStopwatch.ElapsedMilliseconds);
            LogTaskSaveTiming(saveTraceId, "page.preclose.total", preCloseStopwatch.ElapsedMilliseconds);

            // Fire-and-forget: sort tasks, refresh UI, then sync in background.
            _ = _viewModel.CompleteTaskSaveAfterCloseAsync(
                _task,
                selectedPriority,
                _order,
                _isNewTask);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"TaskSaveTiming page.preclose.failed {ex.GetType().Name}: {ex.Message}");
            await DisplayAlertAsync("Save failed", "Could not save the task. Please try again.", "OK");
        }
        finally
        {
            _isSaving = false;
            SaveButton.IsEnabled = true;
            SaveButton.Opacity = 1.0;
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

    async void OnCancelClicked(object sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }

    static bool IsUnplannedTask(TaskItem task)
    {
        return string.IsNullOrWhiteSpace(task.Priority) && task.Order == 0;
    }

    void RefreshOrderForPriority()
    {
        string selectedPriority = PriorityValues[_priorityIndex];
        string key = !string.IsNullOrWhiteSpace(_task.Key)
            ? _task.Key
            : _viewModel.CurrentDay?.Key ?? KeyConvention.ToDateKey(DateTime.Today);
        (int min, int max) = _viewModel.GetTaskOrderRange(key, selectedPriority, _isNewTask ? null : _task);

        _minOrder = min;
        _maxOrder = max;

        if (_useSuggestedOrderDefaults)
        {
            _order = _maxOrder;
        }
        else
        {
            _order = Math.Clamp(_order, _minOrder, _maxOrder);
        }

        UpdateOrderUi();
    }

    void UpdateOrderUi()
    {
        OrderLabel.Text = _order.ToString();
        OrderDownButton.IsEnabled = _order > _minOrder;
        OrderUpButton.IsEnabled = _order < _maxOrder;
    }

    void UpdatePriorityUi()
    {
        PriorityLabel.Text = PriorityValues[_priorityIndex];
        PriorityUpButton.IsEnabled = _priorityIndex > 0;
        PriorityDownButton.IsEnabled = _priorityIndex < PriorityValues.Length - 1;
    }

    async Task InitializeForwardTargetsAsync()
    {
        List<ProjectItem> forwardProjects = (await _viewModel.GetProjectsAsync())
            .Where(project => !string.Equals(KeyConvention.ToProjectKey(project.Id), _task.Key, StringComparison.Ordinal))
            .ToList();

        ForwardTargetSelector.Projects = forwardProjects;

        if (!string.IsNullOrWhiteSpace(_selectedForwardKey))
        {
            ForwardTargetSelector.SelectedKey = _selectedForwardKey;
            return;
        }

        DateTime sourceDate = KeyConvention.TryParseDateKey(_task.Key, out DateTime parsedDate)
            ? parsedDate
            : DateTime.Today;
        DateTime forwardDate = sourceDate < DateTime.Today.Date
            ? DateTime.Today
            : sourceDate.AddDays(1);

        ForwardTargetSelector.SelectedKey = KeyConvention.ToDateKey(forwardDate);
    }

    string? GetForwardDestinationKey()
    {
        _selectedForwardKey = ForwardTargetSelector.SelectedKey;
        return _selectedForwardKey;
    }
}
