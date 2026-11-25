using Hypera.VirtualFs;

namespace Hypera.Audio;

/// <summary>
/// Resolves audio assets inside a VFS <c>sound/</c> hierarchy across .wav, .mp3, .ogg, .flac, and .m4a
/// (case-insensitive) and copies stream contents into unlocked memory buffers so runtime files are never locked.
/// </summary>
public static class SoundAssetResolver
{
    public static readonly string[] SupportedExtensions = [".wav", ".mp3", ".ogg", ".flac", ".m4a"];

    public static bool IsSupportedFormat(AudioContainerFormat format) =>
        format is AudioContainerFormat.Wav or AudioContainerFormat.Mp3 or AudioContainerFormat.Ogg
            or AudioContainerFormat.Flac or AudioContainerFormat.M4a;

    public static bool TryResolveSoundPath(IVirtualFileSystem vfs, string? pathOrName, out string resolvedVirtualPath)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        resolvedVirtualPath = string.Empty;

        if (string.IsNullOrWhiteSpace(pathOrName))
        {
            return false;
        }

        string normalized = VfsPathNormalizer.Normalize(pathOrName);
        if (normalized.Length == 0)
        {
            return false;
        }

        string ext = VfsPathNormalizer.GetExtension(normalized);
        bool hasExtension = ext.Length > 0;
        if (hasExtension && !IsSupportedFormat(NullAudioBackend.DetectFormatFromPath(normalized)))
        {
            return false;
        }

        string soundPrefixed = normalized.StartsWith("sound/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : VfsPathNormalizer.Combine("sound", normalized);

        if (hasExtension)
        {
            if (vfs.FileExists(soundPrefixed))
            {
                resolvedVirtualPath = soundPrefixed;
                return true;
            }

            if (!string.Equals(soundPrefixed, normalized, StringComparison.OrdinalIgnoreCase) && vfs.FileExists(normalized))
            {
                resolvedVirtualPath = normalized;
                return true;
            }

            return false;
        }

        foreach (string candidateExt in SupportedExtensions)
        {
            string candidateInSound = soundPrefixed + candidateExt;
            if (vfs.FileExists(candidateInSound))
            {
                resolvedVirtualPath = candidateInSound;
                return true;
            }
        }

        if (!string.Equals(soundPrefixed, normalized, StringComparison.OrdinalIgnoreCase))
        {
            foreach (string candidateExt in SupportedExtensions)
            {
                string candidateDirect = normalized + candidateExt;
                if (vfs.FileExists(candidateDirect))
                {
                    resolvedVirtualPath = candidateDirect;
                    return true;
                }
            }
        }

        return false;
    }

    public static bool ExistSound(IVirtualFileSystem vfs, string? pathOrName) =>
        TryResolveSoundPath(vfs, pathOrName, out _);

    /// <summary>
    /// Reads the entire asset into an unlocked byte array and closes the underlying VFS stream immediately.
    /// </summary>
    public static byte[] ReadUnlockedBytes(IVirtualFileSystem vfs, string virtualPath)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        using Stream stream = vfs.OpenRead(virtualPath);
        if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> seg))
        {
            return seg.ToArray();
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Returns an independent read-only <see cref="MemoryStream"/> backed by an unlocked byte copy.
    /// </summary>
    public static MemoryStream OpenUnlockedMemoryStream(IVirtualFileSystem vfs, string virtualPath) =>
        new(ReadUnlockedBytes(vfs, virtualPath), writable: false);
}
