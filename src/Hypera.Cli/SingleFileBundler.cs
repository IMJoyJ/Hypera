using Hypera.VirtualFs;

namespace Hypera.Cli;

/// <summary>
/// CLI facade over <see cref="SingleFileBundleCodec"/> for building and detecting
/// single-file executables using the 24-byte <c>HYPRBNDL</c> trailer.
/// </summary>
public static class SingleFileBundler
{
    public const int TrailerSizeInBytes = SingleFileBundleCodec.TrailerSizeInBytes;
    public static ReadOnlySpan<byte> TrailerMagic => SingleFileBundleCodec.TrailerMagic;

    public static BundleTrailer PackBundle(
        string sourceHebOrHepPath,
        string hostTemplatePath,
        string outputExecutablePath) =>
        SingleFileBundleCodec.PackBundle(sourceHebOrHepPath, hostTemplatePath, outputExecutablePath);

    public static bool TryDetectTrailer(string? executablePath, out BundleTrailer trailer) =>
        SingleFileBundleCodec.TryDetectTrailer(executablePath, out trailer);

    public static bool TryDetectTrailer(Stream stream, out BundleTrailer trailer) =>
        SingleFileBundleCodec.TryDetectTrailer(stream, out trailer);

    public static Stream OpenPayloadStream(string executablePath, BundleTrailer trailer) =>
        SingleFileBundleCodec.OpenPayloadStream(executablePath, trailer);

    public static MemoryStream ExtractPayloadToMemory(string executablePath, BundleTrailer trailer) =>
        SingleFileBundleCodec.ExtractPayloadToMemory(executablePath, trailer);

    public static BundlePayloadKind DetectPayloadKind(ReadOnlySpan<byte> headerBytes) =>
        SingleFileBundleCodec.DetectPayloadKind(headerBytes);
}
