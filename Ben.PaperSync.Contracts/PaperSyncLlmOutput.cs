namespace Ben.PaperSync.Contracts;

public sealed record PaperSyncLlmOutput
{
    public required string SchemaVersion { get; init; }
    public required bool IsPlannerPage { get; init; }
    public required PageLegibility Legibility { get; init; }
    public required DetectedPlannerType DetectedPlannerType { get; init; }
    public required LlmPageDate? PageDate { get; init; }
    public required List<LlmExtractedTask> Tasks { get; init; }
    public required List<LlmExtractedNote> Notes { get; init; }
    public required List<string> IgnoredRegions { get; init; }
}

public sealed record LlmPageDate
{
    public required DateOnly Value { get; init; }
    public required ConfidenceLevel ConfidenceLevel { get; init; }
}

public sealed record LlmExtractedTask
{
    public required string Text { get; init; }
    public required string? Priority { get; init; }
    public required int? PriorityOrder { get; init; }
    public required bool Completed { get; init; }
    public required string? Marker { get; init; }
    public required string? Section { get; init; }
    public required int ReadingOrder { get; init; }
    public required ConfidenceLevel ConfidenceLevel { get; init; }
    public required ExtractedTaskStatus Status { get; init; }
}

public sealed record LlmExtractedNote
{
    public required string Text { get; init; }
    public required string? Section { get; init; }
    public required int ReadingOrder { get; init; }
    public required ConfidenceLevel ConfidenceLevel { get; init; }
}
