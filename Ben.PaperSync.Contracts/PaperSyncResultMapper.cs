using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ben.PaperSync.Contracts;

public sealed record PaperSyncMappingContext
{
    public required Guid JobId { get; init; }
    public required PlannerType RequestedPlannerType { get; init; }
    public required string CompletionModelDeployment { get; init; }
    public required string EmbeddingModelDeployment { get; init; }
    public required string AnalyzerVersion { get; init; }
    public required DateTimeOffset AnalyzedAt { get; init; }
    public DateOnly? PageDateHint { get; init; }
}

public static partial class PaperSyncResultMapper
{
    public static PaperSyncResult Map(
        ContentUnderstandingAnalyzeOperation operation,
        PaperSyncMappingContext context)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(operation.Status, "Succeeded", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Content Understanding operation '{operation.Id}' is '{operation.Status}', not Succeeded.");
        }

        var analysis = operation.Result
            ?? throw new InvalidDataException("A succeeded Content Understanding operation has no result.");
        if (analysis.Contents.Count != 1)
        {
            throw new InvalidDataException(
                $"Expected one analyzed content item but received {analysis.Contents.Count}.");
        }

        var content = analysis.Contents[0];
        var fields = content.Fields;

        _ = GetRequiredBoolean(fields, "isPlannerPage");
        var detectedPlannerType = ParseDetectedPlannerType(
            GetRequiredString(fields, "detectedPlannerType"));
        var pageDateField = GetOptionalField(fields, "pageDate");
        var pageDate = GetOptionalDate(pageDateField, "pageDate") ?? context.PageDateHint;
        var tasks = GetRequiredArray(fields, "tasks")
            .Select(task => MapTask(task, content, pageDate))
            .ToList();
        var notes = GetRequiredArray(fields, "notes")
            .Select(note => MapNote(note, content, pageDate))
            .ToList();
        var confidences = tasks
            .Select(task => task.Confidence)
            .Concat(notes.Select(note => note.Confidence))
            .ToArray();
        var warnings = MapWarnings(analysis.Warnings, fields);

        return new PaperSyncResult
        {
            SchemaVersion = PaperSyncSchemaVersions.ResultV1,
            JobId = context.JobId,
            PlannerType = new PlannerTypeSelection
            {
                Requested = context.RequestedPlannerType,
                Detected = detectedPlannerType
            },
            ModelDeployment = context.CompletionModelDeployment,
            PromptVersion = context.AnalyzerVersion,
            AnalyzerId = analysis.AnalyzerId,
            AnalyzerVersion = context.AnalyzerVersion,
            ContentUnderstandingApiVersion = analysis.ApiVersion,
            EmbeddingModelDeployment = context.EmbeddingModelDeployment,
            AnalyzedAt = context.AnalyzedAt,
            PageDate = new ExtractedPageDate
            {
                Value = pageDate,
                Confidence = ValidateConfidence(
                    pageDateField?.Confidence
                        ?? (context.PageDateHint is not null ? 1 : 0),
                    "pageDate"),
                Source = pageDateField is null && context.PageDateHint is not null
                    ? PageDateSource.UserHint
                    : PageDateSource.Model
            },
            OverallConfidence = confidences.Length == 0 ? 0 : confidences.Average(),
            Tasks = tasks,
            Notes = notes,
            Warnings = warnings
        };
    }

    private static ExtractedTask MapTask(
        ContentUnderstandingField field,
        ContentUnderstandingDocumentContent content,
        DateOnly? pageDate)
    {
        var values = GetRequiredObject(field, "tasks item");
        var textField = GetRequiredField(values, "text");
        var text = GetRequiredString(textField, "tasks.text");
        var confidence = ValidateConfidence(
            field.Confidence ?? textField.Confidence ?? 0,
            "tasks item");
        var status = ParseTaskStatus(GetRequiredString(values, "status"));

        return new ExtractedTask
        {
            Id = CreateStableId(
                "task",
                pageDate,
                GetOptionalString(values, "section"),
                text),
            Text = text,
            Priority = GetOptionalString(values, "priority"),
            PriorityOrder = GetOptionalInteger(values, "priorityOrder"),
            Completed = GetRequiredBoolean(values, "completed")
                || status == ExtractedTaskStatus.Completed,
            Marker = GetOptionalString(values, "marker"),
            Section = GetOptionalString(values, "section"),
            ReadingOrder = GetRequiredInteger(values, "readingOrder"),
            Confidence = confidence,
            ConfidenceLevel = ToConfidenceLevel(confidence),
            HasIllegibleText = GetRequiredBoolean(values, "hasIllegibleText")
                || ContainsIllegibleMarker(text),
            Status = status,
            BoundingBox = ParseBoundingBox(field.Source ?? textField.Source, content)
        };
    }

    private static ExtractedNote MapNote(
        ContentUnderstandingField field,
        ContentUnderstandingDocumentContent content,
        DateOnly? pageDate)
    {
        var values = GetRequiredObject(field, "notes item");
        var textField = GetRequiredField(values, "text");
        var text = GetRequiredString(textField, "notes.text");
        var confidence = ValidateConfidence(
            field.Confidence ?? textField.Confidence ?? 0,
            "notes item");

        return new ExtractedNote
        {
            Id = CreateStableId(
                "note",
                pageDate,
                GetOptionalString(values, "section"),
                text),
            Text = text,
            Section = GetOptionalString(values, "section"),
            ReadingOrder = GetRequiredInteger(values, "readingOrder"),
            Confidence = confidence,
            ConfidenceLevel = ToConfidenceLevel(confidence),
            HasIllegibleText = GetRequiredBoolean(values, "hasIllegibleText")
                || ContainsIllegibleMarker(text),
            BoundingBox = ParseBoundingBox(field.Source ?? textField.Source, content)
        };
    }

    private static List<PaperSyncWarning> MapWarnings(
        IEnumerable<ContentUnderstandingError> providerWarnings,
        IReadOnlyDictionary<string, ContentUnderstandingField> fields)
    {
        var warnings = providerWarnings
            .Select(warning => new PaperSyncWarning
            {
                Code = warning.Code,
                Message = warning.Message
            })
            .ToList();

        if (GetOptionalField(fields, "ignoredRegions")?.ValueArray is { } ignoredRegions)
        {
            warnings.AddRange(
                ignoredRegions
                    .Where(field => !string.IsNullOrWhiteSpace(field.ValueString))
                    .Select(field => new PaperSyncWarning
                    {
                        Code = "IGNORED_REGION",
                        Message = $"{field.ValueString} ignored."
                    }));
        }

        return warnings;
    }

    private static BoundingBox? ParseBoundingBox(
        string? source,
        ContentUnderstandingDocumentContent content)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var match = DocumentSourceRegex().Match(source);
        if (!match.Success
            || !int.TryParse(
                match.Groups["page"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var pageNumber))
        {
            return null;
        }

        var coordinates = match.Groups["coordinates"].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var coordinate)
                    ? coordinate
                    : double.NaN)
            .ToArray();
        var page = content.Pages.FirstOrDefault(item => item.PageNumber == pageNumber);
        if (coordinates.Length != 8
            || coordinates.Any(double.IsNaN)
            || page is null
            || page.Width <= 0
            || page.Height <= 0)
        {
            return null;
        }

        var xCoordinates = coordinates.Where((_, index) => index % 2 == 0).ToArray();
        var yCoordinates = coordinates.Where((_, index) => index % 2 != 0).ToArray();
        var minX = Math.Clamp(xCoordinates.Min() / page.Width, 0, 1);
        var maxX = Math.Clamp(xCoordinates.Max() / page.Width, 0, 1);
        var minY = Math.Clamp(yCoordinates.Min() / page.Height, 0, 1);
        var maxY = Math.Clamp(yCoordinates.Max() / page.Height, 0, 1);
        var width = maxX - minX;
        var height = maxY - minY;

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return new BoundingBox
        {
            X = minX,
            Y = minY,
            Width = width,
            Height = height
        };
    }

    private static DateOnly? GetOptionalDate(ContentUnderstandingField? field, string name)
    {
        if (field is null)
        {
            return null;
        }

        return string.Equals(field.Type, "date", StringComparison.Ordinal)
            && field.ValueDate.HasValue
                ? field.ValueDate.Value
                : throw new InvalidDataException(
                    $"Content Understanding field '{name}' has no date value.");
    }

    private static double ValidateConfidence(double confidence, string name) =>
        double.IsFinite(confidence) && confidence is >= 0 and <= 1
            ? confidence
            : throw new InvalidDataException(
                $"Content Understanding field '{name}' has confidence outside the range 0 to 1.");

    private static DetectedPlannerType ParseDetectedPlannerType(string value) =>
        value switch
        {
            "franklin" => DetectedPlannerType.Franklin,
            "daytimer" => DetectedPlannerType.Daytimer,
            "generic" => DetectedPlannerType.Generic,
            _ => throw new InvalidDataException($"Unknown detected planner type '{value}'.")
        };

    private static ExtractedTaskStatus ParseTaskStatus(string value) =>
        value switch
        {
            "notStarted" => ExtractedTaskStatus.NotStarted,
            "inProgress" => ExtractedTaskStatus.InProgress,
            "completed" => ExtractedTaskStatus.Completed,
            "forwarded" => ExtractedTaskStatus.Forwarded,
            "cancelled" => ExtractedTaskStatus.Cancelled,
            "delegated" => ExtractedTaskStatus.Delegated,
            "unknown" => ExtractedTaskStatus.Unknown,
            _ => throw new InvalidDataException($"Unknown extracted task status '{value}'.")
        };

    private static ConfidenceLevel ToConfidenceLevel(double confidence) =>
        confidence switch
        {
            >= 0.85 => ConfidenceLevel.High,
            >= 0.6 => ConfidenceLevel.Medium,
            _ => ConfidenceLevel.Low
        };

    private static ContentUnderstandingField GetRequiredField(
        IReadOnlyDictionary<string, ContentUnderstandingField> fields,
        string name) =>
        fields.TryGetValue(name, out var field)
            ? field
            : throw new InvalidDataException($"Required Content Understanding field '{name}' is missing.");

    private static ContentUnderstandingField? GetOptionalField(
        IReadOnlyDictionary<string, ContentUnderstandingField> fields,
        string name) =>
        fields.TryGetValue(name, out var field) ? field : null;

    private static Dictionary<string, ContentUnderstandingField> GetRequiredObject(
        ContentUnderstandingField field,
        string name) =>
        string.Equals(field.Type, "object", StringComparison.Ordinal)
            && field.ValueObject is not null
                ? field.ValueObject
                : throw new InvalidDataException(
                    $"Content Understanding field '{name}' is not an object.");

    private static List<ContentUnderstandingField> GetRequiredArray(
        IReadOnlyDictionary<string, ContentUnderstandingField> fields,
        string name)
    {
        var field = GetRequiredField(fields, name);
        return string.Equals(field.Type, "array", StringComparison.Ordinal)
            && field.ValueArray is not null
                ? field.ValueArray
                : throw new InvalidDataException(
                    $"Content Understanding field '{name}' is not an array.");
    }

    private static string GetRequiredString(
        IReadOnlyDictionary<string, ContentUnderstandingField> fields,
        string name) =>
        GetRequiredString(GetRequiredField(fields, name), name);

    private static string GetRequiredString(ContentUnderstandingField field, string name) =>
        string.Equals(field.Type, "string", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(field.ValueString)
                ? field.ValueString
                : throw new InvalidDataException(
                    $"Content Understanding field '{name}' has no string value.");

    private static string? GetOptionalString(
        IReadOnlyDictionary<string, ContentUnderstandingField> fields,
        string name) =>
        GetOptionalField(fields, name) is { } field
            && string.Equals(field.Type, "string", StringComparison.Ordinal)
                ? field.ValueString
                : null;

    private static bool GetRequiredBoolean(
        IReadOnlyDictionary<string, ContentUnderstandingField> fields,
        string name)
    {
        var field = GetRequiredField(fields, name);
        return string.Equals(field.Type, "boolean", StringComparison.Ordinal)
            && field.ValueBoolean.HasValue
                ? field.ValueBoolean.Value
                : throw new InvalidDataException(
                    $"Content Understanding field '{name}' has no boolean value.");
    }

    private static int GetRequiredInteger(
        IReadOnlyDictionary<string, ContentUnderstandingField> fields,
        string name) =>
        GetOptionalInteger(fields, name)
        ?? throw new InvalidDataException(
            $"Content Understanding field '{name}' has no integer value.");

    private static int? GetOptionalInteger(
        IReadOnlyDictionary<string, ContentUnderstandingField> fields,
        string name)
    {
        var field = GetOptionalField(fields, name);
        if (field is null)
        {
            return null;
        }

        if (!string.Equals(field.Type, "integer", StringComparison.Ordinal)
            || !field.ValueInteger.HasValue
            || field.ValueInteger.Value is < int.MinValue or > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Content Understanding field '{name}' has an invalid integer value.");
        }

        return (int)field.ValueInteger.Value;
    }

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

    [GeneratedRegex(
        @"D\((?<page>\d+),(?<coordinates>[^)]+)\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex DocumentSourceRegex();
}
