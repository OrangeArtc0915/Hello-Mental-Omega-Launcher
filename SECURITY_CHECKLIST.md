# HMOL 发布前安全检查清单

## 发布到 GitHub 前的强制检查

### 1. 运行自动安全审计
```bash
py security_audit.py
```
必须输出 `✅ 所有检查通过, 可以安全发布` 才可继续。

### 2. 手动复核清单

#### 2.1 敏感信息
- [ ] `HMOL_config.json` **未提交** (在 .gitignore 中)
- [ ] `msal_token_cache.enc` **未提交** (在 .gitignore 中)
- [ ] `logs/` 目录 **未提交** (在 .gitignore 中)
- [ ] `.env` 文件 **未提交** (在 .gitignore 中)
- [ ] 任何真实密码 / token / secret **未在代码中**

#### 2.2 配置外部化
- [ ] 所有密钥使用 `OBF1:` 混淆格式或 `.env` 引用
- [ ] `.env.example` 模板**已提交** (供开发者参考)
- [ ] `README.md` 包含 `.env` 配置说明

#### 2.3 安全模块
- [ ] `crypto_utils.py` 已包含 AES-256-GCM 加密
- [ ] `anti_debug.py` 已启用反调试
- [ ] `security_audit.py` 可正常通过

#### 2.4 完整性保护
- [ ] 启动时调用 `verify_runtime_integrity()`
- [ ] MSAL token 缓存使用 AES-256-GCM 加密存储
- [ ] 关键文件使用 HMAC-SHA256 校验

### 3. 加密与混淆
- [ ] 源代码中无 MD5 / SHA1
- [ ] 使用 `obfuscate.py` 处理新增的硬编码字符串
- [ ] 客户端密钥使用 XOR + Base64 混淆

### 4. 文档与法律
- [ ] `LICENSE` (MIT) 文件已就位
- [ ] `README.md` 包含安全声明
- [ ] `CHANGELOG.md` 已更新版本号

### 5. 第三方依赖
- [ ] `cryptography` ≥ 41.0.0 (AES-256-GCM)
- [ ] `pycryptodome` (可选, 用于 PyInstaller --key)
- [ ] 所有依赖在 `requirements.txt` 中固定版本

### 6. CI/CD 建议
```yaml
# .github/workflows/security.yml
name: Security Audit
on: [push, pull_request]
jobs:
  audit:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v3
      - uses: actions/setup-python@v4
        with:
          python-version: '3.11'
      - run: pip install -r requirements.txt
      - run: py security_audit.py
```

### 7. 发布 exe 后的用户校验
将 `dist/HMOL.exe` 的 SHA256 校验值发布到 GitHub Releases:
```bash
certutil -hashfile dist\HMOL.exe SHA256
```
用户可对比校验值确认下载的 exe 未被篡改。

## 紧急响应

### 如果发现密钥泄露
1. 立即在 QQ 机器人后台重置 AppSecret
2. 在 Azure 门户撤销 Microsoft OAuth client
3. 通知用户重新登录
4. 升级 `crypto_utils` 中的混淆密钥
5. 发布新版本 + 通知 GitHub

### 如果发现可疑 PR
1. 关闭 PR
2. 审计 diff 中是否有注入代码
3. 检查 CI 是否通过
4. 与维护者沟通
