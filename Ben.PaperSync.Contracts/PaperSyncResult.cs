namespace Ben.PaperSync.Contracts;

public sealed record PaperSyncResult
{
    public required string SchemaVersion { get; init; }
    public required Guid JobId { get; init; }
    public required PlannerTypeSelection PlannerType { get; init; }
    public required string ModelDeployment { get; init; }
    public required string PromptVersion { get; init; }
    public string? AnalyzerId { get; init; }
    public string? AnalyzerVersion { get; init; }
    public string? ContentUnderstandingApiVersion { get; init; }
    public string? EmbeddingModelDeployment { get; init; }
    public required DateTimeOffset AnalyzedAt { get; init; }
    public required ExtractedPageDate PageDate { get; init; }
    public required double OverallConfidence { get; init; }
    public required List<ExtractedTask> Tasks { get; init; }
    public required List<ExtractedNote> Notes { get; init; }
    public required List<PaperSyncWarning> Warnings { get; init; }
}

public sealed record PlannerTypeSelection
{
    public required PlannerType Requested { get; init; }
    public required DetectedPlannerType Detected { get; init; }
}

public sealed record ExtractedPageDate
{
    public required DateOnly? Value { get; init; }
    public required double Confidence { get; init; }
    public required PageDateSource Source { get; init; }
}

public sealed record ExtractedTask
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public required string? Priority { get; init; }
    public required int? PriorityOrder { get; init; }
    public required bool Completed { get; init; }
    public required string? Marker { get; init; }
    public required string? Section { get; init; }
    public required int ReadingOrder { get; init; }
    public required double Confidence { get; init; }
    public required ConfidenceLevel ConfidenceLevel { get; init; }
    public required bool HasIllegibleText { get; init; }
    public required ExtractedTaskStatus Status { get; init; }
    public BoundingBox? BoundingBox { get; init; }
}

public sealed record ExtractedNote
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public required string? Section { get; init; }
    public required int ReadingOrder { get; init; }
    public required double Confidence { get; init; }
    public required ConfidenceLevel ConfidenceLevel { get; init; }
    public required bool HasIllegibleText { get; init; }
    public BoundingBox? BoundingBox { get; init; }
}

public sealed record BoundingBox
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
}

public sealed record PaperSyncWarning
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? ItemId { get; init; }
}
