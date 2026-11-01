using System.Net;

namespace AtlassianCli.Http;

public sealed class AtlassianApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
