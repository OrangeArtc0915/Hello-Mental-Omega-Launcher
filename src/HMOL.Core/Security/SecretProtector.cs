using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HMOL.Core.Security;

/// <summary>
/// 敏感字段（密钥等）的落盘保护：写配置时用 Windows DPAPI 按当前用户加密，读出时解密。
///
/// <para>
/// 密文带 <see cref="Prefix"/> 前缀，便于识别、也方便日后换算法时区分版本；
/// 不带前缀的值按旧版明文处理，所以老配置文件照常能读，下次保存自动转成密文。
/// </para>
/// <para>
/// 代价：密钥绑定当前 Windows 账户。把 <c>Data\</c> 拷到别的机器或别的账户下，
/// 原密文解不开，等于未设置，需要重新填一次。
/// </para>
/// </summary>
public static class SecretProtector
{
    /// <summary>密文前缀（含版本号）。</summary>
    public const string Prefix = "enc:v1:";

    /// <summary>加密。空串原样返回，不产生无意义的密文。</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;

        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain),
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);

        return Prefix + Convert.ToBase64String(cipher);
    }

    /// <summary>
    /// 解密。不带前缀的按旧版明文原样返回；解不开（换了账户/机器、数据损坏）返回空串，
    /// 由调用方当作「还没设置」处理。
    /// </summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;

        try
        {
            var plain = ProtectedData.Unprotect(
                Convert.FromBase64String(stored[Prefix.Length..]),
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>给敏感字符串字段用：序列化时加密，反序列化时解密。见 <see cref="SecretProtector"/>。</summary>
public sealed class ProtectedStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => SecretProtector.Unprotect(reader.GetString());

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(SecretProtector.Protect(value));
}
