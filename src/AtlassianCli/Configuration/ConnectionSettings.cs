namespace AtlassianCli.Configuration;

public sealed record ConnectionSettings(Uri BaseUri, string Token, TimeSpan Timeout)
{
    public static ConnectionSettings Load(string service, int timeoutSeconds)
    {
        return FromValues(
            Environment.GetEnvironmentVariable($"{service}_URL"),
            Environment.GetEnvironmentVariable($"{service}_PERSONAL_TOKEN"),
            timeoutSeconds,
            service);
    }

    public static ConnectionSettings FromValues(string? url, string? token, int timeoutSeconds, string service)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException($"{service}_URL must be an absolute HTTPS URL without credentials, query or fragment.");
        }

        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
        {
            throw new ArgumentException($"{service}_PERSONAL_TOKEN must contain a non-empty personal access token without whitespace.");
        }

        if (timeoutSeconds is < 1 or > 600)
        {
            throw new ArgumentException("--timeout must be between 1 and 600 seconds.");
        }

        return new ConnectionSettings(new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"), token, TimeSpan.FromSeconds(timeoutSeconds));
    }
}
