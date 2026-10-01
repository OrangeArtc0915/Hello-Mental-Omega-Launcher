using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HMOL.Core.Logging;
using HMOL.Core.Multiplayer;

namespace HMOL.Core.Security;

/// <summary>
/// 把内嵌在程序里的自签公钥证书写进本机的受信任存储，消除「未知发布者」提示。
///
/// <para>
/// 证书以嵌入资源（Resources\HMOL-mmm.cer）随 exe 分发，发行包里不再单独放 .cer 文件；
/// 内嵌的是公钥部分，私钥留在发布方的 签名\HMOL-mmm.pfx，绝不入库也不随包分发。
/// 写入的是 <see cref="StoreLocation.LocalMachine"/> 存储（对全机生效），必须以管理员身份执行，
/// 因此由提权实例调用（见 <c>ElevatedTasks</c> 的任务分发）。
/// </para>
/// </summary>
public static class TrustedCertificateInstaller
{
    /// <summary>证书文件名：MSBuild 生成的资源名形如 <c>HMOL.Core.Resources.HMOL-mmm.cer</c>，按后缀匹配即可。</summary>
    private const string CertificateFileName = "HMOL-mmm.cer";

    /// <summary>
    /// 证书要写入的两处存储：根证书颁发机构（建立信任链）与受信任的发布者（消除发布者提示）。
    /// 两处都写，Windows 才认这张证书签出来的 exe。
    /// </summary>
    private static readonly StoreName[] TargetStores = [StoreName.Root, StoreName.TrustedPublisher];

    /// <summary>
    /// 读取内嵌证书并写进本机受信任存储，返回可直接展示的中文结果。
    /// 已经装过时幂等返回成功，不重复写入。
    /// </summary>
    public static ToolkitStatus Install()
    {
        if (!OperatingSystem.IsWindows())
            return new ToolkitStatus(false, "非 Windows 无需安装证书");

        X509Certificate2 certificate;

        try
        {
            var payload = ReadEmbeddedCertificate();
            if (payload is null) return new ToolkitStatus(false, "程序内没有找到内置证书（构建时未嵌入）");

            certificate = new X509Certificate2(payload);
        }
        catch (Exception ex)
        {
            Log.Error("读取内置证书失败", ex);
            return new ToolkitStatus(false, $"读取内置证书失败：{ex.Message}");
        }

        var installed = new List<string>();

        foreach (var storeName in TargetStores)
        {
            try
            {
                using var store = new X509Store(storeName, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);

                if (!Contains(store, certificate)) store.Add(certificate);

                installed.Add(Describe(storeName));
            }
            catch (Exception ex)
            {
                Log.Error($"写入证书存储 {storeName} 失败", ex);

                var hint = ex is CryptographicException ? "（写入本机证书存储需要管理员权限）" : string.Empty;
                return new ToolkitStatus(false, $"写入{Describe(storeName)}失败{hint}：{ex.Message}");
            }
        }

        Log.Info($"已安装信任证书 {certificate.Subject}（指纹 {certificate.Thumbprint}）到 {string.Join("、", installed)}");

        return new ToolkitStatus(true,
            $"证书 {certificate.Subject} 已装进{string.Join("、", installed)}；此后运行本程序不再提示「未知发布者」。");
    }

    private static bool Contains(X509Store store, X509Certificate2 certificate)
        => store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0;

    private static string Describe(StoreName name) => name switch
    {
        StoreName.Root => "受信任的根证书颁发机构",
        StoreName.TrustedPublisher => "受信任的发布者",
        _ => name.ToString()
    };

    private static byte[]? ReadEmbeddedCertificate()
    {
        var assembly = typeof(TrustedCertificateInstaller).Assembly;

        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(resource => resource.EndsWith(CertificateFileName, StringComparison.OrdinalIgnoreCase));

        if (name is null) return null;

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}