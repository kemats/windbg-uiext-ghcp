using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Contracts;

namespace WinDbgChatView;

internal sealed class UiAssemblyLoadContext(string path) : AssemblyLoadContext("WinDbgCopilotUI", false)
{
    private readonly AssemblyDependencyResolver _resolver = new(path);
    private readonly string _directory = Path.GetDirectoryName(path)!;

    protected override Assembly? Load(AssemblyName name)
    {
        // Objects crossing the host boundary must retain the host's contract and WPF type identities.
        if (name.Name == typeof(IChatRuntime).Assembly.GetName().Name) return typeof(IChatRuntime).Assembly;
        if (name.Name is "WindowsBase" or "PresentationCore" or "PresentationFramework" or "System.Xaml"
            || name.Name?.StartsWith("DbgX", StringComparison.Ordinal) == true
            || name.Name?.StartsWith("WindowsDebugger", StringComparison.Ordinal) == true)
            return Default.LoadFromAssemblyName(name);
        var resolved = _resolver.ResolveAssemblyToPath(name);
        if (resolved is not null) return LoadFromAssemblyPath(resolved);
        var local = Path.Combine(_directory, name.Name + ".dll");
        return File.Exists(local) ? LoadFromAssemblyPath(local) : null;
    }

    protected override nint LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}