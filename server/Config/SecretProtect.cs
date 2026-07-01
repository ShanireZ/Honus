using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Horus.Server.Config;

/// 敏感配置(视觉端点 API key)加密存储:**配置文件里不存明文**。
/// 用 Windows DPAPI(机器范围)—— 无需另管主密钥,密文**只在本机可解**(移机需重新加密,对监考笔记本是安全加分)。
///
/// 用法:在**部署那台机器**上跑 `Horus.Server protect-secret <明文key>` 打印密文,粘进 server.config.json 的 `visionApiKeyEnc`;
/// 服务器启动时读取该密文并解密加载(仅进内存)。DPAPI 是 Windows 专属,非 Windows 调用会抛清晰错误。
public static class SecretProtect
{
    [SupportedOSPlatform("windows")]
    public static string Protect(string plaintext)
    {
        byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(enc);
    }

    [SupportedOSPlatform("windows")]
    public static string Unprotect(string base64)
    {
        byte[] dec = ProtectedData.Unprotect(Convert.FromBase64String(base64), optionalEntropy: null, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(dec);
    }

    /// 解析视觉端点 API key,按优先级:env(HORUS_VISION_KEY,联调/CI)> `visionApiKeyEnc`(DPAPI 解密)> `visionApiKey`(明文,仅联调)。
    /// 非 Windows 上遇 `visionApiKeyEnc` 抛错(生产=Windows 笔记本)。
    public static string Resolve(ServerConfig cfg)
    {
        if (!string.IsNullOrEmpty(cfg.VisionApiKey)) return cfg.VisionApiKey!;   // 明文(env 已在 Program 覆盖进此字段)/ 联调
        if (string.IsNullOrEmpty(cfg.VisionApiKeyEnc)) return "";
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("visionApiKeyEnc(DPAPI)仅 Windows 可解密;非 Windows 请用 HORUS_VISION_KEY 明文注入。");
        return Unprotect(cfg.VisionApiKeyEnc!);
    }
}
