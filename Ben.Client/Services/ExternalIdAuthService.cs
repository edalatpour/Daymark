using System.Text.Json;
using Ben.Models;

namespace Ben.Services;

/// <summary>
/// Handles sign-in with Apple and Google using Microsoft Entra External ID (CIAM)
/// via the platform's WebAuthenticator (ASWebAuthenticationSession on iOS).
///
/// CLIENT-SIDE ONLY — no backend calls are made. Token exchange happens entirely
/// inside the External ID authorization endpoint using the implicit / hybrid flow.
/// </summary>
public class ExternalIdAuthService
{
    // -----------------------------------------------------------------------
    // External ID tenant configuration (replace placeholders before shipping)
    // -----------------------------------------------------------------------

    /// <summary>
    /// The External ID (CIAM) tenant domain, e.g. "mytenant.ciamlogin.com".
    /// </summary>
    private const string TenantDomain = "mytenant.ciamlogin.com";

    /// <summary>
    /// The client (application) ID registered in the External ID tenant.
    /// </summary>
    private const string ClientId = "my-client-id";

    /// <summary>
    /// The custom policy name configured in the External ID tenant.
    /// </summary>
    private const string PolicyName = "B2C_1A_SignIn";

    /// <summary>
    /// Custom URL scheme redirect URI registered for this app in the External ID tenant.
    /// Must also be registered as a CFBundleURLScheme in Info.plist (iOS) and as an
    /// intent-filter scheme in AndroidManifest.xml (Android).
    /// </summary>
    private const string RedirectUri = "myapp://auth";

    // -----------------------------------------------------------------------
    // Preferences keys (namespaced to avoid collisions with MSAL keys)
    // -----------------------------------------------------------------------
    private const string AuthStateKey = "ExternalId_IsAuthenticated";
    private const string ProviderKey = "ExternalId_Provider";
    private const string UserIdKey = "ExternalId_UserId";
    private const string UserEmailKey = "ExternalId_UserEmail";
    private const string UserNameKey = "ExternalId_UserName";
    private const string IdTokenKey = "ExternalId_IdToken";
    private const string AccessTokenKey = "ExternalId_AccessToken";

    // -----------------------------------------------------------------------
    // Public event — raised whenever authentication state changes
    // -----------------------------------------------------------------------

    /// <summary>
    /// Raised after a successful sign-in or after sign-out so that the UI can
    /// refresh in response to the authentication state change.
    /// </summary>
    public event EventHandler? AuthenticationStateChanged;

    // -----------------------------------------------------------------------
    // Public state properties (persisted in MAUI Preferences)
    // -----------------------------------------------------------------------

    /// <summary>True when the user is currently signed in via External ID.</summary>
    public bool IsAuthenticated => Preferences.Default.Get(AuthStateKey, false);

    /// <summary>The provider that was last used to sign in ("Apple" or "Google").</summary>
    public string? Provider => Preferences.Default.Get(ProviderKey, (string?)null);

    /// <summary>Signed-in user's email address (may be null if not returned by provider).</summary>
    public string? UserEmail => Preferences.Default.Get(UserEmailKey, (string?)null);

    /// <summary>Signed-in user's display name (may be null if not returned by provider).</summary>
    public string? UserName => Preferences.Default.Get(UserNameKey, (string?)null);

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the stored External ID access_token as a Datasync-compatible
    /// <see cref="CommunityToolkit.Datasync.Client.Authentication.AuthenticationToken"/>.
    /// Called by <c>PlannerDbContext</c> on every outgoing Datasync HTTP request so
    /// that the backend can identify the user regardless of which identity provider
    /// they chose (Apple or Google).
    /// </summary>
    public Task<CommunityToolkit.Datasync.Client.Authentication.AuthenticationToken> GetAuthenticationTokenAsync(
        CancellationToken cancellationToken = default)
    {
        var accessToken = Preferences.Default.Get(AccessTokenKey, (string?)null) ?? string.Empty;
        var userId      = Preferences.Default.Get(UserIdKey,      (string?)null) ?? string.Empty;
        var email       = Preferences.Default.Get(UserEmailKey,   (string?)null) ?? string.Empty;

        // Try to read the expiry from the JWT exp claim; fall back to 1 hour from now
        var expiresOn = TryGetJwtExpiry(accessToken) ?? DateTimeOffset.UtcNow.AddHours(1);

        return Task.FromResult(new CommunityToolkit.Datasync.Client.Authentication.AuthenticationToken
        {
            DisplayName = email,
            ExpiresOn   = expiresOn,
            Token       = accessToken,
            UserId      = userId
        });
    }

    /// <summary>
    /// Launches the External ID sign-in flow for the specified identity provider.
    /// Uses <see cref="WebAuthenticator"/> (ASWebAuthenticationSession on iOS) so the
    /// browser chrome is visible to the user and Apple's App Store guidelines are met.
    /// </summary>
    /// <param name="provider">
    /// The identity provider to use: "apple" or "google".
    /// This value is passed as the <c>domain_hint</c> query parameter to the
    /// External ID authorize endpoint to skip the provider-selection screen.
    /// </param>
    /// <returns>
    /// A <see cref="UnifiedIdentity"/> when sign-in succeeds, or <c>null</c> when
    /// the user cancels or an error occurs.
    /// </returns>
    public async Task<UnifiedIdentity?> AuthenticateAsync(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider name must not be empty.", nameof(provider));

        try
        {
            // Build the full authorize URL for the requested identity provider
            var authorizeUrl = BuildAuthorizeUrl(provider);

            // callbackUri must match the redirect_uri registered in the External ID tenant
            var callbackUri = new Uri(RedirectUri);

            Console.WriteLine($"[ExternalId] Launching WebAuthenticator for provider: {provider}");
            Console.WriteLine($"[ExternalId] Authorize URL: {authorizeUrl}");

            // Launch ASWebAuthenticationSession (iOS) / Custom Tab (Android) / system browser (Windows)
            var result = await WebAuthenticator.Default.AuthenticateAsync(
                new Uri(authorizeUrl), callbackUri);

            // Parse the returned tokens and build the normalized identity
            return ParseAndStoreResult(result, provider);
        }
        catch (TaskCanceledException)
        {
            // User dismissed the browser without completing sign-in — not an error
            Console.WriteLine($"[ExternalId] Sign-in cancelled by user (provider: {provider})");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExternalId] Authentication error (provider: {provider}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Clears the External ID authentication state from preferences and raises
    /// <see cref="AuthenticationStateChanged"/>.
    /// </summary>
    public void SignOut()
    {
        Preferences.Default.Set(AuthStateKey, false);
        Preferences.Default.Remove(ProviderKey);
        Preferences.Default.Remove(UserIdKey);
        Preferences.Default.Remove(UserEmailKey);
        Preferences.Default.Remove(UserNameKey);
        Preferences.Default.Remove(IdTokenKey);
        Preferences.Default.Remove(AccessTokenKey);

        Console.WriteLine("[ExternalId] Signed out and preferences cleared.");
        AuthenticationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // -----------------------------------------------------------------------
    // URL builder
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the External ID (CIAM) OAuth 2.0 / OIDC authorize URL.
    ///
    /// Flow: implicit / hybrid — the authorization endpoint returns the id_token
    /// (and optionally an access_token) directly in the redirect callback.
    /// No additional token-endpoint call is required on the client.
    ///
    /// Shape of the generated URL:
    /// https://{TenantDomain}/{PolicyName}/oauth2/v2.0/authorize
    ///   ?client_id={ClientId}
    ///   &response_type=id_token token
    ///   &redirect_uri={RedirectUri}
    ///   &response_mode=fragment
    ///   &scope=openid profile email
    ///   &state={random-guid}
    ///   &nonce={random-guid}
    ///   &domain_hint={provider}
    /// </summary>
    private static string BuildAuthorizeUrl(string provider)
    {
        // Unique values per request to prevent replay attacks
        var state = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");

        // Assemble query parameters in a readable, deterministic order
        var parameters = new[]
        {
            ("client_id",      ClientId),
            ("response_type",  "id_token token"),
            ("redirect_uri",   RedirectUri),
            ("response_mode",  "fragment"),
            ("scope",          "openid profile email"),
            ("state",          state),
            ("nonce",          nonce),
            ("domain_hint",    provider.ToLowerInvariant()),
        };

        var queryString = string.Join("&",
            parameters.Select(p =>
                $"{Uri.EscapeDataString(p.Item1)}={Uri.EscapeDataString(p.Item2)}"));

        // External ID CIAM authorize endpoint (policy embedded in path segment)
        return $"https://{TenantDomain}/{PolicyName}/oauth2/v2.0/authorize?{queryString}";
    }

    // -----------------------------------------------------------------------
    // Token parsing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts id_token and access_token from the <see cref="WebAuthenticatorResult"/>
    /// returned by the callback redirect, decodes the JWT claims, persists the
    /// authentication state in Preferences, and returns a <see cref="UnifiedIdentity"/>.
    ///
    /// WebAuthenticator parses both query-string and URI-fragment parameters from
    /// the callback URL and exposes them via <see cref="WebAuthenticatorResult.Properties"/>,
    /// <see cref="WebAuthenticatorResult.IdToken"/>, and
    /// <see cref="WebAuthenticatorResult.AccessToken"/> convenience properties.
    /// </summary>
    private UnifiedIdentity? ParseAndStoreResult(WebAuthenticatorResult result, string provider)
    {
        // Prefer convenience properties; fall back to raw Properties dictionary
        var idToken = result.IdToken
            ?? (result.Properties.TryGetValue("id_token", out var rawIdToken) ? rawIdToken : null);
        var accessToken = result.AccessToken
            ?? (result.Properties.TryGetValue("access_token", out var rawAccessToken) ? rawAccessToken : null);

        if (string.IsNullOrEmpty(idToken) && string.IsNullOrEmpty(accessToken))
        {
            Console.WriteLine("[ExternalId] No tokens found in callback result.");
            return null;
        }

        // Decode standard OIDC claims from the id_token JWT
        string? userId = null;
        string? email = null;
        string? name = null;

        if (!string.IsNullOrEmpty(idToken))
        {
            try
            {
                var claims = ParseJwtPayloadClaims(idToken);
                claims.TryGetValue("sub", out userId);
                claims.TryGetValue("email", out email);
                claims.TryGetValue("name", out name);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ExternalId] Failed to parse id_token JWT claims: {ex.Message}");
            }
        }

        // Capitalize the provider name for display consistency ("Apple", "Google")
        var displayProvider = provider.Length > 0
            ? char.ToUpperInvariant(provider[0]) + (provider.Length > 1 ? provider[1..].ToLowerInvariant() : string.Empty)
            : provider;

        var identity = new UnifiedIdentity
        {
            Provider    = displayProvider,
            UserId      = userId      ?? string.Empty,
            Email       = email       ?? string.Empty,
            Name        = name        ?? string.Empty,
            IdToken     = idToken     ?? string.Empty,
            AccessToken = accessToken ?? string.Empty,
        };

        // Persist authentication state so it survives app restarts
        Preferences.Default.Set(AuthStateKey, true);
        Preferences.Default.Set(ProviderKey, displayProvider);
        if (userId      != null) Preferences.Default.Set(UserIdKey,      userId);
        if (email       != null) Preferences.Default.Set(UserEmailKey,   email);
        if (name        != null) Preferences.Default.Set(UserNameKey,    name);
        if (idToken     != null) Preferences.Default.Set(IdTokenKey,     idToken);
        if (accessToken != null) Preferences.Default.Set(AccessTokenKey, accessToken);

        Console.WriteLine($"[ExternalId] Sign-in succeeded. Provider={displayProvider}, Email={email}");

        AuthenticationStateChanged?.Invoke(this, EventArgs.Empty);
        return identity;
    }

    // -----------------------------------------------------------------------
    // JWT helper
    // -----------------------------------------------------------------------

    /// <summary>
    /// Decodes the Base64URL-encoded payload section of a JWT and returns all
    /// top-level string claims as a case-insensitive dictionary.
    /// </summary>
    private static Dictionary<string, string> ParseJwtPayloadClaims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Restore standard Base64 padding and character substitutions
        var payloadBase64 = parts[1].Replace('-', '+').Replace('_', '/');
        var mod4 = payloadBase64.Length % 4;
        if (mod4 > 0)
            payloadBase64 = payloadBase64.PadRight(payloadBase64.Length + (4 - mod4), '=');

        var payloadBytes = Convert.FromBase64String(payloadBase64);

        using var doc = JsonDocument.Parse(payloadBytes);
        var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            // Convert all claim values to their string representation
            claims[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.ToString();
        }

        return claims;
    }

    /// <summary>
    /// Extracts the <c>exp</c> (expiry) Unix timestamp from a JWT access_token and
    /// converts it to a <see cref="DateTimeOffset"/>. Returns <c>null</c> if the claim
    /// is absent or the token is not a valid JWT.
    /// </summary>
    private static DateTimeOffset? TryGetJwtExpiry(string jwt)
    {
        try
        {
            if (string.IsNullOrEmpty(jwt))
                return null;

            var claims = ParseJwtPayloadClaims(jwt);
            if (claims.TryGetValue("exp", out var expStr) && long.TryParse(expStr, out var expUnix))
                return DateTimeOffset.FromUnixTimeSeconds(expUnix);

            return null;
        }
        catch
        {
            return null;
        }
    }
}
