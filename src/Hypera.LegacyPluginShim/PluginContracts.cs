using System.Reflection;
using System.Runtime.Loader;

namespace MinorShift.Emuera.Runtime.Utils.PluginSystem;

public abstract class PluginManifestAbstract
{
    public abstract string PluginName { get; }
    public abstract string PluginDescription { get; }
    public abstract string PluginVersion { get; }
    public abstract string PluginAuthor { get; }

    protected PluginManifestAbstract()
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
/// Redirects assembly references to "Emuera" straight to this binary-compatible shim assembly.
/// </summary>
public sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string pluginMainAssemblyPath)
        : base(name: $"HyperaPlugin:{Path.GetFileNameWithoutExtension(pluginMainAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginMainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, "Emuera", StringComparison.OrdinalIgnoreCase))
        {
            return typeof(PluginManager).Assembly;
        }

        string? dependencyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return dependencyPath is not null ? LoadFromAssemblyPath(dependencyPath) : null;
    }
}
