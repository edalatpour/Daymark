using System.Text.Json.Serialization;

namespace Ben.PaperSync.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = true)]
[JsonSerializable(typeof(PaperSyncResult))]
[JsonSerializable(typeof(PaperSyncLlmOutput))]
[JsonSerializable(typeof(PaperSyncWarning))]
[JsonSerializable(typeof(List<PaperSyncWarning>))]
public sealed partial class PaperSyncJsonContext : JsonSerializerContext;
