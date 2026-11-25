using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RxSuite.Application.Abstractions;

namespace RxSuite.Infrastructure.Services;

#region Options

/// <summary>Where prescription images are kept and the key that encrypts them (section "PrescriptionImages").</summary>
public sealed class PrescriptionImageOptions
{
    public const string SectionName = "PrescriptionImages";

    /// <summary>Folder for the encrypted files. Relative paths start at the application's working folder.</summary>
    public string Path { get; init; } = "data/prescription-images";

    /// <summary>
    /// A 32-byte key, Base64-encoded. Keep it out of source control (user secrets, environment
    /// variable or a key vault). Losing it makes every stored image unreadable.
    /// </summary>
    public string Key { get; init; } = string.Empty;
}

#endregion

#region Store

/// <summary>
/// ADAPTER for <see cref="IPrescriptionImageStore"/>: each image is one file encrypted with AES-GCM
/// (BR-RX-3). The tenant Id is authenticated with the file, so a file copied into another tenant's
/// folder, or changed in any way, fails to decrypt instead of being shown.
/// File layout: 12-byte nonce, 16-byte tag, then the encrypted bytes.
/// </summary>
internal sealed partial class EncryptedFilePrescriptionImageStore(PrescriptionImageOptions options) : IPrescriptionImageStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public async Task<string> SaveAsync(int tenantId, byte[] content, CancellationToken cancellationToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[content.Length];

        using (var aes = new AesGcm(Key(), TagSize))
            aes.Encrypt(nonce, content, cipher, tag, TenantData(tenantId));

        var storageKey = $"{tenantId.ToString(CultureInfo.InvariantCulture)}/{Guid.NewGuid():N}.bin";
        var path = FullPath(storageKey);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        await File.WriteAllBytesAsync(path, [.. nonce, .. tag, .. cipher], cancellationToken);
        return storageKey;
    }

    public async Task<byte[]> ReadAsync(int tenantId, string storageKey, CancellationToken cancellationToken)
    {
        // Only keys this store produced, for this tenant: no path tricks such as "../".
        if (!StorageKeyPattern().IsMatch(storageKey) || !storageKey.StartsWith($"{tenantId.ToString(CultureInfo.InvariantCulture)}/", StringComparison.Ordinal))
            throw new InvalidOperationException("The image key does not belong to this tenant.");

        var file = await File.ReadAllBytesAsync(FullPath(storageKey), cancellationToken);
        if (file.Length < NonceSize + TagSize) throw new CryptographicException("The image file is damaged.");

        var plain = new byte[file.Length - NonceSize - TagSize];
        using var aes = new AesGcm(Key(), TagSize);
        aes.Decrypt(file.AsSpan(0, NonceSize), file.AsSpan(NonceSize + TagSize), file.AsSpan(NonceSize, TagSize), plain,
            TenantData(tenantId));
        return plain;
    }

    #region Helpers

    private byte[] Key()
    {
        try
        {
            var key = Convert.FromBase64String(options.Key);
            if (key.Length == 32) return key;
        }
        catch (FormatException) { }

        throw new InvalidOperationException(
            $"{PrescriptionImageOptions.SectionName}:Key must be a Base64-encoded 32-byte key. Set it with user secrets or an environment variable.");
    }

    private string FullPath(string storageKey) =>
        System.IO.Path.Combine(System.IO.Path.GetFullPath(options.Path), storageKey.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static byte[] TenantData(int tenantId) => BitConverter.GetBytes(tenantId);

    [GeneratedRegex("^[0-9]+/[0-9a-f]{32}\\.bin$")]
    private static partial Regex StorageKeyPattern();

    #endregion
}

#endregion
