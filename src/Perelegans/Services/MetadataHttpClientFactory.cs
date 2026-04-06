using System;
using System.Net;
using System.Net.Http;
using Perelegans.Models;

namespace Perelegans.Services;

/// <summary>
/// Creates HttpClient instances for metadata APIs, optionally using a user-configured proxy.
/// </summary>
public static class MetadataHttpClientFactory
{
    public static HttpClient Create(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ProxyAddress) &&
            Uri.TryCreate(settings.ProxyAddress, UriKind.Absolute, out var proxyUri))
        {
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy(proxyUri),
                UseProxy = true
            };

            return new HttpClient(handler, disposeHandler: true);
        }

        return new HttpClient();
    }
}
