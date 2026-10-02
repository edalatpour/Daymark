using System.Text.Json.Serialization;

namespace Ben.PaperSync.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<PlannerType>))]
public enum PlannerType
{
    [JsonStringEnumMemberName("auto")]
    Auto,
    [JsonStringEnumMemberName("franklin")]
    Franklin,
    [JsonStringEnumMemberName("daytimer")]
    Daytimer,
    [JsonStringEnumMemberName("generic")]
    Generic
}

[JsonConverter(typeof(JsonStringEnumConverter<DetectedPlannerType>))]
public enum DetectedPlannerType
{
    [JsonStringEnumMemberName("franklin")]
    Franklin,
    [JsonStringEnumMemberName("daytimer")]
    Daytimer,
    [JsonStringEnumMemberName("generic")]
    Generic
}

[JsonConverter(typeof(JsonStringEnumConverter<ExtractedTaskStatus>))]
public enum ExtractedTaskStatus
{
    [JsonStringEnumMemberName("notStarted")]
    NotStarted,
    [JsonStringEnumMemberName("inProgress")]
    InProgress,
    [JsonStringEnumMemberName("completed")]
    Completed,
    [JsonStringEnumMemberName("forwarded")]
    Forwarded,
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
    [JsonStringEnumMemberName("delegated")]
    Delegated,
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter<ConfidenceLevel>))]
public enum ConfidenceLevel
{
    [JsonStringEnumMemberName("high")]
    High,
    [JsonStringEnumMemberName("medium")]
    Medium,
    [JsonStringEnumMemberName("low")]
    Low
}

[JsonConverter(typeof(JsonStringEnumConverter<PageDateSource>))]
public enum PageDateSource
{
    [JsonStringEnumMemberName("model")]
    Model,
    [JsonStringEnumMemberName("userHint")]
    UserHint
}

[JsonConverter(typeof(JsonStringEnumConverter<PageLegibility>))]
public enum PageLegibility
{
    [JsonStringEnumMemberName("good")]
    Good,
    [JsonStringEnumMemberName("fair")]
    Fair,
    [JsonStringEnumMemberName("poor")]
    Poor
}
