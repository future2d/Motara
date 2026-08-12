using Motara.Persistence;

namespace Motara.App.Input;

public static class InputGestureMatcher
{
    public static bool Matches(InputGesture left, InputGesture right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return StringComparer.Ordinal.Equals(left.CanonicalText, right.CanonicalText);
    }
}
