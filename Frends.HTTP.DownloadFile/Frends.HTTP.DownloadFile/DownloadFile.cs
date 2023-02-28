﻿using Frends.HTTP.DownloadFile.Definitions;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Caching;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("Frends.HTTP.DownloadFile.Tests")]
namespace Frends.HTTP.DownloadFile;

/// <summary>
/// HTTP Task.
/// </summary>
public static class HTTP
{
    internal static IHttpClientFactory ClientFactory = new HttpClientFactory();
    internal static readonly ObjectCache ClientCache = MemoryCache.Default;
    private static readonly CacheItemPolicy _cachePolicy = new() { SlidingExpiration = TimeSpan.FromHours(1) };

    internal static void ClearClientCache()
    {
        var cacheKeys = ClientCache.Select(kvp => kvp.Key).ToList();
        foreach (var cacheKey in cacheKeys)
            ClientCache.Remove(cacheKey);
    }

    /// For mem cleanup.
    static HTTP()
    {
        var currentAssembly = Assembly.GetExecutingAssembly();
        var currentContext = AssemblyLoadContext.GetLoadContext(currentAssembly);
        if (currentContext != null)
            currentContext.Unloading += OnPluginUnloadingRequested;
    }

    /// <summary>
    /// Fetch file over HTTP.
    /// [Documentation](https://tasks.frends.com/tasks/frends-tasks/Frends.HTTP.DownloadFile)
    /// </summary>
    /// <param name="input">Input parameters</param>
    /// <param name="options">Optional parameters.</param>
    /// <param name="cancellationToken">Token generated by frends to stop this Task.</param>
    /// <returns>Object { bool Success, string FilePath } }</returns>
    public static async Task<Result> DownloadFile([PropertyTab] Input input, [PropertyTab] Options options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(input.Url)) throw new ArgumentNullException(input.Url);

        try
        {
            var httpClient = GetHttpClientForOptions(options);
            var headers = GetHeaderDictionary(input.Headers, options);

            //Clear default headers
            httpClient.DefaultRequestHeaders.Clear();

            if (headers != null && headers.Count > 0)
            {
                foreach (var header in headers)
                {
                    var requestHeaderAddedSuccessfully = httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                    if (!requestHeaderAddedSuccessfully)
                        Trace.TraceWarning($"Could not add header {header.Key}:{header.Value}");
                }
            }

            using var s = await httpClient.GetStreamAsync(input.Url, cancellationToken);
            using var fs = new FileStream(input.FilePath, FileMode.CreateNew);
            await s.CopyToAsync(fs, cancellationToken);

            return new Result(true, fs.Name);
        }
        catch (Exception ex)
        {
            throw new Exception(ex.Message, ex.InnerException);
        }
    }

    private static IDictionary<string, string> GetHeaderDictionary(Header[] headers, Options options)
    {
        if (headers != null)
        {
            if (!headers.Any(header => header.Name.ToLower().Equals("authorization")))
            {
                var authHeader = new Header { Name = "Authorization" };
                switch (options.Authentication)
                {
                    case Authentication.Basic:
                        authHeader.Value = $"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes($"{options.Username}:{options.Password}"))}";
                        headers = headers.Concat(new[] { authHeader }).ToArray();
                        break;
                    case Authentication.OAuth:
                        authHeader.Value = $"Bearer {options.Token}";
                        headers = headers.Concat(new[] { authHeader }).ToArray();
                        break;
                }
            }

            //Ignore case for headers and key comparison
            return headers.ToDictionary(key => key.Name, value => value.Value, StringComparer.InvariantCultureIgnoreCase);
        }

        return null;
    }

    private static HttpClient GetHttpClientForOptions(Options options)
    {
        var cacheKey = GetHttpClientCacheKey(options);

        if (ClientCache.Get(cacheKey) is HttpClient httpClient)
            return httpClient;

        httpClient = ClientFactory.CreateClient(options);
        httpClient.SetDefaultRequestHeadersBasedOnOptions(options);

        ClientCache.Add(cacheKey, httpClient, _cachePolicy);

        return httpClient;
    }

    private static string GetHttpClientCacheKey(Options options)
    {
        // Includes everything except for options.Token, which is used on request level, not http client level
        return $"{options.Authentication}:{options.Username}:{options.Password}:{options.ClientCertificateSource}"
               + $":{options.ClientCertificateFilePath}:{options.ClientCertificateInBase64}:{options.ClientCertificateKeyPhrase}"
               + $":{options.CertificateThumbprint}:{options.LoadEntireChainForCertificate}:{options.ConnectionTimeoutSeconds}"
               + $":{options.FollowRedirects}:{options.AllowInvalidCertificate}:{options.AllowInvalidResponseContentTypeCharSet}"
               + $":{options.ThrowExceptionOnErrorResponse}:{options.AutomaticCookieHandling}";
    }

    private static void OnPluginUnloadingRequested(AssemblyLoadContext obj)
    {
        obj.Unloading -= OnPluginUnloadingRequested;
    }
}