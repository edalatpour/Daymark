using System.Net.Http.Headers;
using System.Text.Json;
using Ben.Services.Auth;

namespace Ben.Services;

public sealed record DeleteCloudDataResult(
    bool IsSuccess,
    string Status,
    string Message,
    int TasksDeleted,
    int NotesDeleted,
    int ProjectsDeleted,
    int UsersDeleted,
    int StatusCode);

public interface ICloudAccountService
{
    Task<DeleteCloudDataResult> DeleteCloudDataAsync(CancellationToken cancellationToken = default);
}

public sealed class CloudAccountService : ICloudAccountService
{
    private static readonly HttpClient HttpClient = new();

    private readonly IUnifiedAuthService _unifiedAuthService;
    private readonly DatasyncOptions _datasyncOptions;

    public CloudAccountService(
        IUnifiedAuthService unifiedAuthService,
        DatasyncOptions datasyncOptions)
    {
        _unifiedAuthService = unifiedAuthService;
        _datasyncOptions = datasyncOptions;
    }

    public async Task<DeleteCloudDataResult> DeleteCloudDataAsync(CancellationToken cancellationToken = default)
    {
        if (_datasyncOptions.Endpoint == null)
        {
            return new DeleteCloudDataResult(false, "client_error", "Cloud endpoint is not configured.", 0, 0, 0, 0, 0);
        }

        var token = await _unifiedAuthService.GetAuthenticationTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token.Token))
        {
            return new DeleteCloudDataResult(false, "unauthorized", "Authentication token is unavailable.", 0, 0, 0, 0, 401);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_datasyncOptions.Endpoint, "account/delete-cloud-data"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken);

        return ParseResponse(response.StatusCode, payload);
    }

    private static DeleteCloudDataResult ParseResponse(System.Net.HttpStatusCode statusCode, string payload)
    {
        string status = "unknown";
        string message = string.Empty;
        int tasks = 0;
        int notes = 0;
        int projects = 0;
        int users = 0;

        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                JsonElement root = document.RootElement;

                if (root.TryGetProperty("status", out JsonElement statusElement) && statusElement.ValueKind == JsonValueKind.String)
                {
                    status = statusElement.GetString() ?? status;
                }

                if (root.TryGetProperty("message", out JsonElement messageElement) && messageElement.ValueKind == JsonValueKind.String)
                {
                    message = messageElement.GetString() ?? message;
                }

                if (root.TryGetProperty("deletedCounts", out JsonElement deletedCounts) && deletedCounts.ValueKind == JsonValueKind.Object)
                {
                    tasks = GetIntProperty(deletedCounts, "tasks");
                    notes = GetIntProperty(deletedCounts, "notes");
                    projects = GetIntProperty(deletedCounts, "projects");
                    users = GetIntProperty(deletedCounts, "users");
                }
            }
            catch
            {
                message = string.IsNullOrWhiteSpace(message)
                    ? "Unable to parse cloud deletion response."
                    : message;
            }
        }

        bool isSuccess = (int)statusCode >= 200 && (int)statusCode <= 299;

        if (string.IsNullOrWhiteSpace(message))
        {
            message = isSuccess
                ? "Cloud deletion request completed."
                : $"Cloud deletion request failed with status {(int)statusCode}.";
        }

        return new DeleteCloudDataResult(
            isSuccess,
            status,
            message,
            tasks,
            notes,
            projects,
            users,
            (int)statusCode);
    }

    private static int GetIntProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement valueElement))
        {
            return 0;
        }

        return valueElement.ValueKind == JsonValueKind.Number && valueElement.TryGetInt32(out int value)
            ? value
            : 0;
    }
}