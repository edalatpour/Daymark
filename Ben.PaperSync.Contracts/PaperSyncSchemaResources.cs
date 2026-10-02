using System.Reflection;

namespace Ben.PaperSync.Contracts;

public static class PaperSyncSchemaResources
{
    public const string ResultV1ResourceName =
        "Ben.PaperSync.Contracts.Schemas.paper-sync-result.v1.schema.json";

    public const string LlmOutputV1ResourceName =
        "Ben.PaperSync.Contracts.Schemas.paper-sync-llm-output.v1.schema.json";

    public static Stream OpenResultV1() => Open(ResultV1ResourceName);

    public static Stream OpenLlmOutputV1() => Open(LlmOutputV1ResourceName);

    private static Stream Open(string resourceName) =>
        typeof(PaperSyncSchemaResources).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidOperationException($"Embedded schema '{resourceName}' was not found.");
}
