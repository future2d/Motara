namespace Motara.Core.Parameters;

/// <summary>Validates stable Motara global parameter identifiers.</summary>
public static class GlobalParameterId
{
    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id[0] is < 'A' or > 'Z')
        {
            return false;
        }

        for (int index = 1; index < id.Length; index++)
        {
            char value = id[index];
            if (value is not (>= 'A' and <= 'Z')
                && value is not (>= 'a' and <= 'z')
                && value is not (>= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }
}
