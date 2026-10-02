namespace Ben.PaperSync.Contracts;

public static class PaperSyncErrorCodes
{
    public const string UnsupportedFormat = "UNSUPPORTED_FORMAT";
    public const string ImageTooLarge = "IMAGE_TOO_LARGE";
    public const string ImageQualityLow = "IMAGE_QUALITY_LOW";
    public const string NoPlannerContent = "NO_PLANNER_CONTENT";
    public const string LowConfidence = "LOW_CONFIDENCE";
    public const string ExtractionFailed = "EXTRACTION_FAILED";
    public const string ContentFiltered = "CONTENT_FILTERED";
    public const string TransientFailure = "TRANSIENT_FAILURE";
    public const string QuotaExceeded = "QUOTA_EXCEEDED";
    public const string JobNotFound = "JOB_NOT_FOUND";
}
