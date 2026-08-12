using System.Collections.Immutable;
using Motara.Persistence;

namespace Motara.App.Input;

public sealed class InputActionRegistry
{
    private readonly Dictionary<string, InputActionDescriptor> descriptors = new(StringComparer.Ordinal);
    private InputBindingProfile profile;

    public InputActionRegistry(InputBindingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        this.profile = profile;
    }

    public ImmutableArray<InputActionDescriptor> Descriptors => descriptors.Values
        .OrderBy(static descriptor => descriptor.CategoryResourceKey, StringComparer.Ordinal)
        .ThenBy(static descriptor => descriptor.Id, StringComparer.Ordinal)
        .ToImmutableArray();

    public InputBindingProfile Profile => profile;

    public void Register(InputActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptors.TryAdd(descriptor.Id, descriptor))
        {
            throw new ArgumentException($"Input action '{descriptor.Id}' is already registered.", nameof(descriptor));
        }
    }

    public InputBindingProfile ReplaceDescriptors(
        IEnumerable<InputActionDescriptor> replacements,
        InputBindingProfile candidateProfile)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        ArgumentNullException.ThrowIfNull(candidateProfile);
        InputActionDescriptor[] snapshot = replacements.ToArray();
        if (snapshot.Select(static descriptor => descriptor.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Input action IDs must be unique.", nameof(replacements));
        descriptors.Clear();
        foreach (InputActionDescriptor descriptor in snapshot) Register(descriptor);
        return ReconcileProfile(candidateProfile);
    }

    public InputResolution? Resolve(InputContext context, InputGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        if (context.ActiveScopes.IsDefault)
        {
            throw new ArgumentException("Input context scopes must be initialized.", nameof(context));
        }

        foreach (InputBindingScope scope in context.ActiveScopes)
        {
            InputBinding? binding = profile.Bindings.FirstOrDefault(candidate =>
                candidate.Scope == scope
                && InputGestureMatcher.Matches(candidate.Gesture, gesture));
            if (binding is not null && descriptors.ContainsKey(binding.ActionId))
            {
                return new InputResolution(
                    binding.ActionId,
                    binding.Scope,
                    ShouldConsume: !context.IsNativeControl);
            }
        }

        return null;
    }

    public ImmutableArray<InputBindingConflict> ValidateCandidate(
        InputBinding candidate,
        InputBindingEditMode mode)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        ValidateAllowed(candidate);
        return profile.Bindings
            .Where(existing => existing.Scope == candidate.Scope)
            .Where(existing => InputGestureMatcher.Matches(existing.Gesture, candidate.Gesture))
            .Where(existing => mode != InputBindingEditMode.Replace
                || !StringComparer.Ordinal.Equals(existing.ActionId, candidate.ActionId))
            .Select(existing => new InputBindingConflict(existing, candidate))
            .ToImmutableArray();
    }

    public InputBindingProfile Apply(InputBinding candidate, InputBindingEditMode mode)
    {
        ImmutableArray<InputBindingConflict> conflicts = ValidateCandidate(candidate, mode);
        if (!conflicts.IsEmpty)
        {
            throw new InvalidOperationException("The input gesture conflicts within its target scope.");
        }

        IEnumerable<InputBinding> retained = profile.Bindings;
        if (mode == InputBindingEditMode.Replace)
        {
            retained = retained.Where(binding =>
                binding.Scope != candidate.Scope
                || !StringComparer.Ordinal.Equals(binding.ActionId, candidate.ActionId));
        }

        profile = InputBindingProfile.Create(retained.Append(candidate), profile.Unavailable);
        return profile;
    }

    public InputBindingProfile ReconcileProfile(InputBindingProfile candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var active = ImmutableArray.CreateBuilder<InputBinding>();
        var unavailable = ImmutableArray.CreateBuilder<UnavailableBindingRecord>();

        foreach (InputBinding binding in candidate.Bindings)
        {
            if (IsRegisteredAndAllowed(binding.ActionId, binding.Scope))
            {
                active.Add(binding);
            }
            else
            {
                unavailable.Add(new UnavailableBindingRecord(
                    binding.ActionId,
                    binding.Scope,
                    binding.Gesture,
                    binding.DisplayName));
            }
        }

        foreach (UnavailableBindingRecord binding in candidate.Unavailable)
        {
            if (IsRegisteredAndAllowed(binding.ActionId, binding.Scope)
                && !active.Any(existing =>
                    existing.Scope == binding.Scope
                    && InputGestureMatcher.Matches(existing.Gesture, binding.Gesture)))
            {
                active.Add(new InputBinding(
                    binding.ActionId,
                    binding.Scope,
                    binding.Gesture,
                    displayName: binding.DisplayName));
            }
            else
            {
                unavailable.Add(binding);
            }
        }

        profile = InputBindingProfile.Create(active, unavailable);
        return profile;
    }

    private bool IsRegisteredAndAllowed(string actionId, InputBindingScope scope) =>
        descriptors.TryGetValue(actionId, out InputActionDescriptor? descriptor)
        && descriptor.AllowedScopes.Contains(scope);

    private void ValidateAllowed(InputBinding binding)
    {
        if (!descriptors.TryGetValue(binding.ActionId, out InputActionDescriptor? descriptor))
        {
            throw new ArgumentException("Input action is not registered.", nameof(binding));
        }

        if (!descriptor.AllowedScopes.Contains(binding.Scope))
        {
            throw new ArgumentException("Input action does not allow the requested scope.", nameof(binding));
        }

        if (binding.IsGlobalEnabled && !descriptor.AllowsGlobalRegistration)
        {
            throw new ArgumentException("Input action does not allow global registration.", nameof(binding));
        }
    }
}
