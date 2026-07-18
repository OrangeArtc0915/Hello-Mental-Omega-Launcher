# ==============================================================================
# HMOL Launcher — Hello Mental Omega Launcher
# Copyright (c) 2026 HMOL Contributors. All Rights Reserved.
#
# 本文件受 HMOL Non-Commercial, No-Modification Source-Available License v2.2 保护。
# 严禁任何形式的二次修改、二次开发、二次封装、二次分发。
# 完整许可条款请参见项目根目录的 LICENSE 文件。
#
# This file is protected by the HMOL Non-Commercial, No-Modification
# Source-Available License v2.2. Any form of modification, derivative work,
# repackaging, or redistribution is strictly prohibited.
# For full license terms, see the LICENSE file in the project root.
#
# GitHub: https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher
# Issues: https://github.com/OrangeArtc0915/Hello-Mental-Omega-Launcher/issues
# ==============================================================================

import argparse
import base64
import json
import os
import sys
import time

# 添加当前目录到路径
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from HMOL_crypto import (
    get_key_manager,
    encrypt_str,
    decrypt_str,
    get_machine_fingerprint,
    hashlib,
    self_test,
    CTX_QQ_BOT,
    CTX_MSAL,
    CTX_OD_SHARE,
    _audit,
)


# ============================================================
# 凭据加载(安全 — 不在源码中存储任何明文凭据)
# ============================================================
# 优先级:
#   1. 环境变量(HMOL_<NAME>)
#   2. HMOL_plaintext_secrets.local.json(已 .gitignore)
#   3. 失败时提示用户设置
def _load_plaintext_secrets() -> dict:
    """从环境变量或本地 JSON 加载明文凭据。"""
    names = ["QQ_BOT_APPID", "QQ_BOT_APPSECRET",
             "QQ_BOT_CHANNEL_ID", "QQ_BOT_GROUP_ID",
             "MSAL_CLIENT_ID"]
    out = {}

    # 1. 优先环境变量
    for name in names:
        env_key = f"HMOL_{name}"
        val = os.environ.get(env_key, "")
        if val:
            out[name] = val

    # 2. JSON 文件补充(不覆盖环境变量)
    json_path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                             "HMOL_plaintext_secrets.local.json")
    if os.path.isfile(json_path):
        try:
            with open(json_path, "r", encoding="utf-8") as f:
                data = json.load(f)
            for name in names:
                if name not in out and name in data and data[name]:
                    out[name] = str(data[name])
        except Exception as e:
            sys.stderr.write(f"[WARN] 读取 {json_path} 失败: {e}\n")

    # 3. 检查缺失
    missing = [n for n in names if n not in out or not out[n]]
    if missing:
        sys.stderr.write(
            f"[ERROR] 缺少以下凭据: {missing}\n"
            f"请设置环境变量 HMOL_<NAME> 或创建\n"
            f"  {json_path}\n"
            f"格式: {{\"QQ_BOT_APPID\": \"...\", \"QQ_BOT_APPSECRET\": \"...\", ...}}\n"
        )
        raise RuntimeError(f"缺少凭据: {missing}")

    return out


PLAINTEXT_SECRETS = _load_plaintext_secrets()


# 上下文标签(防止跨用途密钥重用)
CONTEXTS = {
    "QQ_BOT_APPID": CTX_QQ_BOT,
    "QQ_BOT_APPSECRET": CTX_QQ_BOT,
    "QQ_BOT_CHANNEL_ID": CTX_QQ_BOT,
    "QQ_BOT_GROUP_ID": CTX_QQ_BOT,
    "MSAL_CLIENT_ID": CTX_MSAL,
}


# ============================================================
# 加密 / 解密
# ============================================================
def seal_secrets(base_path: str) -> dict:
    """加密所有凭据,返回 {key: base64_blob} 字典。"""
    mgr = get_key_manager(base_path)
    out = {}
    for name, plaintext in PLAINTEXT_SECRETS.items():
        ctx = CONTEXTS.get(name)
        # 用子密钥(每个 secret 不同的派生上下文)
        sub_key = mgr.get_subkey(ctx) if ctx else mgr.get_master_key()
        # 附加上下文信息到 AAD(防止跨上下文替换)
        # 这条 AAD 跟 HMOL_secret_resolver 必须保持一致,改了之后旧的 seal 会全废
        # TODO: 加一个 seal 格式版本号,以后升级 AAD 不至于全员重新 seal
        aad = f"HMOL/{name}/v1".encode("utf-8")
        out[name] = encrypt_str(plaintext, sub_key, aad)
        _audit("seal_secret", True, name=name)
    return out


def unseal_secrets(base_path: str, sealed: dict) -> dict:
    """解封所有凭据,返回 {key: plaintext} 字典(测试用)。"""
    mgr = get_key_manager(base_path)
    out = {}
    for name, blob in sealed.items():
        ctx = CONTEXTS.get(name)
        sub_key = mgr.get_subkey(ctx) if ctx else mgr.get_master_key()
        aad = f"HMOL/{name}/v1".encode("utf-8")
        out[name] = decrypt_str(blob, sub_key, aad)
    return out


# ============================================================
# 生成 Python 源文件(包含加密后的 blobs)
# ============================================================
def generate_seal_module(sealed: dict, out_path: str) -> None:
    """生成 HMOL_secrets_seal.py,包含加密后的 base64 blobs。"""
    # 获取机器指纹的短摘要(用于追踪,不是密钥)
    # 截断为 8 字符(64 位熵)足以唯一标识机器,同时不泄露完整指纹
    fp = get_machine_fingerprint()
    fp_short = hashlib.sha256(fp).hexdigest()[:8]
    sealed_at = time.strftime("%Y-%m-%d %H:%M:%S")

    lines = [
        "#!/usr/bin/env python3",
        "# -*- coding: utf-8 -*-",
        '"""',
        "HMOL Secrets Seal — 加密的凭据 blob(自动生成)",
        "=" * 60,
        "",
        f"机器指纹摘要: {fp_short}",
        f"加密时间: {sealed_at}",
        f"算法: AES-256-GCM (12 字节 nonce + 16 字节 tag)",
        f"密钥派生: PBKDF2-HMAC-SHA256 (600,000 iter)",
        "上下文隔离: HKDF-SHA256",
        "",
        "DO NOT EDIT — 任何手动修改都会导致解密失败。",
        "如需轮换密钥: python HMOL_seal_secrets.py --rotate",
        '"""',
        "",
        "from typing import Dict",
        "",
        "# 每个 secret 都用独立的子密钥加密,防止跨上下文替换攻击",
        f"HMOL_SEALED_SECRETS: Dict[str, str] = {{",
    ]
    for name, blob in sealed.items():
        # 多行 base64 输出,提高可读性
        b64 = blob
        # 每 64 字符换行
        chunks = [b64[i:i + 64] for i in range(0, len(b64), 64)]
        indented = "\n".join(f'    "{c}"' for c in chunks)
        lines.append(f'    "{name}": (\n{indented}\n    ),')

    lines.extend([
        "}",
        "",
        "HMOL_SEALED_AT = " + repr(sealed_at),
        "HMOL_SEALED_MACHINE = " + repr(fp_short),
        "",
        "# 上下文标签(与 HMOL_crypto 中的 CTX_* 对应)",
        "_HMOL_CTX = {",
    ])
    for name, ctx in CONTEXTS.items():
        lines.append(f'    "{name}": {ctx!r},')
    lines.extend([
        "}",
        "",
        "# AAD 模板(防止跨 secret 替换攻击)",
        "_HMOL_AAD_TEMPLATE = 'HMOL/{name}/v1'",
        "",
    ])
    content = "\n".join(lines) + "\n"
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(content)
    print(f"[OK] 已生成: {out_path}")
    print(f"     机器指纹摘要: {fp_short}")
    print(f"     凭据条目数: {len(sealed)}")

    # 生成 HMAC 签名文件(防篡改)
    try:
        import hmac as _hmac
        from HMOL_crypto import get_key_manager as _gkm
        mgr = _gkm(os.path.dirname(os.path.abspath(__file__)))
        hmac_key = mgr.get_subkey(b"HMOL-seal-module-v1")
        sig = _hmac.new(hmac_key, content.encode("utf-8"), hashlib.sha256).digest()
        sig_path = out_path + ".sig"
        with open(sig_path, "wb") as f:
            f.write(sig)
        print(f"     签名文件: {sig_path}")
    except Exception as e:
        print(f"[WARN] 签名生成失败: {e}")


# ============================================================
# 验证模块完整性
# ============================================================
def verify_seal_module(base_path: str, seal_path: str) -> bool:
    """验证 seal 模块能正确解密(在当前机器上)。"""
    import importlib.util
    spec = importlib.util.spec_from_file_location("hmol_seal", seal_path)
    if not spec or not spec.loader:
        print(f"[FAIL] 无法加载 seal 模块: {seal_path}")
        return False
    mod = importlib.util.module_from_spec(spec)
    try:
        spec.loader.exec_module(mod)
    except Exception as e:
        print(f"[FAIL] 加载 seal 模块失败: {e}")
        return False

    sealed = mod.HMOL_SEALED_SECRETS
    print(f"[INFO] 找到 {len(sealed)} 个加密凭据")
    try:
        unsealed = unseal_secrets(base_path, sealed)
        for name, plaintext in unsealed.items():
            # 仅显示前 4 个字符
            masked = (plaintext[:4] + "...") if len(plaintext) > 4 else plaintext
            print(f"  [OK] {name}: {masked}")
        return True
    except Exception as e:
        print(f"[FAIL] 解密失败(机器不匹配?): {e}")
        return False


# ============================================================
# 主入口
# ============================================================
def main():
    parser = argparse.ArgumentParser(description="HMOL Secret Encrypter")
    parser.add_argument("--encrypt", "-e", "--rotate", action="store_true",
                        help="生成/重新生成加密 seal 模块(密钥轮换)")
    parser.add_argument("--decrypt", action="store_true",
                        help="验证 seal 模块能在当前机器解密")
    parser.add_argument("--self-test", action="store_true",
                        help="运行加密模块自检")
    parser.add_argument("--seal-path", type=str,
                        default=os.path.join(
                            os.path.dirname(os.path.abspath(__file__)),
                            "HMOL_secrets_seal.py"),
                        help="seal 模块输出路径")
    args = parser.parse_args()

    base_path = os.path.dirname(os.path.abspath(__file__))

    if args.self_test:
        print("=" * 60)
        print("HMOL Crypto Self-Test")
        print("=" * 60)
        res = self_test()
        import pprint
        pprint.pprint(res)
        print("\nOverall:", "PASS" if res["passed"] else "FAIL")
        return 0 if res["passed"] else 1

    if args.encrypt:
        print("=" * 60)
        print("HMOL Secret Encryption")
        print("=" * 60)
        sealed = seal_secrets(base_path)
        generate_seal_module(sealed, args.seal_path)
        print("\n请将生成的 seal 模块加入版本控制,并从源代码中删除明文。")
        return 0

    if args.decrypt:
        print("=" * 60)
        print("HMOL Seal Module Verification")
        print("=" * 60)
        ok = verify_seal_module(base_path, args.seal_path)
        return 0 if ok else 1

    # 默认:自检
    print("请指定操作: --encrypt / --decrypt / --self-test")
    print("  --encrypt    加密凭据,生成 seal 模块")
    print("  --decrypt    验证 seal 模块")
    print("  --self-test  运行加密自检")
    return 0


if __name__ == "__main__":
    sys.exit(main())
