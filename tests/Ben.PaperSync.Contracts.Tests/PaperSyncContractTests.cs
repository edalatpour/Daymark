using System.Reflection;
using System.Text.Json;
using Ben.PaperSync.Contracts;
using Json.Schema;

namespace Ben.PaperSync.Contracts.Tests;

public sealed class PaperSyncContractTests
{
    private static readonly Lazy<JsonSchema> LlmOutputSchema = new(
        () => JsonSchema.FromText(ReadSchema(PaperSyncSchemaResources.OpenLlmOutputV1)));

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
    public void Model_fixture_round_trips_and_validates(
        string fixtureName,
        PlannerType requestedPlannerType,
        DetectedPlannerType expectedDetectedPlannerType)
    {
        var json = ReadFixture(fixtureName);
        var output = JsonSerializer.Deserialize(
            json,
            PaperSyncJsonContext.Default.PaperSyncLlmOutput);

        Assert.NotNull(output);
        Assert.Equal(expectedDetectedPlannerType, output.DetectedPlannerType);
        AssertSchemaValid(LlmOutputSchema.Value, json);

        var roundTripJson = JsonSerializer.Serialize(
            output,
            PaperSyncJsonContext.Default.PaperSyncLlmOutput);
        var roundTrip = JsonSerializer.Deserialize(
            roundTripJson,
            PaperSyncJsonContext.Default.PaperSyncLlmOutput);

        Assert.Equivalent(output, roundTrip, strict: true);

        var result = PaperSyncResultMapper.Map(
            output,
            new PaperSyncMappingContext
            {
                JobId = Guid.Parse("754029ec-0596-4873-82a4-21972f5ec099"),
                RequestedPlannerType = requestedPlannerType,
                ModelDeployment = "evaluation-model",
                PromptVersion = "v1",
                AnalyzedAt = DateTimeOffset.Parse("2026-10-02T17:00:00Z"),
                PageDateHint = new DateOnly(2026, 10, 4)
            });
        var resultJson = JsonSerializer.Serialize(
            result,
            PaperSyncJsonContext.Default.PaperSyncResult);

        Assert.Equal(expectedDetectedPlannerType, result.PlannerType.Detected);
        AssertSchemaValid(ResultSchema.Value, resultJson);
        Assert.NotNull(JsonSerializer.Deserialize(
            resultJson,
            PaperSyncJsonContext.Default.PaperSyncResult));
    }

    [Fact]
    public void Model_schema_requires_every_declared_property_and_rejects_extras()
    {
        using var schema = JsonDocument.Parse(
            ReadSchema(PaperSyncSchemaResources.OpenLlmOutputV1));

        AssertStrictObjects(schema.RootElement);
    }

    [Fact]
    public void Mapper_creates_stable_ids_and_preserves_illegible_marker()
    {
        var output = JsonSerializer.Deserialize(
            ReadFixture("franklin.json"),
            PaperSyncJsonContext.Default.PaperSyncLlmOutput)!;
        var context = new PaperSyncMappingContext
        {
            JobId = Guid.NewGuid(),
            RequestedPlannerType = PlannerType.Franklin,
            ModelDeployment = "evaluation-model",
            PromptVersion = "v1",
            AnalyzedAt = DateTimeOffset.UtcNow
        };

        var first = PaperSyncResultMapper.Map(output, context);
        var second = PaperSyncResultMapper.Map(output, context);

        Assert.Equal(first.Tasks.Select(task => task.Id), second.Tasks.Select(task => task.Id));
        Assert.True(first.Tasks[1].HasIllegibleText);
        Assert.Equal(0.4, first.Tasks[1].Confidence);
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

    private static void AssertStrictObjects(JsonElement schemaNode)
    {
        if (schemaNode.ValueKind == JsonValueKind.Object)
        {
            if (schemaNode.TryGetProperty("properties", out var properties))
            {
                Assert.True(schemaNode.TryGetProperty("additionalProperties", out var additional));
                Assert.False(additional.GetBoolean());
                Assert.True(schemaNode.TryGetProperty("required", out var required));

                var requiredNames = required
                    .EnumerateArray()
                    .Select(item => item.GetString())
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var property in properties.EnumerateObject())
                {
                    Assert.Contains(property.Name, requiredNames);
                }
            }

            foreach (var property in schemaNode.EnumerateObject())
            {
                AssertStrictObjects(property.Value);
            }
        }
        else if (schemaNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in schemaNode.EnumerateArray())
            {
                AssertStrictObjects(item);
            }
        }
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

    private static string ReadSchema(Func<Stream> openSchema)
    {
        using var stream = openSchema();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
