using System.Net;
using System.Text.Json;
using Ben.Models;

namespace Ben.Services;

/// <summary>
/// Handles Apple sign-in using Microsoft Entra External ID (CIAM)
/// via the platform's WebAuthenticator (ASWebAuthenticationSession on iOS/macOS)
/// or a localhost HTTP listener with system browser on Windows.
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
    private const string TenantDomain = "benid.ciamlogin.com";

    /// <summary>
    /// The client (application) ID registered in the External ID tenant.
    /// </summary>
    private const string ClientId = "0061f72b-2e92-4c32-9a5c-1f66dfc583a3";

    /// <summary>
    /// Tenant path segment used by the authorize endpoint.
    /// For this CIAM tenant, discovery publishes the tenant GUID path segment.
    /// </summary>
    private const string TenantPathSegment = "90726599-91bc-4a3b-9761-cb55bb201026";

    /// <summary>
    /// Datasync API scope exposed by the CIAM API app registration.
    /// </summary>
    private const string DatasyncScope = "api://54072f18-bd6a-4835-82b3-11d6f4b4f467/access_as_user";

    /// <summary>
    /// Custom URL scheme redirect URI registered for this app in the External ID tenant.
    /// Used on iOS/macOS with WebAuthenticator.
    /// </summary>
    private const string RedirectUri = "myapp://auth";

    /// <summary>
    /// Localhost redirect URI path used on Windows. The full URI includes a fixed port:
    /// http://localhost:{WindowsCallbackPort}/callback
    /// This exact URI must be registered as a "Web" or "SPA" redirect URI in the External ID tenant.
    /// Register: http://localhost:39841/callback
    /// </summary>
    private const string WindowsCallbackPath = "/callback";

    /// <summary>
    /// Fixed port for the Windows localhost callback listener.
    /// Using a fixed port allows the exact redirect URI to be registered in Entra External ID.
    /// </summary>
    private const int WindowsCallbackPort = 39841;

    /// <summary>
    /// Timeout for the Windows browser-based authentication flow.
    /// </summary>
    private static readonly TimeSpan WindowsAuthTimeout = TimeSpan.FromMinutes(5);

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

    /// <summary>The provider that was last used to sign in (currently "Apple").</summary>
    public string? Provider => Preferences.Default.Get(ProviderKey, (string?)null);

    /// <summary>Signed-in user's email address (may be null if not returned by provider).</summary>
    public string? UserEmail => Preferences.Default.Get(UserEmailKey, (string?)null);

    /// <summary>Signed-in user's display name (may be null if not returned by provider).</summary>
    public string? UserName => Preferences.Default.Get(UserNameKey, (string?)null);

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the stored External ID bearer token as a Datasync-compatible
    /// <see cref="CommunityToolkit.Datasync.Client.Authentication.AuthenticationToken"/>.
    /// Called by <c>PlannerDbContext</c> on every outgoing Datasync HTTP request so
    /// that the backend can identify the user for the active Apple sign-in.
    /// </summary>
    public Task<CommunityToolkit.Datasync.Client.Authentication.AuthenticationToken> GetAuthenticationTokenAsync(
        CancellationToken cancellationToken = default)
    {
        var accessToken = Preferences.Default.Get(AccessTokenKey, (string?)null);
        var idToken = Preferences.Default.Get(IdTokenKey, (string?)null);
        var bearerToken = !string.IsNullOrWhiteSpace(accessToken)
            ? accessToken
            : idToken ?? string.Empty;
        var userId = Preferences.Default.Get(UserIdKey, (string?)null) ?? string.Empty;
        var email = Preferences.Default.Get(UserEmailKey, (string?)null) ?? string.Empty;

        // Read the expiry from the JWT exp claim. If the token is unparsable we return
        // DateTimeOffset.MinValue so that Datasync treats the token as expired and does
        // not send it to the backend rather than using a fabricated window.
        var expiresOn = TryGetJwtExpiry(bearerToken) ?? DateTimeOffset.MinValue;

        return Task.FromResult(new CommunityToolkit.Datasync.Client.Authentication.AuthenticationToken
        {
            DisplayName = email,
            ExpiresOn = expiresOn,
            Token = bearerToken,
            UserId = userId
        });
    }

    /// <summary>
    /// Launches the External ID sign-in flow for Apple.
    /// Uses <see cref="WebAuthenticator"/> (ASWebAuthenticationSession on iOS) so the
    /// browser chrome is visible to the user and Apple's App Store guidelines are met.
    /// </summary>
    /// <returns>
    /// A <see cref="UnifiedIdentity"/> when sign-in succeeds, or <c>null</c> when
    /// the user cancels or an error occurs.
    /// </returns>
    public async Task<UnifiedIdentity?> AuthenticateAsync()
    {
        return await AuthenticateInternalAsync(forcePrompt: false);
    }

    /// <summary>
    /// Launches a forced re-authentication flow for sensitive actions (for example,
    /// account deletion), requiring the identity provider login prompt.
    /// </summary>
    public async Task<UnifiedIdentity?> ReauthenticateAsync()
    {
        return await AuthenticateInternalAsync(forcePrompt: true);
    }

    private async Task<UnifiedIdentity?> AuthenticateInternalAsync(bool forcePrompt)
    {
        const string provider = "apple";

        if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
        {
            return await AuthenticateWithWebAuthenticatorAsync(provider, forcePrompt);
        }

        if (OperatingSystem.IsWindows())
        {
            return await AuthenticateWithLocalhostListenerAsync(provider, forcePrompt);
        }

        Console.WriteLine("[ExternalId] Apple sign-in is not supported on this platform.");
        return null;
    }

    /// <summary>
    /// iOS/macOS flow using MAUI WebAuthenticator (ASWebAuthenticationSession).
    /// </summary>
    private async Task<UnifiedIdentity?> AuthenticateWithWebAuthenticatorAsync(string provider, bool forcePrompt)
    {
        try
        {
            var authorizeUrl = BuildAuthorizeUrl(RedirectUri, forcePrompt, out _);
            var callbackUri = new Uri(RedirectUri);

            Console.WriteLine($"[ExternalId] Launching WebAuthenticator for provider: {provider}");

            var result = await WebAuthenticator.Default.AuthenticateAsync(
                new Uri(authorizeUrl), callbackUri);

            return ParseAndStoreResult(result.Properties, provider);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[ExternalId] Sign-in canceled (provider: {provider}).");
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            Console.WriteLine("[ExternalId] Platform does not support WebAuthenticator.");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExternalId] Authentication error (provider: {provider}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Windows flow using a localhost HTTP listener and the system browser.
    /// Since the implicit flow returns tokens in the URL fragment (not sent to the server),
    /// the listener serves a small HTML page with JavaScript that extracts the fragment
    /// and POSTs it back to the listener.
    /// </summary>
    private async Task<UnifiedIdentity?> AuthenticateWithLocalhostListenerAsync(string provider, bool forcePrompt)
    {
        HttpListener? listener = null;

        try
        {
            // Start the listener on the fixed port
            listener = StartLocalhostListener(WindowsCallbackPort);

            var redirectUri = $"http://localhost:{WindowsCallbackPort}{WindowsCallbackPath}";
            var authorizeUrl = BuildAuthorizeUrl(redirectUri, forcePrompt, out var expectedState);

            Console.WriteLine($"[ExternalId] Opening system browser for provider: {provider}");
            Console.WriteLine($"[ExternalId] Listening on: {redirectUri}");

            // Open the system browser
            await Browser.OpenAsync(authorizeUrl, BrowserLaunchMode.SystemPreferred);

            // Wait for the callback with a timeout
            using var cts = new CancellationTokenSource(WindowsAuthTimeout);
            var tokenParams = await WaitForCallbackAsync(listener, expectedState, cts.Token);

            if (tokenParams == null)
            {
                Console.WriteLine("[ExternalId] No tokens received from callback.");
                return null;
            }

            return ParseAndStoreResult(tokenParams, provider);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[ExternalId] Sign-in timed out or was canceled (provider: {provider}).");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExternalId] Authentication error (provider: {provider}): {ex.Message}");
            return null;
        }
        finally
        {
            try { listener?.Stop(); } catch { /* best effort cleanup */ }
            try { listener?.Close(); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// Starts an HttpListener on the specified localhost port.
    /// </summary>
    private static HttpListener StartLocalhostListener(int port)
    {
        var listener = new HttpListener();
        var prefix = $"http://localhost:{port}{WindowsCallbackPath}/";
        listener.Prefixes.Add(prefix);
        listener.Start();
        return listener;
    }

    /// <summary>
    /// Waits for the browser callback. Handles both the initial GET (serves the JS fragment extractor)
    /// and the subsequent POST (receives the extracted tokens).
    /// </summary>
    private static async Task<Dictionary<string, string>?> WaitForCallbackAsync(
        HttpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        // Phase 1: Initial GET from the browser redirect — serve HTML/JS to extract the fragment
        while (!cancellationToken.IsCancellationRequested)
        {
            var getContextTask = listener.GetContextAsync();
            using var registration = cancellationToken.Register(() => listener.Stop());
            HttpListenerContext context;

            try
            {
                context = await getContextTask;
            }
            catch (HttpListenerException)
            {
                // Listener was stopped (timeout/cancellation)
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            var request = context.Request;

            if (request.HttpMethod == "GET")
            {
                // Check for error in query string (some providers redirect errors as query params)
                var queryError = request.QueryString["error"];
                if (!string.IsNullOrEmpty(queryError))
                {
                    var errorDesc = request.QueryString["error_description"] ?? "Unknown error";
                    Console.WriteLine($"[ExternalId] Auth error from provider: {queryError} - {errorDesc}");
                    await ServeHtmlResponseAsync(context.Response, GetErrorHtml(queryError, errorDesc));
                    return null;
                }

                // Serve the fragment extraction page
                await ServeHtmlResponseAsync(context.Response, GetFragmentExtractorHtml());
            }
            else if (request.HttpMethod == "POST")
            {
                // Phase 2: Receive the fragment data posted by the JS
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                var body = await reader.ReadToEndAsync(cancellationToken);
                var parameters = ParseFormUrlEncoded(body);

                // Validate state to prevent CSRF
                if (parameters.TryGetValue("state", out var returnedState) &&
                    returnedState != expectedState)
                {
                    Console.WriteLine("[ExternalId] State mismatch — possible CSRF. Rejecting callback.");
                    await ServeHtmlResponseAsync(context.Response, GetErrorHtml("state_mismatch",
                        "Authentication state validation failed. Please try again."));
                    return null;
                }

                // Check for error in the fragment
                if (parameters.TryGetValue("error", out var fragError))
                {
                    parameters.TryGetValue("error_description", out var fragErrorDesc);
                    Console.WriteLine($"[ExternalId] Auth error in fragment: {fragError} - {fragErrorDesc}");
                    await ServeHtmlResponseAsync(context.Response, GetErrorHtml(fragError, fragErrorDesc ?? "Unknown error"));
                    return null;
                }

                // Serve success page and return the tokens
                await ServeHtmlResponseAsync(context.Response, GetSuccessHtml());
                return parameters;
            }
            else
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
            }
        }

        return null;
    }

    private static async Task ServeHtmlResponseAsync(HttpListenerResponse response, string html)
    {
        response.ContentType = "text/html; charset=utf-8";
        response.StatusCode = 200;
        var buffer = System.Text.Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    /// <summary>
    /// HTML page served on the initial GET. JavaScript reads the URL fragment and POSTs
    /// the token parameters back to the same endpoint so the app can capture them.
    /// </summary>
    private static string GetFragmentExtractorHtml() => """
        <!DOCTYPE html>
        <html>
        <head><title>Signing in...</title></head>
        <body>
            <p>Completing sign-in, please wait...</p>
            <script>
                (function() {
                    var hash = window.location.hash.substring(1);
                    if (!hash) {
                        document.body.innerHTML = '<p>Authentication failed: no response received.</p>';
                        return;
                    }
                    var xhr = new XMLHttpRequest();
                    xhr.open('POST', window.location.pathname, true);
                    xhr.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded');
                    xhr.onload = function() {
                        document.body.innerHTML = '<p>Sign-in complete! You can close this tab.</p>';
                    };
                    xhr.onerror = function() {
                        document.body.innerHTML = '<p>Failed to complete sign-in. Please try again.</p>';
                    };
                    xhr.send(hash);
                })();
            </script>
        </body>
        </html>
        """;

    private static string GetSuccessHtml() => """
        <!DOCTYPE html>
        <html>
        <head><title>Sign-in Complete</title></head>
        <body>
            <p>Sign-in successful! You can close this tab and return to the app.</p>
        </body>
        </html>
        """;

    private static string GetErrorHtml(string error, string description) => $"""
        <!DOCTYPE html>
        <html>
        <head><title>Sign-in Failed</title></head>
        <body>
            <p>Sign-in failed: {System.Web.HttpUtility.HtmlEncode(description)}</p>
            <p>Error code: {System.Web.HttpUtility.HtmlEncode(error)}</p>
            <p>You can close this tab and try again in the app.</p>
        </body>
        </html>
        """;

    /// <summary>
    /// Parses a URL-encoded form body (key=value&amp;key2=value2) into a dictionary.
    /// </summary>
    private static Dictionary<string, string> ParseFormUrlEncoded(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(body)) return result;

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
            }
            else
            {
                var key = Uri.UnescapeDataString(pair[..eqIndex]);
                var value = Uri.UnescapeDataString(pair[(eqIndex + 1)..]);
                result[key] = value;
            }
        }

        return result;
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
    /// </summary>
    private static string BuildAuthorizeUrl(string redirectUri, bool forcePrompt, out string state)
    {
        // Unique values per request to prevent replay attacks
        state = Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");

        // Assemble query parameters in a readable, deterministic order
        var parameters = new[]
        {
            ("client_id",      ClientId),
            ("response_type",  "id_token token"),
            ("redirect_uri",   redirectUri),
            ("response_mode",  "fragment"),
            ("scope",          $"openid profile email {DatasyncScope}"),
            ("idp",            "Apple"),
            ("state",          state),
            ("nonce",          nonce),
            ("domain_hint",    "apple"),
        };

        if (forcePrompt)
        {
            parameters = [.. parameters,
                ("prompt", "login"),
                ("max_age", "0")
            ];
        }

        var queryString = string.Join("&",
            parameters.Select(p =>
                $"{Uri.EscapeDataString(p.Item1)}={Uri.EscapeDataString(p.Item2)}"));

        // External ID CIAM authorize endpoint from tenant discovery metadata.
        return $"https://{TenantDomain}/{TenantPathSegment}/oauth2/v2.0/authorize?{queryString}";
    }

    // -----------------------------------------------------------------------
    // Token parsing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts id_token and access_token from the token parameters dictionary,
    /// decodes the JWT claims, persists the authentication state in Preferences,
    /// and returns a <see cref="UnifiedIdentity"/>.
    /// </summary>
    private UnifiedIdentity? ParseAndStoreResult(IDictionary<string, string> properties, string provider)
    {
        properties.TryGetValue("id_token", out var idToken);
        properties.TryGetValue("access_token", out var accessToken);

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

        // Capitalize the provider name for display consistency.
        var displayProvider = provider.Length > 0
            ? char.ToUpperInvariant(provider[0]) + (provider.Length > 1 ? provider[1..].ToLowerInvariant() : string.Empty)
            : provider;

        var identity = new UnifiedIdentity
        {
            Provider = displayProvider,
            UserId = userId ?? string.Empty,
            Email = email ?? string.Empty,
            Name = name ?? string.Empty,
            IdToken = idToken ?? string.Empty,
            AccessToken = accessToken ?? string.Empty,
        };

        // Persist authentication state so it survives app restarts
        Preferences.Default.Set(AuthStateKey, true);
        Preferences.Default.Set(ProviderKey, displayProvider);
        if (userId != null) Preferences.Default.Set(UserIdKey, userId);
        if (email != null) Preferences.Default.Set(UserEmailKey, email);
        if (name != null) Preferences.Default.Set(UserNameKey, name);
        if (idToken != null) Preferences.Default.Set(IdTokenKey, idToken);
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
