namespace Motara.Persistence;

/// <summary>Contains local-only Cubism Editor output connection preferences.</summary>
public sealed record CubismEditorOutputSettings
{
    public const string DefaultEndpoint = "ws://127.0.0.1:22033/";

    public static CubismEditorOutputSettings Default { get; } = new(DefaultEndpoint, false, false);

    public CubismEditorOutputSettings(string endpoint, bool startOnLaunch, bool alwaysOutput = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        Endpoint = endpoint.Trim();
        StartOnLaunch = startOnLaunch;
        AlwaysOutput = alwaysOutput;
    }

    public string Endpoint { get; init; }

    public bool StartOnLaunch { get; init; }

    public bool AlwaysOutput { get; init; }

    internal static void Validate(CubismEditorOutputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || !string.Equals(endpoint.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException("Cubism Editor must use an absolute ws:// endpoint without credentials.", nameof(settings));
        }
    }
}
