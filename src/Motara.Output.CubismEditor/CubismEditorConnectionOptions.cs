namespace Motara.Output.CubismEditor;

/// <summary>Defines the local Cubism Editor external-API endpoint and bounded polling intervals.</summary>
public sealed class CubismEditorConnectionOptions
{
    public CubismEditorConnectionOptions(
        Uri endpoint,
        string applicationName = "Motara",
        TimeSpan? refreshInterval = null,
        TimeSpan? retryInterval = null,
        TimeSpan? requestTimeout = null,
        bool alwaysOutput = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        if (!endpoint.IsAbsoluteUri
            || !string.Equals(endpoint.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException("Cubism Editor must use an absolute ws:// endpoint without credentials.", nameof(endpoint));
        }

        Endpoint = endpoint;
        ApplicationName = applicationName.Trim();
        RefreshInterval = ValidateInterval(refreshInterval ?? TimeSpan.FromSeconds(1d / 60d), nameof(refreshInterval));
        RetryInterval = ValidateInterval(retryInterval ?? TimeSpan.FromSeconds(1), nameof(retryInterval));
        RequestTimeout = ValidateInterval(requestTimeout ?? TimeSpan.FromSeconds(2), nameof(requestTimeout));
        AlwaysOutput = alwaysOutput;
    }

    public Uri Endpoint { get; }

    public string ApplicationName { get; }

    public TimeSpan RefreshInterval { get; }

    public TimeSpan RetryInterval { get; }

    public TimeSpan RequestTimeout { get; }

    /// <summary>Gets whether output bypasses Cubism Editor's current edit-mode check.</summary>
    public bool AlwaysOutput { get; }

    public static CubismEditorConnectionOptions CreateDefault() =>
        new(new Uri("ws://127.0.0.1:22033/"));

    private static TimeSpan ValidateInterval(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}
