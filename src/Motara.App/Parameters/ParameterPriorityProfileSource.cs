namespace Motara.App.Parameters;

internal sealed class ParameterPriorityProfileSource
{
    private ParameterPriorityProfile current;

    internal ParameterPriorityProfileSource(ParameterPriorityProfile? initial = null)
    {
        current = initial ?? ParameterPriorityProfile.Default;
        current.Validate();
    }

    internal event EventHandler? Changed;

    internal ParameterPriorityProfile Current => Volatile.Read(ref current);

    internal void Apply(ParameterPriorityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        Volatile.Write(ref current, profile);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
