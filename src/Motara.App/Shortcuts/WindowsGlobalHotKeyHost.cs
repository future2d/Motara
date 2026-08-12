using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Motara.Persistence;

namespace Motara.App.Shortcuts;

public enum GlobalHotKeyRegistrationState
{
    Registered,
    Unavailable,
    UnsupportedGesture,
    Conflict,
    Failed,
}

public sealed record GlobalHotKeyRegistration(
    InputBinding Binding,
    GlobalHotKeyRegistrationState State,
    string? Reason,
    int? NativeId);

public interface IWindowsGlobalHotKeyNative
{
    bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);
    bool UnregisterHotKey(IntPtr windowHandle, int id);
}

public sealed class WindowsGlobalHotKeyHost : IAsyncDisposable, IGlobalHotKeyProfileRegistrar
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private readonly object sync = new();
    private readonly IWindowsGlobalHotKeyNative native;
    private readonly Func<IntPtr> windowHandleProvider;
    private readonly ILogger logger;
    private readonly Dictionary<int, InputBinding> registered = [];
    private int nextId = 0x4D00;

    public event EventHandler<InputBinding>? HotKeyPressed;

    public WindowsGlobalHotKeyHost(
        Func<IntPtr> windowHandleProvider,
        IWindowsGlobalHotKeyNative? native = null,
        ILogger? logger = null)
    {
        this.windowHandleProvider = windowHandleProvider ?? throw new ArgumentNullException(nameof(windowHandleProvider));
        this.native = native ?? new Win32GlobalHotKeyNative();
        this.logger = logger ?? NullLogger<WindowsGlobalHotKeyHost>.Instance;
    }

    public Task<ImmutableArray<GlobalHotKeyRegistration>> RegisterAsync(
        IReadOnlyList<InputBinding> bindings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(bindings.Select(binding => new GlobalHotKeyRegistration(
                binding, GlobalHotKeyRegistrationState.Unavailable, "Windows global hotkeys are unavailable on this platform.", null)).ToImmutableArray());
        }

        IntPtr hwnd = windowHandleProvider();
        ImmutableArray<GlobalHotKeyRegistration> snapshot;
        lock (sync)
            snapshot = RegisterLocked(bindings, hwnd, cancellationToken);
        LogRegistrationSnapshot(snapshot);
        return Task.FromResult(snapshot);
    }

    void IGlobalHotKeyProfileRegistrar.Replace(
        IReadOnlyList<InputBinding> bindings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return;

        IntPtr hwnd = windowHandleProvider();
        ImmutableArray<GlobalHotKeyRegistration> snapshot;
        int removedCount;
        lock (sync)
        {
            removedCount = registered.Count;
            foreach (int id in registered.Keys.ToArray())
                native.UnregisterHotKey(hwnd, id);
            registered.Clear();
            snapshot = RegisterLocked(bindings, hwnd, cancellationToken);
        }
        GlobalHotKeyLog.Unregistered(logger, removedCount);
        LogRegistrationSnapshot(snapshot);
    }

    public Task UnregisterAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IntPtr hwnd = windowHandleProvider();
        lock (sync)
        {
            int count = registered.Count;
            foreach (int id in registered.Keys.ToArray())
                native.UnregisterHotKey(hwnd, id);
            registered.Clear();
            GlobalHotKeyLog.Unregistered(logger, count);
        }
        return Task.CompletedTask;
    }

    /// <summary>Routes a WM_HOTKEY id received by the host window to the registered binding.</summary>
    public bool TryHandleHotKey(int nativeId)
    {
        InputBinding? binding;
        lock (sync)
            registered.TryGetValue(nativeId, out binding);
        if (binding is null)
            return false;
        HotKeyPressed?.Invoke(this, binding);
        return true;
    }

    public ValueTask DisposeAsync() => new(UnregisterAllAsync(CancellationToken.None));

    private ImmutableArray<GlobalHotKeyRegistration> RegisterLocked(
        IReadOnlyList<InputBinding> bindings,
        IntPtr hwnd,
        CancellationToken cancellationToken)
    {
        var results = ImmutableArray.CreateBuilder<GlobalHotKeyRegistration>(bindings.Count);
        foreach (InputBinding binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (binding.Scope != InputBindingScope.Global || binding.Gesture.Kind != InputGestureKind.KeyChord)
            {
                results.Add(new(binding, GlobalHotKeyRegistrationState.UnsupportedGesture, "Only global keyboard chords can be registered.", null));
                continue;
            }

            if (registered.Values.Any(existing => StringComparer.Ordinal.Equals(existing.Gesture.CanonicalText, binding.Gesture.CanonicalText)))
            {
                results.Add(new(binding, GlobalHotKeyRegistrationState.Conflict, "This global shortcut is already registered.", null));
                continue;
            }

            if (!TryGetVirtualKey(binding.Gesture.Primary!, out uint key) || !TryGetModifiers(binding.Gesture.Modifiers, out uint modifiers))
            {
                results.Add(new(binding, GlobalHotKeyRegistrationState.UnsupportedGesture, "The key is not supported by Windows RegisterHotKey.", null));
                continue;
            }

            int id = nextId++;
            if (!native.RegisterHotKey(hwnd, id, modifiers, key))
            {
                int error = Marshal.GetLastWin32Error();
                results.Add(new(binding, GlobalHotKeyRegistrationState.Failed, new Win32Exception(error).Message, null));
                continue;
            }

            registered[id] = binding;
            results.Add(new(binding, GlobalHotKeyRegistrationState.Registered, null, id));
        }
        return results.ToImmutable();
    }

    private void LogRegistrationSnapshot(ImmutableArray<GlobalHotKeyRegistration> snapshot)
    {
        foreach (GlobalHotKeyRegistration registration in snapshot.Where(static result =>
            result.State != GlobalHotKeyRegistrationState.Registered))
        {
            GlobalHotKeyLog.RegistrationUnavailable(
                logger,
                registration.Binding.ActionId,
                registration.Binding.Gesture.CanonicalText,
                registration.State,
                registration.Reason ?? string.Empty);
        }
        GlobalHotKeyLog.Registered(
            logger,
            snapshot.Count(result => result.State == GlobalHotKeyRegistrationState.Registered),
            snapshot.Count(result => result.State != GlobalHotKeyRegistrationState.Registered));
    }

    private static bool TryGetModifiers(InputModifiers value, out uint result)
    {
        result = 0;
        if ((value & InputModifiers.Control) != 0) result |= ModControl;
        if ((value & InputModifiers.Alt) != 0) result |= ModAlt;
        if ((value & InputModifiers.Shift) != 0) result |= ModShift;
        if ((value & InputModifiers.Meta) != 0) result |= ModWin;
        return (value & ~(InputModifiers.Control | InputModifiers.Alt | InputModifiers.Shift | InputModifiers.Meta)) == 0;
    }

    private static bool TryGetVirtualKey(string value, out uint result)
    {
        string key = value.Trim().ToUpperInvariant();
        if (key.Length == 1 && ((key[0] is >= 'A' and <= 'Z') || (key[0] is >= '0' and <= '9')))
        { result = key[0]; return true; }
        if (key.Length == 2 && key[0] == 'D' && key[1] is >= '0' and <= '9')
        { result = key[1]; return true; }
        if (key.Length == 7 && key.StartsWith("NUMPAD", StringComparison.Ordinal)
            && key[6] is >= '0' and <= '9')
        { result = (uint)(0x60 + key[6] - '0'); return true; }
        if (key.StartsWith('F') && int.TryParse(key[1..], out int f) && f is >= 1 and <= 24)
        { result = (uint)(0x70 + f - 1); return true; }
        result = key switch
        {
            "BACKSPACE" => 0x08,
            "TAB" => 0x09,
            "ENTER" => 0x0D,
            "ESCAPE" or "ESC" => 0x1B,
            "SPACE" => 0x20,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "END" => 0x23,
            "HOME" => 0x24,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            "MULTIPLY" => 0x6A,
            "ADD" => 0x6B,
            "SUBTRACT" => 0x6D,
            "DECIMAL" => 0x6E,
            "DIVIDE" => 0x6F,
            _ => 0,
        };
        return result != 0;
    }

    private sealed class Win32GlobalHotKeyNative : IWindowsGlobalHotKeyNative
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        bool IWindowsGlobalHotKeyNative.RegisterHotKey(IntPtr h, int id, uint m, uint k) => RegisterHotKey(h, id, m, k);
        bool IWindowsGlobalHotKeyNative.UnregisterHotKey(IntPtr h, int id) => UnregisterHotKey(h, id);
    }
}

internal static partial class GlobalHotKeyLog
{
    [LoggerMessage(6850, LogLevel.Information, "Windows global hotkeys registered: {RegisteredCount} active, {FailedCount} unavailable")]
    internal static partial void Registered(ILogger logger, int registeredCount, int failedCount);
    [LoggerMessage(6851, LogLevel.Information, "Windows global hotkeys unregistered: {Count}")]
    internal static partial void Unregistered(ILogger logger, int count);
    [LoggerMessage(
        6852,
        LogLevel.Warning,
        "Windows global hotkey unavailable for {ActionId} ({Gesture}): {State}; {Reason}")]
    internal static partial void RegistrationUnavailable(
        ILogger logger,
        string actionId,
        string gesture,
        GlobalHotKeyRegistrationState state,
        string reason);
}
