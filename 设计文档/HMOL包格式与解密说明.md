# .HMOL 包格式与解密说明（v1）

面向 HMOL 启动器的对接文档。看完本文即可用 C# 实现 `.hmol` 的识别、解密、校验与安装。

| 项 | 值 |
|---|---|
| 格式名 | `HMOLPKG` |
| 格式版本 | `1` |
| 容器扩展名 | `.hmol` |
| 加密 | AES-256-GCM（认证加密） |
| 密钥派生 | HKDF-SHA256 |
| 载荷 | 普通 zip（内含补丁文件） |
| 文档日期 | 2026-11-07 |

---

## 1. 设计目标

- HMOL 能一眼识别：文件头魔数固定，不依赖扩展名。
- 载荷加密：防止玩家随手解压、替换或改包。
- 防篡改：GCM 认证标签覆盖「明文头部 + 加密载荷」，头部改一个字节就解不开。
- 头部明文：HMOL 不解密也能读出包名、版本、发布时间、载荷大小与 SHA-256，便于列表展示和断点续传。
- 解密后就是标准 zip，可直接复用 HMOL 现有的解压 / 安装 / 回滚链路。

> 安全边界：主密钥随本文下发，属于「共享密钥」，只提供防误改、防随手解包，不等同于强保密。若要真正保密，需要改为非对称方案（见第 10 节）。

---

## 2. 文件字节布局

全部多字节整数为 **小端序（Little-Endian）**。

| 偏移 | 长度 | 字段 | 说明 |
|---|---|---|---|
| 0 | 8 | `magic` | ASCII `HMOLPKG` + `0x00`，即字节 `48 4D 4F 4C 50 4B 47 00` |
| 8 | 2 | `formatVersion` | `uint16`，当前固定 `1` |
| 10 | 2 | `flags` | `uint16`，保留，当前固定 `0`；读到非 0 应拒绝 |
| 12 | 4 | `headerLength` | `uint32`，头部 JSON 的字节数，上限 1 MB |
| 16 | `headerLength` | `headerJson` | UTF-8、无 BOM、**紧凑格式**的 JSON（见第 3 节） |
| 16 + headerLength | N | `ciphertext` | AES-256-GCM 加密的载荷，N = `payload.size` |
| 文件末尾 | 16 | `tag` | GCM 认证标签，固定 16 字节，追加在密文之后 |

文件总长度必须等于：

```
16 + headerLength + payload.size + 16
```

---

## 3. 头部 JSON

### 3.1 实际样例

下面是 `patch.HMOL` 的真实头部（原样，未格式化；`\uXXXX` 是 JSON 标准转义，解析后就是中文）：

```json
{"format":"HMOLPKG","version":1,"package":"MO-\u8865\u4E011\u002B2-\u76F4\u63A5\u8986\u76D6","packageVersion":"0.6.0","title":"MO \u5C40\u57DF\u7F51\u8865\u4E01 1\u002B2\uFF08\u76F4\u63A5\u8986\u76D6\uFF09","createdAt":"2026-11-15T20:00:10\u002B08:00","payload":{"type":"zip","size":1417222,"sha256":"4750EBEBE034C80E6CABAB7CD27D32C085B7EDF1CCD5CA5B3A5F795220EA2237","entryCount":11},"crypto":{"algorithm":"AES-256-GCM","kdf":"HKDF-SHA256","salt":"iH7bCQsjWqGWb0vNIbsuWM3Pqy4os6f1XJh8wx/Vryo=","iv":"AHZ1NNeNLuupX0HD","tagLength":16,"info":"HMOLPKG/v1/payload"}}
```

### 3.2 字段表

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `format` | string | 是 | 固定 `"HMOLPKG"` |
| `version` | number | 是 | 格式版本，当前 `1` |
| `package` | string | 是 | 包标识（稳定名，不含扩展名） |
| `packageVersion` | string | 是 | 包自身的版本号，如 `0.4.14` |
| `title` | string | 否 | 展示名，界面可直接显示 |
| `createdAt` | string | 是 | ISO-8601 带时区，如 `2026-11-07T21:51:01+08:00` |
| `payload.type` | string | 是 | 当前为 `"zip"`；测试向量用 `"raw"` |
| `payload.size` | number | 是 | 解密后明文字节数 |
| `payload.sha256` | string | 是 | 解密后明文的 SHA-256（大写十六进制） |
| `payload.entryCount` | number | 否 | zip 条目数，预览用 |
| `crypto.algorithm` | string | 是 | 固定 `"AES-256-GCM"` |
| `crypto.kdf` | string | 是 | 固定 `"HKDF-SHA256"` |
| `crypto.salt` | string | 是 | base64，32 字节随机盐（每个包不同） |
| `crypto.iv` | string | 是 | base64，12 字节 GCM nonce（每个包不同，绝不复用） |
| `crypto.tagLength` | number | 是 | 固定 `16` |
| `crypto.info` | string | 是 | HKDF 的 info 参数，固定 `"HMOLPKG/v1/payload"` |

---

## 4. 密钥与派生

### 4.1 共享主密钥（32 字节）

```
8E2B823836FF0A1257DA8BFE6D68585078127B1FB99E933F41A0A655901049D3
```

### 4.2 派生规则

```
aesKey = HKDF-SHA256(
             ikm  = 主密钥（32 字节）,
             salt = crypto.salt（32 字节，本包独立）,
             info = "HMOLPKG/v1/payload",
             len  = 32 )
```

- 每个包使用独立 `salt` 与 `iv`，因此同一份明文每次打包的密文都不同。
- `iv`（GCM nonce）必须 12 字节；同一 `aesKey` 下不可复用。
- 不需要额外的 HMAC：GCM 的 tag 已经完成完整性校验。

### 4.3 AAD（附加认证数据）

```
AAD = headerJson 的原始字节（偏移 16 开始、长度 headerLength，原样）
```

**关键**：AAD 必须是文件里存的那段原始字节。不要用 JSON 对象重新序列化后的结果 —— 重新序列化会改变转义/空格/字段顺序，导致 tag 校验失败。

---

## 5. 解密步骤

1. 读文件头 16 字节：校验 magic、`formatVersion == 1`、`flags == 0`、`headerLength` 合理。
2. 按 `headerLength` 取出 `headerJson` **原始字节**，保存好（后面就是 AAD）。
3. 解析 JSON；校验 `format == "HMOLPKG"`、`version == 1`、`algorithm == "AES-256-GCM"`、`tagLength == 16`。
4. 校验文件长度：`16 + headerLength + payload.size + 16`，否则判为截断。
5. base64 解出 `salt`（必须 32 字节）与 `iv`（必须 12 字节）。
6. 按第 4 节派生 `aesKey`。
7. 取密文（`payload.size` 字节）与 tag（末尾 16 字节），AES-256-GCM 解密，AAD 用第 2 步的原始头部字节。
8. 解密失败（tag 不匹配）→ 报「包已损坏或被修改」，不要继续。
9. 用 `payload.size` 与 `payload.sha256` 再校验一次明文。
10. 得到明文 zip，交给现有解压 / 安装流程。

校验顺序建议：先做第 8 步的认证，再信任头部里的任何元数据去落盘；头部虽然明文，但在认证通过前都当作不可信输入。

---

## 6. C# 参考实现（.NET 8）

可直接放进 `HMOL.Core/Packages/HmolReader.cs`。只依赖 BCL，无第三方包。

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HMOL.Core.Packages;

public sealed class HmolHeader
{
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("package")] public string Package { get; set; } = "";
    [JsonPropertyName("packageVersion")] public string PackageVersion { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("createdAt")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("payload")] public HmolPayload Payload { get; set; } = new();
    [JsonPropertyName("crypto")] public HmolCrypto Crypto { get; set; } = new();
}

public sealed class HmolPayload
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("entryCount")] public int EntryCount { get; set; }
}

public sealed class HmolCrypto
{
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "";
    [JsonPropertyName("kdf")] public string Kdf { get; set; } = "";
    [JsonPropertyName("salt")] public string Salt { get; set; } = "";
    [JsonPropertyName("iv")] public string Iv { get; set; } = "";
    [JsonPropertyName("tagLength")] public int TagLength { get; set; }
    [JsonPropertyName("info")] public string Info { get; set; } = "";
}

public static class HmolReader
{
    private const int TagLength = 16;
    private const ushort FormatVersion = 1;
    private const string KdfInfo = "HMOLPKG/v1/payload";

    // 共享主密钥：见文档第 4.1 节
    private static readonly byte[] MasterKey =
        Convert.FromHexString("8E2B823836FF0A1257DA8BFE6D68585078127B1FB99E933F41A0A655901049D3");

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HMOLPKG\0");

    /// <summary>按文件头判断是不是 .hmol（不要只看扩展名）。</summary>
    public static bool IsHmol(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[8];
            if (stream.Read(head) < 8) return false;
            for (var i = 0; i < 8; i++) if (head[i] != Magic[i]) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>只读明文头部（不校验密文，用于列表展示）。</summary>
    public static HmolHeader ReadHeader(string path)
    {
        var raw = File.ReadAllBytes(path);
        var header = JsonSerializer.Deserialize<HmolHeader>(GetHeaderBytes(raw))
                     ?? throw new InvalidDataException("头部不是合法 JSON");

        if (header.Format != "HMOLPKG")
            throw new InvalidDataException("format 不是 HMOLPKG");
        if (header.Version != FormatVersion)
            throw new InvalidDataException("不支持的格式版本：" + header.Version);

        return header;
    }

    /// <summary>解密到指定 zip 文件。失败抛 InvalidDataException（信息可直接展示给用户）。</summary>
    public static HmolHeader DecryptTo(string hmolPath, string outputZipPath)
    {
        var raw = File.ReadAllBytes(hmolPath);
        var headerBytes = GetHeaderBytes(raw);
        var header = JsonSerializer.Deserialize<HmolHeader>(headerBytes)
                     ?? throw new InvalidDataException("头部不是合法 JSON");

        if (header.Format != "HMOLPKG") throw new InvalidDataException("format 不是 HMOLPKG");
        if (header.Version != FormatVersion) throw new InvalidDataException("不支持的格式版本：" + header.Version);
        if (header.Crypto.Algorithm != "AES-256-GCM") throw new InvalidDataException("不支持的算法：" + header.Crypto.Algorithm);
        if (header.Crypto.TagLength != TagLength) throw new InvalidDataException("tagLength 必须为 16");

        var salt = Convert.FromBase64String(header.Crypto.Salt);
        var iv = Convert.FromBase64String(header.Crypto.Iv);
        if (salt.Length != 32) throw new InvalidDataException("salt 必须为 32 字节");
        if (iv.Length != 12) throw new InvalidDataException("iv 必须为 12 字节");

        var start = 16 + (int)BitConverter.ToUInt32(raw, 12);
        var cipherLength = raw.Length - start - TagLength;
        if (cipherLength < 0 || cipherLength != header.Payload.Size)
            throw new InvalidDataException($"载荷长度不符：头部 {header.Payload.Size}，实际 {cipherLength}");

        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, MasterKey, 32, salt,
            Encoding.UTF8.GetBytes(KdfInfo));

        var cipher = raw.AsSpan(start, cipherLength).ToArray();
        var tag = raw.AsSpan(raw.Length - TagLength, TagLength).ToArray();
        var plain = new byte[cipherLength];

        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(iv, cipher, tag, plain, headerBytes);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("认证失败：包已损坏或被修改");
        }

        if (Convert.ToHexString(SHA256.HashData(plain)) != header.Payload.Sha256.ToUpperInvariant())
            throw new InvalidDataException("SHA-256 不符：解出的内容与头部记录不一致");

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputZipPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(outputZipPath, plain);

        return header;
    }

    private static byte[] GetHeaderBytes(byte[] raw)
    {
        if (raw.Length < 16) throw new InvalidDataException("文件太短");

        for (var i = 0; i < Magic.Length; i++)
            if (raw[i] != Magic[i]) throw new InvalidDataException("不是 .hmol 文件（魔数不符）");

        if (BitConverter.ToUInt16(raw, 8) != FormatVersion) throw new InvalidDataException("容器版本不支持");
        if (BitConverter.ToUInt16(raw, 10) != 0) throw new InvalidDataException("flags 非 0");

        var headerLength = (int)BitConverter.ToUInt32(raw, 12);
        if (headerLength < 2 || headerLength > 1024 * 1024) throw new InvalidDataException("头部长度非法");
        if (raw.Length < 16 + headerLength + TagLength) throw new InvalidDataException("文件被截断");

        return raw.AsSpan(16, headerLength).ToArray();
    }
}
```

---

## 7. 接入 HMOL 的推荐做法

### 7.1 识别

在导入 / 下载完成的回调里，先按魔数判断（`HmolReader.IsHmol`），再回落到现有的 zip / 7z / rar 判断。文件选择框的过滤条件加上 `*.hmol`。

### 7.2 安装（推荐：复用 PackageInstaller）

```csharp
// 1) 解密到包管理器目录，名字用包标识，保证后续按名字能找到
var target = Path.Combine(PackageTypes.DirectoryOf(PackageType.Plugin),
                          header.Package + ".zip");
HmolReader.DecryptTo(hmolPath, target);

// 2) 走现有安装流程：解压 → 合并到游戏根目录 → 覆盖前备份 → 失败回滚
var outcome = PackageInstaller.Install(instance, PackageType.Plugin,
    Path.GetFileName(target), policy, progress, token);
```

这样能直接复用 HMOL 已有的 `.bak-<时间戳>` 备份、回滚、精确卸载与「已安装」列表。

补丁包内容形如：

```
使用说明.txt
LICENSE
LICENSE-CnCNet-Client.md
NOTICE.md
Resources\clientdx.exe
Resources\clientogl.exe
Resources\clientxna.exe
Resources\MoLanRelay.dll
Resources\Binaries\MoLanRelay.dll
Resources\Binaries\Windows\MonoGame.Framework.dll
```

全部路径都相对于游戏根目录，天然符合「解压合并到根目录」的语义。

### 7.3 展示

列表里显示 `header.Title` + `header.PackageVersion`，不要显示解密后的临时文件名。想显示「补丁日期」就用 `header.CreatedAt`。

### 7.4 失败处理

| 现象 | 建议提示 |
|---|---|
| 魔数不符 | 不是 HMOL 包 / 文件损坏 |
| `flags` 或版本非 1 | 需要更新启动器 |
| GCM 认证失败 | 包已损坏或被修改，请重新下载 |
| SHA-256 不符 | 同上（理论上不会发生，除非实现有误） |
| 长度不符 | 下载不完整 |

---

## 8. 测试向量

用于核对 HKDF 与 AES-GCM 实现是否与本文一致。向量文件：`HMOL-测试向量.hmol`（也可用下面的 base64 还原）。

| 项 | 值 |
|---|---|
| 主密钥 | `8E2B823836FF0A1257DA8BFE6D68585078127B1FB99E933F41A0A655901049D3` |
| salt | `000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F`（32 字节递增） |
| iv | `000102030405060708090A0B`（12 字节递增） |
| 派生出的 AES 密钥 | `A23546BEDD185E48FC47D736A05D136CA1A33DA609059E1A5D7B1FA7FB349516` |
| 明文 | `HMOL test vector v1\n`（20 字节，UTF-8） |
| 明文 SHA-256 | `73D16A991D9472DFDB34BCBF2EE0F57E8ADFD8A62ABFEDF1C2A653BD2FDA5E17` |
| 密文 | `B7FCCF8663005FAEAC67EDF040D498977892141F` |
| tag | `17ABBA42C3AD691AC5CC5D75508D6846` |

容器 base64（解码后即完整 `.hmol`，共 469 字节）：

```
SE1PTFBLRwABAAAA1QEAAHsiZm9ybWF0IjoiSE1PTFBLRyIsInZlcnNpb24iOjEsInBhY2thZ2UiOiJITU9MLVRFU1QtVkVDVE9SIiwicGFja2FnZVZlcnNpb24iOiIxLjAuMCIsInRpdGxlIjoiSE1PTCBmb3JtYXQgdGVzdCB2ZWN0b3IiLCJjcmVhdGVkQXQiOiIyMDI2LTExLTA3VDAwOjAwOjAwXHUwMDJCMDg6MDAiLCJwYXlsb2FkIjp7InR5cGUiOiJyYXciLCJzaXplIjoyMCwic2hhMjU2IjoiNzNEMTZBOTkxRDk0NzJERkRCMzRCQ0JGMkVFMEY1N0U4QURGRDhBNjJBQkZFREYxQzJBNjUzQkQyRkRBNUUxNyIsImVudHJ5Q291bnQiOjB9LCJjcnlwdG8iOnsiYWxnb3JpdGhtIjoiQUVTLTI1Ni1HQ00iLCJrZGYiOiJIS0RGLVNIQTI1NiIsInNhbHQiOiJBQUVDQXdRRkJnY0lDUW9MREEwT0R4QVJFaE1VRlJZWEdCa2FHeHdkSGg4PSIsIml2IjoiQUFFQ0F3UUZCZ2NJQ1FvTCIsInRhZ0xlbmd0aCI6MTYsImluZm8iOiJITU9MUEtHL3YxL3BheWxvYWQifX23/M+GYwBfrqxn7fBA1JiXeJIUHxerukLDrWkaxcxddVCNaEY=
```

核对结果：解密后应得到 `HMOL test vector v1\n`，且 SHA-256 为 `73D16A991D9472DFDB34BCBF2EE0F57E8ADFD8A62ABFEDF1C2A653BD2FDA5E17`。

---

## 9. 打包端（补丁项目）用法

转换工具源码在 `d:\MO\CNC\_gh\打包工具\HmolPack`，.NET 8 控制台程序。

```powershell
# 编译（只需一次，源码改动后再编译）
dotnet build "d:\MO\CNC\_gh\打包工具\HmolPack\HmolPack.csproj" -c Release

# 把 zip 转成 .hmol
HmolPack.exe pack <输入.zip> <输出.hmol> --name "包标识" --version "0.4.14" --title "展示名"

# 校验 / 解包（自测用）
HmolPack.exe verify <文件.hmol>
HmolPack.exe unpack <文件.hmol> <输出.zip>
```

也可以直接用包装脚本（自动编译、支持从目录现压 zip）：

```powershell
powershell -ExecutionPolicy Bypass -File "d:\MO\CNC\_gh\脚本\打包hmol.ps1" `
  -SourceDir "d:\MO\CNC\_build\MO-补丁1+2-直接覆盖" `
  -OutFile   "d:\MO\CNC\发布\patch.HMOL" `
  -PackageName "MO-补丁1+2-直接覆盖" -PackageVersion "0.4.14"
```

发布约定：玩家补丁包固定命名为 **`patch.HMOL`**，以后只发 `.hmol`，不再直接发 zip。源码包仍用 zip（GPL 要求源码可直接查看，且 HMOL 不需要读取它）。

---

## 10. 版本演进与安全说明

- 读到 `formatVersion` 或 `version` 不等于 `1` → 提示「请更新 HMOL」，不要尝试猜测解密。
- `flags` 保留给未来（例如换算法、加签名）。当前必须为 `0`。
- 新增字段可以往后加，解析器应忽略未知字段（`JsonSerializer` 默认行为即可）。
- 共享主密钥能防「玩家随手解压改文件」，但拿到本文的人都能重新打包。若要防伪造，下一步应改为：
  - 打包端用私钥签名头部；
  - HMOL 内置公钥验签；
  - 载荷仍用对称加密（密钥可继续内置）。
- 任何情况下都不要把解密结果长时间留在磁盘：安装完成后删除临时 zip。

---

## 11. 本文对应的发行物

| 文件 | 大小 | SHA-256 |
|---|---|---|
| `patch.HMOL` | 1,417,816 B | `847441D29886AC767EC817555F8DC3C2FADCA84F6AF919891F15D31CC28912B8` |
| 包内载荷（zip） | 1,417,222 B | `4750EBEBE034C80E6CABAB7CD27D32C085B7EDF1CCD5CA5B3A5F795220EA2237` |
| `HMOL-测试向量.hmol` | 469 B | `B9F5B11B56900FFD39E1A9FC7D025572A6CBD17E63ECCC1178D678DE3412A347` |

包标识 `MO-补丁1+2-直接覆盖`，包版本 `0.6.0`，内含 11 个条目（补丁 2026-11-15，中继 v0.6.0：地图同步、中文输入丢字修复、`/diag` 诊断包等）。
