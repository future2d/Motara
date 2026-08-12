using System.Collections.Immutable;
using Motara.Persistence;

namespace Motara.App.Input;

public sealed record InputActionDescriptor
{
    public InputActionDescriptor(
        string id,
        string nameResourceKey,
        string categoryResourceKey,
        ImmutableHashSet<InputBindingScope> allowedScopes,
        ImmutableArray<InputBinding> defaultBindings,
        bool allowsGlobalRegistration,
        bool allowsExternalSequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(nameResourceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryResourceKey);
        ArgumentNullException.ThrowIfNull(allowedScopes);
        if (allowedScopes.Count == 0 || defaultBindings.IsDefault)
        {
            throw new ArgumentException("Input actions require scopes and initialized defaults.");
        }

        if (defaultBindings.Any(binding =>
            !StringComparer.Ordinal.Equals(binding.ActionId, id)
            || !allowedScopes.Contains(binding.Scope)))
        {
            throw new ArgumentException("Default bindings must target this action and an allowed scope.");
        }

        Id = id;
        NameResourceKey = nameResourceKey;
        CategoryResourceKey = categoryResourceKey;
        AllowedScopes = allowedScopes;
        DefaultBindings = defaultBindings;
        AllowsGlobalRegistration = allowsGlobalRegistration;
        AllowsExternalSequence = allowsExternalSequence;
    }

    public string Id { get; }

    public string NameResourceKey { get; }

    public string CategoryResourceKey { get; }

    public ImmutableHashSet<InputBindingScope> AllowedScopes { get; }

    public ImmutableArray<InputBinding> DefaultBindings { get; }

    public bool AllowsGlobalRegistration { get; }

    public bool AllowsExternalSequence { get; }
}

public enum InputBindingEditMode
{
    Add = 0,
    Replace = 1,
}

public readonly record struct InputContext(
    ImmutableArray<InputBindingScope> ActiveScopes,
    bool IsNativeControl);

public readonly record struct InputResolution(
    string ActionId,
    InputBindingScope Scope,
    bool ShouldConsume);

public readonly record struct InputBindingConflict(
    InputBinding Existing,
    InputBinding Candidate);
