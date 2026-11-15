using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HmolPack;

/// <summary>头部 JSON。字段顺序即序列化顺序，AAD 依赖完全一致的字节。</summary>
internal sealed class HmolHeader
{
    [JsonPropertyName("format")] public string Format { get; set; } = "HMOLPKG";
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("package")] public string Package { get; set; } = string.Empty;
    [JsonPropertyName("packageVersion")] public string PackageVersion { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = string.Empty;
    [JsonPropertyName("payload")] public PayloadInfo Payload { get; set; } = new();
    [JsonPropertyName("crypto")] public CryptoInfo Crypto { get; set; } = new();
}

internal sealed class PayloadInfo
{
    [JsonPropertyName("type")] public string Type { get; set; } = "zip";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    [JsonPropertyName("entryCount")] public int EntryCount { get; set; }
}

internal sealed class CryptoInfo
{
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "AES-256-GCM";
    [JsonPropertyName("kdf")] public string Kdf { get; set; } = "HKDF-SHA256";
    [JsonPropertyName("salt")] public string Salt { get; set; } = string.Empty;
    [JsonPropertyName("iv")] public string Iv { get; set; } = string.Empty;
    [JsonPropertyName("tagLength")] public int TagLength { get; set; } = 16;
    [JsonPropertyName("info")] public string Info { get; set; } = "HMOLPKG/v1/payload";
}

internal static class Program
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HMOLPKG\0");
    private const ushort FormatVersion = 1;
    private const int TagLength = 16;
    private const string KdfInfo = "HMOLPKG/v1/payload";
    private const string MasterKeyHex = "8E2B823836FF0A1257DA8BFE6D68585078127B1FB99E933F41A0A655901049D3";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0) return Usage();

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "pack":   return PackCommand(args);
                case "unpack": return UnpackCommand(args);
                case "verify": return VerifyCommand(args);
                case "vector": return VectorCommand(args);
                default:       return Usage();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            HmolPack - .HMOL package tool (format v1)

              HmolPack pack   <input.zip> <output.hmol> [--name N] [--version V] [--title T]
              HmolPack unpack <input.hmol> <output.zip>
              HmolPack verify <input.hmol>
              HmolPack vector [output-directory]

            """);
        return 2;
    }

    private static int PackCommand(string[] args)
    {
        if (args.Length < 3) return Usage();

        var zipPath = args[1];
        var outPath = args[2];
        var name = GetOption(args, "--name") ?? Path.GetFileNameWithoutExtension(zipPath);
        var version = GetOption(args, "--version") ?? "0.0.0";
        var title = GetOption(args, "--title");

        if (!File.Exists(zipPath))
        {
            Console.Error.WriteLine("ERROR: input not found: " + zipPath);
            return 1;
        }

        var plain = File.ReadAllBytes(zipPath);
        var entryCount = CountEntries(zipPath);

        using var rng = RandomNumberGenerator.Create();
        var salt = new byte[32];
        var iv = new byte[12];
        rng.GetBytes(salt);
        rng.GetBytes(iv);

        var header = new HmolHeader
        {
            Package = name,
            PackageVersion = version,
            Title = title,
            CreatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz"),
            Payload = new PayloadInfo
            {
                Type = "zip",
                Size = plain.Length,
                Sha256 = Sha256Hex(plain),
                EntryCount = entryCount
            },
            Crypto = new CryptoInfo
            {
                Salt = Convert.ToBase64String(salt),
                Iv = Convert.ToBase64String(iv),
                TagLength = TagLength,
                Info = KdfInfo
            }
        };

        var container = BuildContainer(plain, header, salt, iv);
        File.WriteAllBytes(outPath, container);

        Console.WriteLine($"PACK OK  {outPath}");
        Console.WriteLine($"  package={name} version={version} entries={entryCount}");
        Console.WriteLine($"  payload={plain.Length} bytes sha256={header.Payload.Sha256}");
        Console.WriteLine($"  container={container.Length} bytes");
        return 0;
    }

    private static int UnpackCommand(string[] args)
    {
        if (args.Length < 3) return Usage();

        var container = ReadAndDecrypt(args[1], out var header);
        File.WriteAllBytes(args[2], container);

        Console.WriteLine($"UNPACK OK  {args[2]}");
        Console.WriteLine($"  payload={container.Length} bytes sha256={header.Payload.Sha256}");
        return 0;
    }

    private static int VerifyCommand(string[] args)
    {
        if (args.Length < 2) return Usage();

        var container = ReadAndDecrypt(args[1], out var header);

        Console.WriteLine("VERIFY OK");
        Console.WriteLine($"  format={header.Format} version={header.Version} flags=0");
        Console.WriteLine($"  package={header.Package} packageVersion={header.PackageVersion}");
        Console.WriteLine($"  title={header.Title}");
        Console.WriteLine($"  createdAt={header.CreatedAt}");
        Console.WriteLine($"  payload type={header.Payload.Type} size={header.Payload.Size} entries={header.Payload.EntryCount}");
        Console.WriteLine($"  payload sha256={header.Payload.Sha256}");
        Console.WriteLine($"  decrypted={container.Length} bytes");
        return 0;
    }

    /// <summary>生成一组固定 salt/iv 的测试向量，供 HMOL 侧核对实现。</summary>
    private static int VectorCommand(string[] args)
    {
        var outDir = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);

        var payload = Encoding.UTF8.GetBytes("HMOL test vector v1\n");
        var salt = new byte[32];
        var iv = new byte[12];
        for (var i = 0; i < salt.Length; i++) salt[i] = (byte)i;
        for (var i = 0; i < iv.Length; i++) iv[i] = (byte)i;

        var header = new HmolHeader
        {
            Package = "HMOL-TEST-VECTOR",
            PackageVersion = "1.0.0",
            Title = "HMOL format test vector",
            CreatedAt = "2026-11-07T00:00:00+08:00",
            Payload = new PayloadInfo
            {
                Type = "raw",
                Size = payload.Length,
                Sha256 = Sha256Hex(payload),
                EntryCount = 0
            },
            Crypto = new CryptoInfo
            {
                Salt = Convert.ToBase64String(salt),
                Iv = Convert.ToBase64String(iv),
                TagLength = TagLength,
                Info = KdfInfo
            }
        };

        var container = BuildContainer(payload, header, salt, iv);
        var vectorFile = Path.Combine(outDir, "HMOL-测试向量.hmol");
        File.WriteAllBytes(vectorFile, container);

        Console.WriteLine("MASTER KEY HEX = " + MasterKeyHex);
        Console.WriteLine("DERIVED KEY HEX = " + Convert.ToHexString(DeriveKey(salt)));
        Console.WriteLine("SALT HEX = " + Convert.ToHexString(salt));
        Console.WriteLine("IV HEX = " + Convert.ToHexString(iv));
        Console.WriteLine("PAYLOAD TEXT = HMOL test vector v1\\n");
        Console.WriteLine("PAYLOAD SHA256 = " + header.Payload.Sha256);
        Console.WriteLine("HEADER JSON = " + Encoding.UTF8.GetString(GetHeaderBytes(container)));
        Console.WriteLine("CIPHERTEXT HEX = " + Convert.ToHexString(GetCipherPart(container)));
        Console.WriteLine("TAG HEX = " + Convert.ToHexString(GetTagPart(container)));
        Console.WriteLine("CONTAINER BASE64 = " + Convert.ToBase64String(container));
        Console.WriteLine("VECTOR FILE = " + vectorFile);
        return 0;
    }

    // ---------------- 格式核心 ----------------

    private static byte[] BuildContainer(byte[] plain, HmolHeader header, byte[] salt, byte[] iv)
    {
        var headerBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header));

        var key = DeriveKey(salt);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLength];

        using (var aes = new AesGcm(key, TagLength))
        {
            aes.Encrypt(iv, plain, cipher, tag, headerBytes);
        }

        using var output = new MemoryStream();
        output.Write(Magic);
        output.Write(BitConverter.GetBytes(FormatVersion));
        output.Write(BitConverter.GetBytes((ushort)0));
        output.Write(BitConverter.GetBytes((uint)headerBytes.Length));
        output.Write(headerBytes);
        output.Write(cipher);
        output.Write(tag);
        return output.ToArray();
    }

    private static byte[] ReadAndDecrypt(string path, out HmolHeader header)
    {
        if (!File.Exists(path)) throw new IOException("file not found: " + path);

        var raw = File.ReadAllBytes(path);
        var headerBytes = GetHeaderBytes(raw);
        header = JsonSerializer.Deserialize<HmolHeader>(headerBytes)
                 ?? throw new InvalidDataException("header is not valid JSON");

        if (!string.Equals(header.Format, "HMOLPKG", StringComparison.Ordinal))
            throw new InvalidDataException("header.format is not HMOLPKG");
        if (header.Version != FormatVersion)
            throw new InvalidDataException("unsupported format version: " + header.Version);
        if (!string.Equals(header.Crypto.Algorithm, "AES-256-GCM", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("unsupported algorithm: " + header.Crypto.Algorithm);
        if (header.Crypto.TagLength != TagLength)
            throw new InvalidDataException("tagLength must be 16");

        var salt = Convert.FromBase64String(header.Crypto.Salt);
        var iv = Convert.FromBase64String(header.Crypto.Iv);
        var key = DeriveKey(salt);

        var cipher = GetCipherPart(raw, header.Payload.Size);
        var tag = GetTagPart(raw);
        var plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(iv, cipher, tag, plain, headerBytes);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("authentication failed (header/payload was modified, or key mismatch)");
        }

        if (plain.Length != header.Payload.Size)
            throw new InvalidDataException("payload size mismatch");
        if (!string.Equals(Sha256Hex(plain), header.Payload.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("payload sha256 mismatch");

        return plain;
    }

    private static byte[] DeriveKey(byte[] salt)
    {
        var master = Convert.FromHexString(MasterKeyHex);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt, Encoding.UTF8.GetBytes(KdfInfo));
    }

    private static byte[] GetHeaderBytes(byte[] raw)
    {
        if (raw.Length < 16) throw new InvalidDataException("file too short");

        for (var i = 0; i < Magic.Length; i++)
            if (raw[i] != Magic[i]) throw new InvalidDataException("bad magic (not a .hmol file)");

        var version = BitConverter.ToUInt16(raw, 8);
        if (version != FormatVersion) throw new InvalidDataException("unsupported container version: " + version);

        var headerLength = BitConverter.ToUInt32(raw, 12);
        if (headerLength < 2 || headerLength > 1024 * 1024) throw new InvalidDataException("bad header length");
        if (raw.Length < 16 + (int)headerLength + TagLength) throw new InvalidDataException("truncated file");

        return raw.AsSpan(16, (int)headerLength).ToArray();
    }

    private static byte[] GetCipherPart(byte[] raw) => GetCipherPart(raw, -1);

    private static byte[] GetCipherPart(byte[] raw, long payloadSize)
    {
        var headerLength = (int)BitConverter.ToUInt32(raw, 12);
        var start = 16 + headerLength;
        var length = raw.Length - start - TagLength;

        if (payloadSize >= 0 && length != payloadSize)
            throw new InvalidDataException($"payload length mismatch: header={payloadSize}, actual={length}");

        return raw.AsSpan(start, length).ToArray();
    }

    private static byte[] GetTagPart(byte[] raw)
    {
        if (raw.Length < TagLength) throw new InvalidDataException("file too short");
        return raw.AsSpan(raw.Length - TagLength, TagLength).ToArray();
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static int CountEntries(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Count;
        }
        catch
        {
            // 不是 zip 也能打进容器，只是 entryCount 记 0
            return 0;
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
