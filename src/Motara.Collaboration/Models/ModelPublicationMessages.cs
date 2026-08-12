using System.Buffers.Binary;
using System.Collections.Immutable;

namespace Motara.Collaboration.Models;

public static class ModelPublicationMessages
{
    internal const byte WithdrawalKind = 3;
    internal const byte ChunkKind = 2;
    internal const byte ManifestKind = 1;

    public static byte[] EncodeManifest(ModelPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(ManifestKind); writer.Write(manifest.ModelInstanceId.Value.ToByteArray()); writer.Write(manifest.ModelContentId.Value); writer.Write(manifest.PackageContentId.Value); writer.Write(manifest.Generation.Value); writer.Write(manifest.DisplayName); writer.Write(manifest.Files.Length);
        foreach (ModelPackageFile file in manifest.Files) { writer.Write(file.AssetId); writer.Write((int)file.Kind); writer.Write(file.Name ?? string.Empty); writer.Write(file.Group ?? string.Empty); writer.Write(file.Length); writer.Write(file.Sha256.AsSpan()); }
        return stream.ToArray();
    }

    public static ModelPackageManifest DecodeManifest(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false); using var reader = new BinaryReader(stream);
        if (reader.ReadByte() != ManifestKind) throw new ArgumentException("The manifest message is invalid.", nameof(payload));
        Guid instance = new(reader.ReadBytes(16)); ModelContentId native = ModelContentId.Parse(reader.ReadString()); PackageContentId package = PackageContentId.Parse(reader.ReadString()); ModelGeneration generation = new(reader.ReadUInt64()); string name = reader.ReadString(); int count = reader.ReadInt32();
        if (count <= 0 || count > ModelPackageLimits.Default.MaxFileCount) throw new ArgumentException("The manifest message file count is invalid.", nameof(payload));
        var files = ImmutableArray.CreateBuilder<ModelPackageFile>(count);
        for (int index = 0; index < count; index++) { string id = reader.ReadString(); ModelPackageAssetKind kind = (ModelPackageAssetKind)reader.ReadInt32(); string nameMetadata = reader.ReadString(); string groupMetadata = reader.ReadString(); long length = reader.ReadInt64(); byte[] hash = reader.ReadBytes(32); if (hash.Length != 32) throw new ArgumentException("The manifest message is truncated.", nameof(payload)); files.Add(new ModelPackageFile(id, kind, length, hash, string.IsNullOrEmpty(nameMetadata) ? null : nameMetadata, string.IsNullOrEmpty(groupMetadata) ? null : groupMetadata)); }
        if (stream.Position != stream.Length) throw new ArgumentException("The manifest message has trailing data.", nameof(payload));
        return new ModelPackageManifest(new ModelInstanceId(instance), native, package, generation, name, files.MoveToImmutable());
    }

    public static byte[] EncodeWithdrawal(ModelGeneration generation)
    {
        byte[] payload = new byte[9];
        payload[0] = WithdrawalKind;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(1), generation.Value);
        return payload;
    }

    public static ModelGeneration DecodeWithdrawal(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 9 || payload[0] != WithdrawalKind)
            throw new ArgumentException("The withdrawal message is invalid.", nameof(payload));
        return new ModelGeneration(BinaryPrimitives.ReadUInt64BigEndian(payload[1..]));
    }

    public static byte[] EncodeChunk(ModelPackageChunk chunk)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(ChunkKind); writer.Write(chunk.PackageContentId.Value); writer.Write(chunk.Generation.Value);
        writer.Write(chunk.AssetId); writer.Write(chunk.Offset); writer.Write(chunk.Data.Length); writer.Write(chunk.Data.AsSpan()); writer.Write(chunk.Sha256.AsSpan());
        return stream.ToArray();
    }

    public static ModelPackageChunk DecodeChunk(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false); using var reader = new BinaryReader(stream);
        if (reader.ReadByte() != ChunkKind) throw new ArgumentException("The chunk message is invalid.", nameof(payload));
        PackageContentId package = PackageContentId.Parse(reader.ReadString()); ModelGeneration generation = new(reader.ReadUInt64()); string asset = reader.ReadString(); long offset = reader.ReadInt64(); int length = reader.ReadInt32();
        if (length <= 0 || length > 1024 * 1024) throw new ArgumentException("The chunk message length is invalid.", nameof(payload));
        byte[] data = reader.ReadBytes(length); byte[] hash = reader.ReadBytes(32);
        if (data.Length != length || hash.Length != 32 || stream.Position != stream.Length) throw new ArgumentException("The chunk message is truncated.", nameof(payload));
        return new ModelPackageChunk(package, generation, asset, offset, data, hash);
    }
}
