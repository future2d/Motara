namespace Motara.Tracking.Abstractions;

/// <summary>Describes whether a parameter slot contains usable input.</summary>
public enum ParameterValidity
{
    /// <summary>The source supplied a finite value for the slot.</summary>
    Valid,

    /// <summary>The source did not supply the slot in this frame.</summary>
    Missing,

    /// <summary>The source supplied a value that failed boundary validation.</summary>
    Invalid,
}
