using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Perelegans.Models;

namespace Perelegans.Services;

public sealed class BangumiOAuthService
{
    private const string AuthorizationEndpoint = "https://bgm.tv/oauth/authorize";
    private const string TokenEndpoint = "https://bgm.tv/oauth/access_token";
    private readonly HttpClient _httpClient;

    public BangumiOAuthService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<BangumiOAuthToken> SignInWithBrowserAsync(
        string clientId,
        string clientSecret,
        int callbackPort,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException(TranslationService.Instance["Settings_BangumiOAuthMissingClientId"]);
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException(TranslationService.Instance["Settings_BangumiOAuthMissingClientSecret"]);

        var port = callbackPort is >= 1024 and <= 65535 ? callbackPort : 45127;
        var redirectUri = $"http://127.0.0.1:{port}/bangumi/oauth/callback/";
        var state = CreateState();

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var authorizeUrl = BuildAuthorizeUrl(clientId.Trim(), redirectUri, state);
        Process.Start(new ProcessStartInfo(authorizeUrl)
        {
            UseShellExecute = true
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        var context = await listener.GetContextAsync().WaitAsync(timeoutCts.Token);
        var query = context.Request.QueryString;

        var responseHtml = "<html><body>You can close this window and return to Perelegans.</body></html>";
        var responseBytes = Encoding.UTF8.GetBytes(responseHtml);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = responseBytes.Length;
        await context.Response.OutputStream.WriteAsync(responseBytes, timeoutCts.Token);
        context.Response.Close();

        var returnedState = query["state"] ?? string.Empty;
        if (!string.Equals(returnedState, state, StringComparison.Ordinal))
            throw new InvalidOperationException(TranslationService.Instance["Settings_BangumiOAuthStateMismatch"]);

        var error = query["error"];
        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException(error);

        var code = query["code"];
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException(TranslationService.Instance["Settings_BangumiOAuthMissingCode"]);

        return await ExchangeCodeAsync(clientId.Trim(), clientSecret.Trim(), redirectUri, code, timeoutCts.Token);
    }

    public Task<BangumiOAuthToken> RefreshAsync(
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException(TranslationService.Instance["Settings_BangumiOAuthMissingRefreshToken"]);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId.Trim(),
            ["client_secret"] = clientSecret.Trim(),
            ["refresh_token"] = refreshToken.Trim(),
            ["redirect_uri"] = "http://127.0.0.1/"
        };

        return PostTokenAsync(form, cancellationToken);
    }

    private Task<BangumiOAuthToken> ExchangeCodeAsync(
        string clientId,
        string clientSecret,
        string redirectUri,
        string code,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri
        };

        return PostTokenAsync(form, cancellationToken);
    }

    private async Task<BangumiOAuthToken> PostTokenAsync(
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
        request.Headers.Add("User-Agent", "Perelegans/0.2 (https://github.com/Shizuku-in/Perelegans)");
        request.Content = new FormUrlEncodedContent(form);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture, "{0} {1}: {2}", (int)response.StatusCode, response.ReasonPhrase, responseBody));
        }

        var token = JsonSerializer.Deserialize<BangumiOAuthToken>(responseBody) ?? new BangumiOAuthToken();
        if (string.IsNullOrWhiteSpace(token.AccessToken))
            throw new InvalidOperationException(TranslationService.Instance["Settings_BangumiOAuthMissingAccessToken"]);

        return token;
    }

    private static string BuildAuthorizeUrl(string clientId, string redirectUri, string state)
    {
        return $"{AuthorizationEndpoint}?client_id={Uri.EscapeDataString(clientId)}" +
               $"&response_type=code" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&state={Uri.EscapeDataString(state)}";
    }

    private static string CreateState()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

}
