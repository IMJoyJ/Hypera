using Hypera.Bytecode;
using Hypera.Persistence;
using Hypera.VirtualFs;

namespace Hypera.Cli;

/// <summary>
/// Thin CLI facade over <see cref="HepPackageWriter"/> and <see cref="HepArchiveVfs"/>
/// ensuring 100% binary-identical <c>.hep</c> packages with <c>Hypera.Core</c>.
/// </summary>
public static class HepPackageCodec
{
    public const string EmbeddedModuleVirtualPath = HepPackageWriter.DefaultPrebuiltBytecodePath;

    public static void PackDirectory(
        string sourceDirectory,
        string outputHepPath,
        bool compress = true,
        BytecodeModule? prebuiltModule = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputHepPath);

        ReadOnlyMemory<byte>? prebuiltBytes = prebuiltModule is not null
            ? BytecodeSerializer.Serialize(prebuiltModule)
            : null;

        HepPackageWriter.PackDirectory(
            sourceDirectory,
            outputHepPath,
            prebuiltBytecode: prebuiltBytes,
            prebuiltBytecodeVirtualPath: EmbeddedModuleVirtualPath,
            compress: compress);
    }

    public static void WriteArchive(
        Stream destination,
        IReadOnlyList<(string VirtualPath, byte[] RawBytes)> entries,
        bool compress = true,
        bool includesPrebuiltBytecode = false)
    {
        HepPackageWriter.PackEntries(
            entries,
            destination,
            compress: compress,
            includesPrebuiltBytecode: includesPrebuiltBytecode);
    }

    public static HepArchiveVfs OpenArchiveFile(string hepFilePath) => new(hepFilePath);

    public static HepArchiveVfs OpenArchiveStream(Stream stream, string rootDescription, bool leaveOpen = false) =>
        new(stream, rootDescription, leaveOpen);
}
