using System.Net;

namespace ApiCollection.Postman.CloudApi;

public static class PostmanHttpClientFactory
{
    /// <summary>
    /// Creates the client used to talk to Postman. Redirects are <b>not</b> followed: the API key travels in a custom header,
    /// which .NET would forward to whatever address a redirect names. Timeouts are enforced per request by the caller.
    /// </summary>
    /// <param name="handler">Replaces the real network handler (tests only).</param>
    public static HttpClient Create(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        };

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ApiCollectionAutomation/1.0");
        return client;
    }
}
