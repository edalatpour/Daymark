using System.Text.Json.Serialization;

namespace Ben.PaperSync.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = true)]
[JsonSerializable(typeof(PaperSyncResult))]
[JsonSerializable(typeof(ContentUnderstandingAnalyzeOperation))]
[JsonSerializable(typeof(ContentUnderstandingAnalyzerDefinition))]
[JsonSerializable(typeof(PaperSyncWarning))]
[JsonSerializable(typeof(List<PaperSyncWarning>))]
public sealed partial class PaperSyncJsonContext : JsonSerializerContext;
