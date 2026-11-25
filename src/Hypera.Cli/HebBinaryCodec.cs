using Hypera.Bytecode;
using Hypera.Persistence;

namespace Hypera.Cli;

/// <summary>
/// Thin CLI facade over <see cref="BytecodeSerializer"/> ensuring 100% binary-identical <c>.heb</c> output with <c>Hypera.Core</c>.
/// </summary>
public static class HebBinaryCodec
{
    public static void Write(Stream destination, BytecodeModule module) =>
        BytecodeSerializer.Serialize(module, destination);

    public static BytecodeModule Read(Stream source) =>
        BytecodeSerializer.Deserialize(source);
}
