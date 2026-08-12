namespace Motara.App.Parameters;

internal sealed class ParameterArbitrator
{
    private readonly int[] ranks;

    internal ParameterArbitrator(ParameterPriorityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        ranks = new int[Enum.GetValues<ParameterProviderKind>().Length];
        for (int index = 0; index < profile.Order.Length; index++)
        {
            ranks[(int)profile.Order[index]] = index;
        }
    }

    internal ResolvedParameterValue Resolve(
        int parameterIndex,
        double defaultValue,
        ReadOnlySpan<ParameterContribution> contributions)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(parameterIndex);
        if (!double.IsFinite(defaultValue))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultValue));
        }

        double value = defaultValue;
        ParameterProviderKind provider = ParameterProviderKind.Default;
        int rank = ranks[(int)provider];
        foreach (ParameterContribution contribution in contributions)
        {
            if (contribution.ParameterIndex != parameterIndex
                || !double.IsFinite(contribution.Value)
                || !Enum.IsDefined(contribution.Provider))
            {
                continue;
            }

            int candidateRank = ranks[(int)contribution.Provider];
            if (candidateRank <= rank)
            {
                continue;
            }

            value = contribution.Value;
            provider = contribution.Provider;
            rank = candidateRank;
        }

        return new(value, provider);
    }
}
