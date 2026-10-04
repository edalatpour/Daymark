using System.Text.Json;

namespace Ben.PaperSync.Contracts;

public sealed record ContentUnderstandingAnalyzeOperation
{
    public required string Id { get; init; }
    public required string Status { get; init; }
    public ContentUnderstandingAnalysisResult? Result { get; init; }
    public ContentUnderstandingError? Error { get; init; }
    public ContentUnderstandingUsage? Usage { get; init; }
}

public sealed record ContentUnderstandingAnalysisResult
{
    public required string AnalyzerId { get; init; }
    public required string ApiVersion { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required List<ContentUnderstandingDocumentContent> Contents { get; init; }
    public List<ContentUnderstandingError> Warnings { get; init; } = [];
}

public sealed record ContentUnderstandingDocumentContent
{
    public required string Kind { get; init; }
    public string? AnalyzerId { get; init; }
    public string? MimeType { get; init; }
    public string? Markdown { get; init; }
    public string? Unit { get; init; }
    public Dictionary<string, ContentUnderstandingField> Fields { get; init; } = [];
    public List<ContentUnderstandingPage> Pages { get; init; } = [];
}

public sealed record ContentUnderstandingPage
{
    public required int PageNumber { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
}

public sealed record ContentUnderstandingField
{
    public required string Type { get; init; }
    public double? Confidence { get; init; }
    public string? Source { get; init; }
    public List<ContentUnderstandingSpan> Spans { get; init; } = [];
    public string? ValueString { get; init; }
    public DateOnly? ValueDate { get; init; }
    public string? ValueTime { get; init; }
    public double? ValueNumber { get; init; }
    public long? ValueInteger { get; init; }
    public bool? ValueBoolean { get; init; }
    public List<ContentUnderstandingField>? ValueArray { get; init; }
    public Dictionary<string, ContentUnderstandingField>? ValueObject { get; init; }
    public JsonElement? ValueJson { get; init; }
}

public sealed record ContentUnderstandingSpan
{
    public required int Offset { get; init; }
    public required int Length { get; init; }
}

public sealed record ContentUnderstandingError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Target { get; init; }
    public List<ContentUnderstandingError> Details { get; init; } = [];
}

public sealed record ContentUnderstandingUsage
{
    public double? DocumentPagesMinimal { get; init; }
    public double? DocumentPagesBasic { get; init; }
    public double? DocumentPagesStandard { get; init; }
    public double? ContextualizationTokens { get; init; }
    public Dictionary<string, long> Tokens { get; init; } = [];
}

public sealed record ContentUnderstandingAnalyzerDefinition
{
    public required string Description { get; init; }
    public required string BaseAnalyzerId { get; init; }
    public required Dictionary<string, string> Models { get; init; }
    public required ContentUnderstandingAnalyzerConfig Config { get; init; }
    public required ContentUnderstandingFieldSchema FieldSchema { get; init; }
}

public sealed record ContentUnderstandingAnalyzerConfig
{
    public required bool ReturnDetails { get; init; }
    public required bool EnableFormula { get; init; }
    public required bool EstimateFieldSourceAndConfidence { get; init; }
    public required string TableFormat { get; init; }
}

public sealed record ContentUnderstandingFieldSchema
{
    public required Dictionary<string, JsonElement> Fields { get; init; }
}
