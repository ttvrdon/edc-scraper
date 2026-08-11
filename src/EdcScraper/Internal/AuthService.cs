using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Text.Json;
using EdcScraper.Models;

namespace EdcScraper.Internal;

/// <summary>
/// Handles Keycloak PKCE authentication and token lifecycle for the EDC portal.
/// </summary>
internal sealed class AuthService : IDisposable
{
    private const string SsoBaseUrl = "https://sso.portal.edc-cr.cz/auth/realms/edc/protocol/openid-connect";
    private const string ClientId = "a63c22a3-6e1d-4eac-b383-d06373da046a";
    private const string RedirectUri = "https://portal.edc-cr.cz/";

    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    private TokenResponse? _token;
    private DateTime _accessTokenExpiry;
    private DateTime _refreshTokenExpiry;

    public string? AccessToken => _token?.AccessToken;
    public bool IsLoggedIn => _token != null;

    public AuthService(JsonSerializerOptions jsonOptions)
    {
        _jsonOptions = jsonOptions;

        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
            UseCookies = true
        };
        _httpClient = new HttpClient(handler);
        BrowserHeaders.AddToBrowserHeaders(_httpClient);
    }

    /// <summary>
    /// Performs the full PKCE login flow: fetches the login page, submits credentials,
    /// extracts the authorization code from the redirect, and exchanges it for tokens.
    /// </summary>
    public async Task LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(codeVerifier);
        var state = GenerateRandomHex(16);

        // Step 1: Load the login page to get the signed form action URL (contains session_code, execution, tab_id)
        var authUrl = $"{SsoBaseUrl}/auth" +
            $"?client_id={Uri.EscapeDataString(ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&response_type=code" +
            $"&scope=openid" +
            $"&state={state}" +
            $"&code_challenge={codeChallenge}" +
            $"&code_challenge_method=S256";

        var loginPage = await GetFollowingRedirectsAsync(authUrl, cancellationToken);
        var loginHtml = await loginPage.Content.ReadAsStringAsync(cancellationToken);

        var formAction = ParseFormAction(loginHtml);
        if (string.IsNullOrEmpty(formAction))
            throw new EdcScraperException("Could not locate login form action URL in the SSO page.");

        // Step 2: POST credentials — Keycloak responds with a 302 to the redirect_uri containing the auth code
        var formContent = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", email),
            new KeyValuePair<string, string>("password", password),
            new KeyValuePair<string, string>("credentialId", ""),
        });

        var postResponse = await _httpClient.PostAsync(formAction, formContent, cancellationToken);

        // Follow redirects until we land on portal.edc-cr.cz with ?code=
        var code = await ExtractAuthCodeFromRedirectChainAsync(postResponse, cancellationToken);
        if (string.IsNullOrEmpty(code))
            throw new EdcScraperException("Authorization code not found after login. Verify credentials.");

        // Step 3: Exchange auth code for tokens
        await ExchangeCodeForTokenAsync(code, codeVerifier, cancellationToken);
    }

    /// <summary>
    /// Returns a valid access token, refreshing silently if it has expired.
    /// Throws if not logged in.
    /// </summary>
    public async Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_token == null)
            throw new InvalidOperationException("Not authenticated. Call LoginAsync first.");

        if (DateTime.UtcNow >= _accessTokenExpiry)
        {
            if (DateTime.UtcNow >= _refreshTokenExpiry)
                throw new EdcScraperException("Session has expired. Please call LoginAsync again.");

            await RefreshTokenAsync(cancellationToken);
        }

        return _token.AccessToken;
    }

    /// <summary>
    /// Performs server-side logout via Keycloak's end_session endpoint.
    /// This invalidates the session on the server and clears the local token state.
    /// </summary>
    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        if (_token == null)
            return;  // Already logged out

        try
        {
            // Call Keycloak's logout endpoint to invalidate the session server-side
            var formBody = new List<KeyValuePair<string, string>>
            {
                new("client_id", ClientId),
                new("post_logout_redirect_uri", RedirectUri),
            };

            // Only add id_token_hint if available (it's the id_token from the response)
            if (!string.IsNullOrEmpty(_token.IdToken))
                formBody.Add(new("id_token_hint", _token.IdToken));

            var body = new FormUrlEncodedContent(formBody);

            var response = await _httpClient.PostAsync($"{SsoBaseUrl}/logout", body, cancellationToken);
            
            // Logout endpoint may return 204 No Content, 302 redirect, or 200 OK; all are success
            if (!response.IsSuccessStatusCode && (int)response.StatusCode != 302)
            {
                var body_text = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new EdcScraperException(
                    $"Logout failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body_text}");
            }
        }
        catch (EdcScraperException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EdcScraperException("Logout request failed.", ex);
        }
        finally
        {
            // Clear local token state regardless of server response
            ClearToken();
        }
    }

    /// <summary>
    /// Clears the stored session tokens locally (does NOT call server).
    /// Use LogoutAsync() for proper server-side logout.
    /// </summary>
    private void ClearToken()
    {
        _token = null;
        _accessTokenExpiry = DateTime.MinValue;
        _refreshTokenExpiry = DateTime.MinValue;
    }

    private async Task ExchangeCodeForTokenAsync(string code, string codeVerifier, CancellationToken cancellationToken)
    {
        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("client_id", ClientId),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("code_verifier", codeVerifier),
        });

        var response = await _httpClient.PostAsync($"{SsoBaseUrl}/token", body, cancellationToken);
        await EnsureSuccessWithMessageAsync(response, "Token exchange failed");
        StoreToken(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private async Task RefreshTokenAsync(CancellationToken cancellationToken)
    {
        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("client_id", ClientId),
            new KeyValuePair<string, string>("refresh_token", _token!.RefreshToken),
        });

        var response = await _httpClient.PostAsync($"{SsoBaseUrl}/token", body, cancellationToken);
        await EnsureSuccessWithMessageAsync(response, "Token refresh failed");
        StoreToken(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private void StoreToken(string tokenJson)
    {
        _token = JsonSerializer.Deserialize<TokenResponse>(tokenJson, _jsonOptions)
            ?? throw new EdcScraperException("Failed to deserialize token response.");
        _accessTokenExpiry = DateTime.UtcNow.AddSeconds(_token.ExpiresIn - 30);
        _refreshTokenExpiry = DateTime.UtcNow.AddSeconds(_token.RefreshExpiresIn - 30);
    }

    // ----------------------------------------------------------------
    // HTTP helpers
    // ----------------------------------------------------------------

    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(string url, CancellationToken ct)
    {
        var response = await _httpClient.GetAsync(url, ct);
        int maxRedirects = 10;
        while (IsRedirect(response) && maxRedirects-- > 0)
        {
            var location = response.Headers.Location
                ?? throw new EdcScraperException("Redirect response missing Location header.");
            if (!location.IsAbsoluteUri)
                location = new Uri(new Uri(url), location);
            url = location.ToString();
            response = await _httpClient.GetAsync(url, ct);
        }
        return response;
    }

    /// <summary>
    /// Follows the redirect chain after credential POST until reaching the portal redirect URI
    /// that contains the authorization code in the query string.
    /// </summary>
    private async Task<string?> ExtractAuthCodeFromRedirectChainAsync(HttpResponseMessage response, CancellationToken ct)
    {
        int maxRedirects = 10;
        while (maxRedirects-- > 0)
        {
            var location = response.Headers.Location;
            if (location == null) break;

            // Resolve relative URIs against the SSO base
            if (!location.IsAbsoluteUri)
                location = new Uri(new Uri(SsoBaseUrl + "/"), location);

            var locationStr = location.ToString();

            // Check for the authorization code in the URL
            if (locationStr.Contains("portal.edc-cr.cz") || locationStr.StartsWith(RedirectUri))
            {
                var query = HttpUtility.ParseQueryString(location.Query);
                var code = query["code"];
                if (!string.IsNullOrEmpty(code))
                    return code;
            }

            // Continue following redirects
            response = await _httpClient.GetAsync(location, ct);
        }

        return null;
    }

    private static bool IsRedirect(HttpResponseMessage r) =>
        r.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
            or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static async Task EnsureSuccessWithMessageAsync(HttpResponseMessage response, string context)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new EdcScraperException($"{context}: HTTP {(int)response.StatusCode} — {body}");
        }
    }

    // ----------------------------------------------------------------
    // PKCE helpers
    // ----------------------------------------------------------------

    private static string GenerateCodeVerifier()
    {
        // RFC 7636: 43–128 characters of [A-Za-z0-9\-._~]
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string GenerateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string GenerateRandomHex(int byteCount)
    {
        var bytes = new byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ----------------------------------------------------------------
    // HTML parsing
    // ----------------------------------------------------------------

    private static readonly Regex FormActionRegex =
        new(@"(https?://[^\s""]+authenticate\?session_code=[^\s""]+)", RegexOptions.Compiled);

    private static string ParseFormAction(string html)
    {
        var match = FormActionRegex.Match(html);
        if (match.Success)
            return WebUtility.HtmlDecode(match.Groups[1].Value);

        // Fallback: relative path pattern
        var relMatch = Regex.Match(html,
            @"""(\/auth/realms/[^\s""]+authenticate\?session_code=[^\s""]+)""",
            RegexOptions.IgnoreCase);
        if (relMatch.Success)
        {
            var path = WebUtility.HtmlDecode(relMatch.Groups[1].Value);
            return "https://sso.portal.edc-cr.cz" + path;
        }

        return string.Empty;
    }

    public void Dispose() => _httpClient.Dispose();
}
