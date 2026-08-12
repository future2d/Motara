using System.Reflection;
using System.Runtime.InteropServices;

namespace Motara.ModelRuntime.PurismCore;

internal static class NativeLibraryResolver
{
    private const string LibraryName = "PurismCore";

    internal static void Register()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
        {
            return 0;
        }

        string? configuredPath = Environment.GetEnvironmentVariable("MOTARA_PURISMCORE_NATIVE_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath)
            && NativeLibrary.TryLoad(Path.GetFullPath(configuredPath), out nint configuredHandle))
        {
            return configuredHandle;
        }

        string applicationPath = Path.Combine(AppContext.BaseDirectory, LibraryName + ".dll");
        return NativeLibrary.TryLoad(applicationPath, out nint applicationHandle)
            ? applicationHandle
            : 0;
    }
}
