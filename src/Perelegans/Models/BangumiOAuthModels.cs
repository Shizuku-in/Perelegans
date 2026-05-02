using System;
using System.Text.Json.Serialization;

namespace Perelegans.Models;

public sealed class BangumiOAuthToken
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;

    public DateTime? ExpiresAt => ExpiresIn.HasValue
        ? DateTime.Now.AddSeconds(Math.Max(0, ExpiresIn.Value))
        : null;
}
