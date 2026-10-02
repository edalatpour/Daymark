using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ben.PaperSync.Contracts;

public sealed record PaperSyncMappingContext
{
    public required Guid JobId { get; init; }
    public required PlannerType RequestedPlannerType { get; init; }
    public required string ModelDeployment { get; init; }
    public required string PromptVersion { get; init; }
    public required DateTimeOffset AnalyzedAt { get; init; }
    public DateOnly? PageDateHint { get; init; }
}

public static class PaperSyncResultMapper
{
    public static PaperSyncResult Map(PaperSyncLlmOutput output, PaperSyncMappingContext context)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(context);

        var pageDate = output.PageDate?.Value ?? context.PageDateHint;
        var tasks = output.Tasks
            .Select(task => MapTask(task, pageDate))
            .ToList();
        var notes = output.Notes
            .Select(note => MapNote(note, pageDate))
            .ToList();
        var confidences = tasks
            .Select(task => task.Confidence)
            .Concat(notes.Select(note => note.Confidence))
            .ToArray();

        return new PaperSyncResult
        {
            SchemaVersion = PaperSyncSchemaVersions.ResultV1,
            JobId = context.JobId,
            PlannerType = new PlannerTypeSelection
            {
                Requested = context.RequestedPlannerType,
                Detected = output.DetectedPlannerType
            },
            ModelDeployment = context.ModelDeployment,
            PromptVersion = context.PromptVersion,
            AnalyzedAt = context.AnalyzedAt,
            PageDate = new ExtractedPageDate
            {
                Value = pageDate,
                Confidence = output.PageDate is not null
                    ? ToNumericConfidence(output.PageDate.ConfidenceLevel)
                    : context.PageDateHint is not null
                        ? 1
                        : 0,
                Source = output.PageDate is null && context.PageDateHint is not null
                    ? PageDateSource.UserHint
                    : PageDateSource.Model
            },
            OverallConfidence = confidences.Length == 0 ? 0 : confidences.Average(),
            Tasks = tasks,
            Notes = notes,
            Warnings = []
        };
    }

    private static ExtractedTask MapTask(LlmExtractedTask task, DateOnly? pageDate)
    {
        var confidence = ToNumericConfidence(task.ConfidenceLevel);
        return new ExtractedTask
        {
            Id = CreateStableId("task", pageDate, task.Section, task.Text),
            Text = task.Text,
            Priority = task.Priority,
            PriorityOrder = task.PriorityOrder,
            Completed = task.Completed || task.Status == ExtractedTaskStatus.Completed,
            Marker = task.Marker,
            Section = task.Section,
            ReadingOrder = task.ReadingOrder,
            Confidence = confidence,
            ConfidenceLevel = task.ConfidenceLevel,
            HasIllegibleText = ContainsIllegibleMarker(task.Text),
            Status = task.Status
        };
    }

    private static ExtractedNote MapNote(LlmExtractedNote note, DateOnly? pageDate)
    {
        var confidence = ToNumericConfidence(note.ConfidenceLevel);
        return new ExtractedNote
        {
            Id = CreateStableId("note", pageDate, note.Section, note.Text),
            Text = note.Text,
            Section = note.Section,
            ReadingOrder = note.ReadingOrder,
            Confidence = confidence,
            ConfidenceLevel = note.ConfidenceLevel,
            HasIllegibleText = ContainsIllegibleMarker(note.Text)
        };
    }

    private static double ToNumericConfidence(ConfidenceLevel level) =>
        level switch
        {
            ConfidenceLevel.High => 0.9,
            ConfidenceLevel.Medium => 0.7,
            ConfidenceLevel.Low => 0.4,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
        };

    private static bool ContainsIllegibleMarker(string text) =>
        text.Contains("[?]", StringComparison.Ordinal);

    private static string CreateStableId(
        string itemType,
        DateOnly? pageDate,
        string? section,
        string text)
    {
        var input = string.Join(
            '\u001f',
            itemType,
            pageDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            section?.Trim().ToUpperInvariant() ?? string.Empty,
            text.Trim().ToUpperInvariant());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }
}
