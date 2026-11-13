using System.IO;
using System.Runtime.InteropServices;

namespace UnityPackageExplorer.PreviewHandler;

/// <summary>Loads assimp.dll by explicit absolute path so P/Invoke probing cannot miss it in surrogate hosts.</summary>
internal static class AssimpNativeLoader
{
    private const string NativeName = "assimp.dll";
    private static int _state;

    /// <summary>Idempotent. Throws with the underlying loader failure when the native library cannot be loaded.</summary>
    public static void EnsureLoaded()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) == 1) return;
        try
        {
            // AppContext.BaseDirectory points at the surrogate host (prevhost.exe) directory,
            // so resolve the DLL next to this managed assembly instead.
            var assemblyDir = Path.GetDirectoryName(typeof(AssimpNativeLoader).Assembly.Location);
            var candidates = new[] { assemblyDir, AppContext.BaseDirectory }
                .Where(static dir => !string.IsNullOrEmpty(dir))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in candidates)
            {
                var explicitPath = Path.Combine(dir!, NativeName);
                if (!File.Exists(explicitPath)) continue;
                NativeLibrary.Load(explicitPath);
                return;
            }
            // Normal probing still applies when the layout does not carry the file next to the host.
            NativeLibrary.Load(NativeName);
        }
        catch
        {
            Volatile.Write(ref _state, 0);
            throw;
        }
    }
}
