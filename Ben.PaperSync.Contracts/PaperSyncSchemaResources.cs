using System.Reflection;

namespace Ben.PaperSync.Contracts;

public static class PaperSyncSchemaResources
{
    public const string ResultV1ResourceName =
        "Ben.PaperSync.Contracts.Schemas.paper-sync-result.v1.schema.json";

    public const string AnalyzerV1ResourceName =
        "Ben.PaperSync.Contracts.Analyzers.paper-sync-auto-v1.json";

    public static Stream OpenResultV1() => Open(ResultV1ResourceName);

    public static Stream OpenAnalyzerV1() => Open(AnalyzerV1ResourceName);

    private static Stream Open(string resourceName) =>
        typeof(PaperSyncSchemaResources).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidOperationException($"Embedded schema '{resourceName}' was not found.");
}
