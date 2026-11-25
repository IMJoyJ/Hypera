using System.Reflection;
using System.Runtime.Loader;

namespace MinorShift.Emuera.Runtime.Utils.PluginSystem;

public abstract class PluginManifestAbstract
{
    public abstract string PluginName { get; }
    public abstract string PluginDescription { get; }
    public abstract string PluginVersion { get; }
    public abstract string PluginAuthor { get; }

    public PluginManifestAbstract()
    {
    }

    public List<IPluginMethod> GetPluginMethods() => methods;

    protected List<IPluginMethod> methods = [];
}

public interface IPluginMethod
{
    string Name { get; }
    string Description { get; }
    void Execute(PluginMethodParameter[] args);
}

public class PluginMethodParameter
{
    public PluginMethodParameter(string initialValue)
    {
        isString = true;
        strValue = initialValue;
        intValue = 0;
    }

    public PluginMethodParameter(long initialValue)
    {
        isString = false;
        strValue = string.Empty;
        intValue = initialValue;
    }

    public bool isString;
    public string strValue;
    public long intValue;
}

/// <summary>
/// Isolated collectible <see cref="AssemblyLoadContext"/> for legacy Emuera Plugins/*.dll assemblies.
/// Redirects assembly references to "Emuera" and "Hypera.Core" straight to the host's loaded assemblies,
/// and loads plugin binaries via unlocked memory streams so runtime DLLs are never file-locked.
/// </summary>
public sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginMainAssemblyPath)
        : base(name: $"HyperaPlugin:{Path.GetFileNameWithoutExtension(pluginMainAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginMainAssemblyPath);
    }

    public Assembly LoadAssemblyUnlocked(string assemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        byte[] assemblyBytes = File.ReadAllBytes(assemblyPath);
        using var peStream = new MemoryStream(assemblyBytes, writable: false);

        string pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (File.Exists(pdbPath))
        {
            byte[] pdbBytes = File.ReadAllBytes(pdbPath);
            using var pdbStream = new MemoryStream(pdbBytes, writable: false);
            return LoadFromStream(peStream, pdbStream);
        }

        return LoadFromStream(peStream);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, "Emuera", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assemblyName.Name, "Hypera.LegacyPluginShim", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(PluginManager).Assembly;
        }

        if (string.Equals(assemblyName.Name, "Hypera.Core", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(Hypera.Runtime.EmueraEngine).Assembly;
        }

        string? dependencyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return dependencyPath is not null ? LoadAssemblyUnlocked(dependencyPath) : null;
    }
}
