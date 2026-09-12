using System.Runtime.InteropServices;

namespace Contracts;

public static class RuntimeAssets
{
    public static string RuntimeIdentifier => GetRuntimeIdentifier(RuntimeInformation.ProcessArchitecture);

    public static string GetRuntimeIdentifier(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => throw new PlatformNotSupportedException($"Unsupported process architecture: {architecture}.")
    };

    public static string NativeDirectory(string root) => Path.Combine(root, "runtimes", RuntimeIdentifier, "native");
}