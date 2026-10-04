using System.Text.Json;
using Ben.PaperSync.Contracts;
using Json.Schema;

namespace Ben.PaperSync.Contracts.Tests;

public sealed class PaperSyncContractTests
{
    private static readonly Lazy<JsonSchema> ResultSchema = new(
        () => JsonSchema.FromText(ReadSchema(PaperSyncSchemaResources.OpenResultV1)));

    public static TheoryData<string, PlannerType, DetectedPlannerType> PlannerFixtures =>
        new()
        {
            { "franklin.json", PlannerType.Franklin, DetectedPlannerType.Franklin },
            { "daytimer.json", PlannerType.Daytimer, DetectedPlannerType.Daytimer },
            { "generic.json", PlannerType.Auto, DetectedPlannerType.Generic }
        };

    [Theory]
    [MemberData(nameof(PlannerFixtures))]
    public void Provider_fixture_round_trips_maps_and_validates(
        string fixtureName,
        PlannerType requestedPlannerType,
        DetectedPlannerType expectedDetectedPlannerType)
    {
        var json = ReadFixture(fixtureName);
        var operation = JsonSerializer.Deserialize(
            json,
            PaperSyncJsonContext.Default.ContentUnderstandingAnalyzeOperation);

        Assert.NotNull(operation);
        Assert.Equal("Succeeded", operation.Status);
        Assert.Equal(PaperSyncSchemaVersions.ContentUnderstandingApiVersion, operation.Result!.ApiVersion);

        var roundTripJson = JsonSerializer.Serialize(
            operation,
            PaperSyncJsonContext.Default.ContentUnderstandingAnalyzeOperation);
        var roundTrip = JsonSerializer.Deserialize(
            roundTripJson,
            PaperSyncJsonContext.Default.ContentUnderstandingAnalyzeOperation);

        Assert.Equivalent(operation, roundTrip, strict: true);

        var result = PaperSyncResultMapper.Map(
            operation,
            new PaperSyncMappingContext
            {
                JobId = Guid.Parse("754029ec-0596-4873-82a4-21972f5ec099"),
                RequestedPlannerType = requestedPlannerType,
                CompletionModelDeployment = "evaluation-completion",
                EmbeddingModelDeployment = "evaluation-embedding",
                AnalyzerVersion = PaperSyncSchemaVersions.AnalyzerV1,
                AnalyzedAt = DateTimeOffset.Parse("2026-10-02T17:00:00Z"),
                PageDateHint = new DateOnly(2026, 10, 4)
            });
        var resultJson = JsonSerializer.Serialize(
            result,
            PaperSyncJsonContext.Default.PaperSyncResult);

        Assert.Equal(expectedDetectedPlannerType, result.PlannerType.Detected);
        Assert.Equal("paper-sync-auto-v1", result.AnalyzerId);
        Assert.Equal("evaluation-embedding", result.EmbeddingModelDeployment);
        Assert.Contains(result.Warnings, warning => warning.Code == "IGNORED_REGION");
        AssertSchemaValid(ResultSchema.Value, resultJson);
        Assert.NotNull(JsonSerializer.Deserialize(
            resultJson,
            PaperSyncJsonContext.Default.PaperSyncResult));
    }

    [Fact]
    public void Analyzer_definition_uses_document_grounding_and_required_fields()
    {
        var analyzerJson = ReadSchema(PaperSyncSchemaResources.OpenAnalyzerV1);
        var analyzer = JsonSerializer.Deserialize(
            analyzerJson,
            PaperSyncJsonContext.Default.ContentUnderstandingAnalyzerDefinition);

        Assert.NotNull(analyzer);
        Assert.Equal("prebuilt-document", analyzer.BaseAnalyzerId);
        Assert.True(analyzer.Config.EstimateFieldSourceAndConfidence);
        Assert.Equal("prebuilt-analyzer-completion", analyzer.Models["completion"]);
        Assert.Equal("prebuilt-analyzer-embedding", analyzer.Models["embedding"]);
        Assert.Contains("isPlannerPage", analyzer.FieldSchema.Fields.Keys);
        Assert.Contains("detectedPlannerType", analyzer.FieldSchema.Fields.Keys);
        Assert.Contains("pageDate", analyzer.FieldSchema.Fields.Keys);
        Assert.Contains("tasks", analyzer.FieldSchema.Fields.Keys);
        Assert.Contains("notes", analyzer.FieldSchema.Fields.Keys);
        Assert.Contains("ignoredRegions", analyzer.FieldSchema.Fields.Keys);
    }

    [Fact]
    public void Mapper_creates_stable_ids_and_preserves_illegible_marker()
    {
        var operation = JsonSerializer.Deserialize(
            ReadFixture("franklin.json"),
            PaperSyncJsonContext.Default.ContentUnderstandingAnalyzeOperation)!;
        var context = new PaperSyncMappingContext
        {
            JobId = Guid.NewGuid(),
            RequestedPlannerType = PlannerType.Franklin,
            CompletionModelDeployment = "evaluation-completion",
            EmbeddingModelDeployment = "evaluation-embedding",
            AnalyzerVersion = PaperSyncSchemaVersions.AnalyzerV1,
            AnalyzedAt = DateTimeOffset.UtcNow
        };

        var first = PaperSyncResultMapper.Map(operation, context);
        var second = PaperSyncResultMapper.Map(operation, context);

        Assert.Equal(first.Tasks.Select(task => task.Id), second.Tasks.Select(task => task.Id));
        Assert.True(first.Tasks[1].HasIllegibleText);
        Assert.Equal(0.4, first.Tasks[1].Confidence);
        var boundingBox = Assert.IsType<BoundingBox>(first.Tasks[0].BoundingBox);
        Assert.Equal(0.06, boundingBox.X, precision: 3);
        Assert.Equal(0.04, boundingBox.Height, precision: 3);
    }

    [Fact]
    public void Mapper_clips_grounding_to_page_bounds()
    {
        var operation = ReadFranklinOperation();
        var task = operation.Result!.Contents[0].Fields["tasks"].ValueArray![0];
        operation.Result.Contents[0].Fields["tasks"].ValueArray![0] = task with
        {
            Source = "D(1,1800,400,2200,400,2200,520,1800,520)"
        };

        var result = MapFranklinOperation(operation);

        var boundingBox = Assert.IsType<BoundingBox>(result.Tasks[0].BoundingBox);
        Assert.Equal(0.9, boundingBox.X, precision: 3);
        Assert.Equal(0.1, boundingBox.Width, precision: 3);
    }

    [Fact]
    public void Mapper_rejects_confidence_outside_public_schema_range()
    {
        var operation = ReadFranklinOperation();
        var task = operation.Result!.Contents[0].Fields["tasks"].ValueArray![0];
        operation.Result.Contents[0].Fields["tasks"].ValueArray![0] = task with
        {
            Confidence = 1.1
        };

        Assert.Throws<InvalidDataException>(() => MapFranklinOperation(operation));
    }

    private static void AssertSchemaValid(JsonSchema schema, string instanceJson)
    {
        using var instance = JsonDocument.Parse(instanceJson);
        var results = schema.Evaluate(
            instance.RootElement,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.List
            });

        Assert.True(results.IsValid);
    }

    private static string ReadFixture(string name)
    {
        var assembly = typeof(PaperSyncContractTests).Assembly;
        var resourceName =
            $"{typeof(PaperSyncContractTests).Namespace}.Fixtures.{name}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Fixture '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static ContentUnderstandingAnalyzeOperation ReadFranklinOperation() =>
        JsonSerializer.Deserialize(
            ReadFixture("franklin.json"),
            PaperSyncJsonContext.Default.ContentUnderstandingAnalyzeOperation)!;

    private static PaperSyncResult MapFranklinOperation(
        ContentUnderstandingAnalyzeOperation operation) =>
        PaperSyncResultMapper.Map(
            operation,
            new PaperSyncMappingContext
            {
                JobId = Guid.NewGuid(),
                RequestedPlannerType = PlannerType.Franklin,
                CompletionModelDeployment = "evaluation-completion",
                EmbeddingModelDeployment = "evaluation-embedding",
                AnalyzerVersion = PaperSyncSchemaVersions.AnalyzerV1,
                AnalyzedAt = DateTimeOffset.UtcNow
            });

    private static string ReadSchema(Func<Stream> openSchema)
    {
        using var stream = openSchema();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
