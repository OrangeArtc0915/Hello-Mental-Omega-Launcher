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
"""
HMOL Crypto — Centralized Cryptography Module
==========================================================

"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import logging
import os
import platform
import secrets
import socket
import struct
import sys
import threading
import time
import uuid
from typing import Any, Optional, Tuple

# ============================================================
# 第三方加密库
# ============================================================
try:
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC
    from cryptography.hazmat.primitives.kdf.hkdf import HKDF
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa, padding
    from cryptography.hazmat.backends import default_backend
    CRYPTO_AVAILABLE = True
except ImportError:
    CRYPTO_AVAILABLE = False
    AESGCM = None
    PBKDF2HMAC = None
    HKDF = None
    hashes = None
    serialization = None
    rsa = None
    padding = None
    default_backend = None


# ============================================================
# 常量
# ============================================================

# 协议版本(用于未来升级)
HMOL_CRYPTO_VERSION = 1

# 派生迭代次数 — 600,000 (OWASP 2023 推荐值)
HMOL_PBKDF2_ITERATIONS = 600_000

# AES-256 密钥长度(32 字节 = 256 位)
HMOL_AES_KEY_LEN = 32

# GCM Nonce 长度(12 字节 = 96 位,NIST 推荐)
HMOL_GCM_NONCE_LEN = 12

# GCM Tag 长度(16 字节 = 128 位,最大安全)
HMOL_GCM_TAG_LEN = 16

# 盐长度(16 字节 = 128 位)
HMOL_SALT_LEN = 16

# RSA 密钥长度
HMOL_RSA_KEY_LEN = 2048

# 算法标识
ALGO_AES256_GCM = "AES-256-GCM"
ALGO_PBKDF2_SHA256 = "PBKDF2-HMAC-SHA256"
ALGO_HKDF_SHA256 = "HKDF-SHA256"
ALGO_RSA_2048_OAEP = "RSA-2048-OAEP-SHA256"

# 上下文分离标签(防止跨上下文密钥重用)
CTX_MASTER = b"HMOL-master-key-v1"
CTX_TOKEN_CACHE = b"HMOL-token-cache-v1"
CTX_QQ_BOT = b"HMOL-qq-bot-creds-v1"
CTX_MSAL = b"HMOL-msal-creds-v1"
CTX_OD_SHARE = b"HMOL-od-share-url-v1"


# ============================================================
# 审计日志
# ============================================================
_audit_logger = logging.getLogger("HMOL.crypto.audit")
if not _audit_logger.handlers:
    _audit_logger.setLevel(logging.INFO)
    # 仅输出到 stderr,不写文件(避免日志膨胀)
    _h = logging.StreamHandler(sys.stderr)
    _h.setFormatter(logging.Formatter("[CRYPTO-AUDIT] %(asctime)s %(message)s"))
    _audit_logger.addHandler(_h)
_audit_logger.propagate = False


def _audit(op: str, success: bool, **details: Any) -> None:
    """记录加密操作审计日志。

    Args:
        op: 操作类型(derive_key / encrypt / decrypt / rsa_gen / etc.)
        success: 是否成功
        **details: 附加元数据(不含敏感数据)
    """
    try:
        rec = {
            "op": op,
            "ok": success,
            "ts": int(time.time() * 1000),
            "pid": os.getpid(),
        }
        rec.update({k: v for k, v in details.items() if v is not None})
        if success:
            _audit_logger.info(json.dumps(rec, ensure_ascii=False))
        else:
            _audit_logger.warning(json.dumps(rec, ensure_ascii=False))
    except Exception:
        # 审计失败不影响主流程
        pass


# ============================================================
# 机器指纹(Machine Fingerprint)
# ============================================================
_FINGERPRINT_CACHE: Optional[bytes] = None
_FINGERPRINT_LOCK = threading.Lock()


def _read_machine_fingerprint() -> bytes:
    """读取机器指纹(不存储敏感系统信息,只派生熵)。

    采集: MachineGuid/machine-id + 主机名 + MAC + 平台
    输出: SHA-256 哈希(32 字节)
    必须有至少一个强绑定源(MachineGuid/machine-id),否则报错。
    """
    parts = []
    errors = []
    has_strong_binding = False

    # 主机名
    try:
        parts.append(socket.gethostname().encode("utf-8"))
    except Exception:
        parts.append(b"unknown-host")

    # MAC 地址(第一个非 loopback 接口)
    try:
        mac = uuid.getnode()
        if (mac >> 40) % 2 == 0:  # 防止随机 MAC
            parts.append(mac.to_bytes(6, "big"))
    except Exception:
        pass

    # 平台 + 机器架构
    try:
        parts.append(platform.platform().encode("utf-8"))
    except Exception:
        pass

    # 处理器型号
    try:
        if hasattr(platform, "processor") and platform.processor():
            parts.append(platform.processor().encode("utf-8"))
    except Exception:
        pass

    # Windows: MachineGuid(强烈绑定机器)
    if sys.platform == "win32":
        try:
            import winreg
            with winreg.OpenKey(
                winreg.HKEY_LOCAL_MACHINE,
                r"SOFTWARE\Microsoft\Cryptography"
            ) as key:
                v, _ = winreg.QueryValueEx(key, "MachineGuid")
                if v:
                    parts.append(str(v).encode("utf-8"))
                    has_strong_binding = True
        except Exception as e:
            errors.append(f"MachineGuid 读取失败: {e}")
    # Linux/macOS: machine-id
    else:
        for p in ("/etc/machine-id", "/var/lib/dbus/machine-id"):
            try:
                if os.path.isfile(p):
                    with open(p, "rb") as f:
                        parts.append(f.read().strip())
                    has_strong_binding = True
                    break
            except Exception as e:
                errors.append(f"{p} 读取失败: {e}")

    if not has_strong_binding:
        raise RuntimeError(
            "无法获取机器唯一标识(可能权限不足):\n  "
            + "\n  ".join(errors) if errors else "未知原因"
        )

    raw = b"|".join(parts)
    return hashlib.sha256(raw).digest()


def get_machine_fingerprint() -> bytes:
    """获取机器指纹(线程安全 + 缓存)。"""
    global _FINGERPRINT_CACHE
    if _FINGERPRINT_CACHE is None:
        with _FINGERPRINT_LOCK:
            if _FINGERPRINT_CACHE is None:
                _FINGERPRINT_CACHE = _read_machine_fingerprint()
    return _FINGERPRINT_CACHE


# ============================================================
# 密钥派生
# ============================================================
def _ensure_crypto() -> None:
    if not CRYPTO_AVAILABLE:
        raise RuntimeError(
            "cryptography 库未安装。请执行: pip install cryptography"
        )


def generate_salt(length: int = HMOL_SALT_LEN) -> bytes:
    """生成加密强随机盐。"""
    return secrets.token_bytes(length)


def derive_master_key(
    password: bytes,
    salt: bytes,
    iterations: int = HMOL_PBKDF2_ITERATIONS,
) -> bytes:
    """从密码 + 盐派生主密钥(使用 PBKDF2-HMAC-SHA256)。

    迭代次数默认 600,000(OWASP 2023 推荐)。
    """
    _ensure_crypto()
    kdf = PBKDF2HMAC(
        algorithm=hashes.SHA256(),
        length=HMOL_AES_KEY_LEN,
        salt=salt,
        iterations=iterations,
        backend=default_backend(),
    )
    return kdf.derive(password)


def derive_subkey(master_key: bytes, info: bytes, length: int = HMOL_AES_KEY_LEN) -> bytes:
    """从主密钥派生子密钥(HKDF-SHA256)。"""
    _ensure_crypto()
    return HKDF(
        algorithm=hashes.SHA256(),
        length=length,
        salt=None,
        info=info,
        backend=default_backend(),
    ).derive(master_key)


# ============================================================
# 主密钥管理
# ============================================================
class MasterKeyManager:
    """主密钥管理器 — 绑定到机器 + 进程本地熵。

    主密钥派生源:
      password = machine_fingerprint + process_local_secret
    这样:
      - 在不同机器上无法派生相同的主密钥
      - 即使知道 password 算法,也需要 process_local_secret
    """

    # 进程本地熵文件路径(由 get_program_base_path() 决定,启动器同目录)
    _ENTROPY_FILE = "HMOL_crypto_entropy.bin"

    def __init__(self, base_path: str):
        self._base_path = base_path
        self._lock = threading.Lock()
        self._master_key: Optional[bytes] = None
        self._entropy_salt: Optional[bytes] = None
        self._entropy_secret: Optional[bytes] = None

    def _entropy_file_path(self) -> str:
        return os.path.join(self._base_path, self._ENTROPY_FILE)

    def _load_or_create_entropy(self) -> Tuple[bytes, bytes]:
        """加载或创建进程本地熵(首次运行时生成)。

        Returns:
            (salt, secret) — 都用于密钥派生
        """
        path = self._entropy_file_path()
        if os.path.isfile(path):
            try:
                with open(path, "rb") as f:
                    data = f.read()
                if len(data) == HMOL_SALT_LEN * 2:
                    return data[:HMOL_SALT_LEN], data[HMOL_SALT_LEN:]
            except Exception as e:
                _audit("entropy_load", False, error=str(e))
        # 首次运行,生成并保存
        salt = secrets.token_bytes(HMOL_SALT_LEN)
        secret = secrets.token_bytes(HMOL_SALT_LEN)
        try:
            with open(path, "wb") as f:
                f.write(salt + secret)
            try:
                import stat
                os.chmod(path, stat.S_IRUSR | stat.S_IWUSR)
            except Exception:
                pass
            _audit("entropy_create", True, path=path)
        except Exception as e:
            _audit("entropy_create", False, error=str(e))
            raise
        return salt, secret

    def get_master_key(self) -> bytes:
        """获取主密钥(派生并缓存)。"""
        with self._lock:
            if self._master_key is not None:
                return self._master_key
            if self._entropy_salt is None:
                self._entropy_salt, self._entropy_secret = self._load_or_create_entropy()
            fp = get_machine_fingerprint()
            password = hashlib.sha256(fp + self._entropy_secret).digest()
            self._master_key = derive_master_key(password, self._entropy_salt)
            # 立即清空 password(防止意外泄露)
            # 注:password 是局部变量,函数返回后会被回收,但显式清空更安全
            password = b"\x00" * len(password)
            _audit("master_key_derive", True)
            return self._master_key

    def get_subkey(self, context: bytes) -> bytes:
        """获取上下文子密钥。"""
        master = self.get_master_key()
        return derive_subkey(master, context)

    def reset(self) -> None:
        """重置缓存(用于测试或重新生成)。"""
        with self._lock:
            self._master_key = None
            self._entropy_salt = None
            self._entropy_secret = None


# ============================================================
# AES-256-GCM 加解密
# ============================================================
def encrypt_aes_gcm(
    plaintext: bytes,
    key: bytes,
    associated_data: Optional[bytes] = None,
) -> bytes:
    """使用 AES-256-GCM 加密。

    输出格式: version(1) | nonce(12) | ciphertext_with_tag
    密文自动包含 16 字节 GCM tag,AES-GCM 内部处理

    Args:
        plaintext: 明文
        key: 32 字节密钥
        associated_data: 可选附加认证数据(AAD),不会被加密但会被认证

    Returns:
        字节串: version_byte + nonce + ciphertext(含 tag)
    """
    _ensure_crypto()
    if len(key) != HMOL_AES_KEY_LEN:
        raise ValueError(f"AES key must be {HMOL_AES_KEY_LEN} bytes")
    nonce = secrets.token_bytes(HMOL_GCM_NONCE_LEN)
    aesgcm = AESGCM(key)
    ct = aesgcm.encrypt(nonce, plaintext, associated_data)
    # 格式: version(1) | nonce(12) | ct(含 16 字节 tag)
    return bytes([HMOL_CRYPTO_VERSION]) + nonce + ct


def decrypt_aes_gcm(
    blob: bytes,
    key: bytes,
    associated_data: Optional[bytes] = None,
) -> bytes:
    """使用 AES-256-GCM 解密。

    Args:
        blob: encrypt_aes_gcm 的输出
        key: 32 字节密钥
        associated_data: 必须与加密时一致(否则认证失败)

    Returns:
        明文字节
    """
    _ensure_crypto()
    if len(key) != HMOL_AES_KEY_LEN:
        raise ValueError(f"AES key must be {HMOL_AES_KEY_LEN} bytes")
    if len(blob) < 1 + HMOL_GCM_NONCE_LEN + HMOL_GCM_TAG_LEN:
        raise ValueError("ciphertext too short")
    version = blob[0]
    if version != HMOL_CRYPTO_VERSION:
        raise ValueError(f"unsupported crypto version: {version}")
    nonce = blob[1:1 + HMOL_GCM_NONCE_LEN]
    ct = blob[1 + HMOL_GCM_NONCE_LEN:]
    aesgcm = AESGCM(key)
    try:
        return aesgcm.decrypt(nonce, ct, associated_data)
    except Exception as e:
        _audit("decrypt_aes_gcm", False, error=str(e))
        raise


# ============================================================
# 字符串便捷接口
# ============================================================
def encrypt_str(plaintext: str, key: bytes, associated_data: Optional[bytes] = None) -> str:
    """加密字符串并返回 base64(便于嵌入代码或存储)。"""
    blob = encrypt_aes_gcm(plaintext.encode("utf-8"), key, associated_data)
    result = base64.b64encode(blob).decode("ascii")
    _audit("encrypt_str", True)
    return result


def decrypt_str(token: str, key: bytes, associated_data: Optional[bytes] = None) -> str:
    """解密 base64 token 字符串。"""
    blob = base64.b64decode(token.encode("ascii"))
    result = decrypt_aes_gcm(blob, key, associated_data).decode("utf-8")
    _audit("decrypt_str", True)
    return result


# ============================================================
# 加密文件 / 解密文件
# ============================================================
def encrypt_file(src: str, dst: str, key: bytes, associated_data: Optional[bytes] = None) -> None:
    """加密文件(原子的)。"""
    with open(src, "rb") as f:
        plaintext = f.read()
    blob = encrypt_aes_gcm(plaintext, key, associated_data)
    # 原子写入
    tmp = dst + ".tmp"
    with open(tmp, "wb") as f:
        f.write(blob)
    os.replace(tmp, dst)


def decrypt_file(src: str, dst: str, key: bytes, associated_data: Optional[bytes] = None) -> None:
    """解密文件(原子的)。"""
    with open(src, "rb") as f:
        blob = f.read()
    plaintext = decrypt_aes_gcm(blob, key, associated_data)
    tmp = dst + ".tmp"
    with open(tmp, "wb") as f:
        f.write(plaintext)
    os.replace(tmp, dst)


# ============================================================
# RSA-2048
# ============================================================
class RSACipher:
    """RSA-2048-OAEP + SHA256,用于高熵 payload 加密。"""

    @staticmethod
    def generate_keypair() -> Tuple[bytes, bytes]:
        """生成 RSA-2048 密钥对,返回 (PEM-private, PEM-public)。"""
        _ensure_crypto()
        private = rsa.generate_private_key(
            public_exponent=65537,
            key_size=HMOL_RSA_KEY_LEN,
            backend=default_backend(),
        )
        pub = private.public_key()
        priv_pem = private.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.PKCS8,
            encryption_algorithm=serialization.NoEncryption(),
        )
        pub_pem = pub.public_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PublicFormat.SubjectPublicKeyInfo,
        )
        _audit("rsa_keypair_gen", True)
        return priv_pem, pub_pem

    @staticmethod
    def encrypt(plaintext: bytes, public_pem: bytes) -> bytes:
        """用公钥加密(限制 ≤ 190 字节)。"""
        _ensure_crypto()
        pub = serialization.load_pem_public_key(public_pem, backend=default_backend())
        return pub.encrypt(
            plaintext,
            padding.OAEP(
                mgf=padding.MGF1(algorithm=hashes.SHA256()),
                algorithm=hashes.SHA256(),
                label=None,
            ),
        )

    @staticmethod
    def decrypt(ciphertext: bytes, private_pem: bytes) -> bytes:
        """用私钥解密。"""
        _ensure_crypto()
        priv = serialization.load_pem_private_key(
            private_pem, password=None, backend=default_backend(),
        )
        try:
            return priv.decrypt(
                ciphertext,
                padding.OAEP(
                    mgf=padding.MGF1(algorithm=hashes.SHA256()),
                    algorithm=hashes.SHA256(),
                    label=None,
                ),
            )
        except Exception as e:
            _audit("rsa_decrypt", False, error=str(e))
            raise


# ============================================================
# 密封数据(Sealed Box) — AES key + RSA-wrapped key
# ============================================================
def seal_for_rsa(plaintext: bytes, public_pem: bytes) -> bytes:
    """用 RSA 公钥加密较大数据(混合加密)。

    格式: rsa_wrapped_key(256) + nonce(12) + ciphertext(含 tag)
    """
    _ensure_crypto()
    # 生成随机 AES 密钥
    aes_key = secrets.token_bytes(HMOL_AES_KEY_LEN)
    # RSA 包装 AES 密钥
    wrapped = RSACipher.encrypt(aes_key, public_pem)
    if len(wrapped) != HMOL_RSA_KEY_LEN // 8:
        # RSA-2048 输出 256 字节
        raise ValueError(f"unexpected RSA output size: {len(wrapped)}")
    # 用 AES-GCM 加密数据
    nonce = secrets.token_bytes(HMOL_GCM_NONCE_LEN)
    aesgcm = AESGCM(aes_key)
    ct = aesgcm.encrypt(nonce, plaintext, None)
    # 清空 AES key(防内存泄露)
    aes_key = b"\x00" * len(aes_key)
    return bytes([HMOL_CRYPTO_VERSION]) + wrapped + nonce + ct


def open_sealed(blob: bytes, private_pem: bytes) -> bytes:
    """解封 RSA 加密的数据。"""
    _ensure_crypto()
    if len(blob) < 1 + HMOL_RSA_KEY_LEN // 8 + HMOL_GCM_NONCE_LEN + HMOL_GCM_TAG_LEN:
        raise ValueError("sealed blob too short")
    version = blob[0]
    if version != HMOL_CRYPTO_VERSION:
        raise ValueError(f"unsupported crypto version: {version}")
    wrapped = blob[1:1 + HMOL_RSA_KEY_LEN // 8]
    nonce = blob[1 + HMOL_RSA_KEY_LEN // 8:1 + HMOL_RSA_KEY_LEN // 8 + HMOL_GCM_NONCE_LEN]
    ct = blob[1 + HMOL_RSA_KEY_LEN // 8 + HMOL_GCM_NONCE_LEN:]
    aes_key = RSACipher.decrypt(wrapped, private_pem)
    aesgcm = AESGCM(aes_key)
    try:
        result = aesgcm.decrypt(nonce, ct, None)
    except Exception as e:
        _audit("open_sealed", False, error=str(e))
        raise
    aes_key = b"\x00" * len(aes_key)
    return result


# ============================================================
# HMAC 完整性校验
# ============================================================
def hmac_sha256(key: bytes, data: bytes) -> bytes:
    """计算 HMAC-SHA256(32 字节)。"""
    return hmac.new(key, data, hashlib.sha256).digest()


def verify_hmac(key: bytes, data: bytes, expected: bytes) -> bool:
    """使用恒定时间比较验证 HMAC(防侧信道)。"""
    return hmac.compare_digest(hmac_sha256(key, data), expected)


# ============================================================
# 自检
# ============================================================
def self_test() -> dict:
    """运行加密模块自检 — 验证所有算法的正确性。

    Returns:
        dict: { "passed": bool, "results": [{...}] }
    """
    results = []
    if not CRYPTO_AVAILABLE:
        return {"passed": False, "results": [{"op": "import", "ok": False,
                                                "error": "cryptography not installed"}]}

    # 1. AES-256-GCM 加密/解密
    try:
        key = secrets.token_bytes(HMOL_AES_KEY_LEN)
        pt = b"Hello HMOL world!" * 100
        aad = b"associated-data-test"
        ct = encrypt_aes_gcm(pt, key, aad)
        pt2 = decrypt_aes_gcm(ct, key, aad)
        ok = (pt == pt2) and (len(ct) == 1 + HMOL_GCM_NONCE_LEN + len(pt) + HMOL_GCM_TAG_LEN)
        # 测试 AAD 错误应失败
        try:
            decrypt_aes_gcm(ct, key, b"wrong-aad")
            aad_ok = False  # 不应该成功
        except Exception:
            aad_ok = True
        results.append({"op": "aes256_gcm", "ok": ok and aad_ok})
    except Exception as e:
        results.append({"op": "aes256_gcm", "ok": False, "error": str(e)})

    # 2. PBKDF2 派生
    try:
        salt = secrets.token_bytes(HMOL_SALT_LEN)
        k1 = derive_master_key(b"password", salt, iterations=1000)  # 测试用 1000
        k2 = derive_master_key(b"password", salt, iterations=1000)
        ok = (k1 == k2) and (len(k1) == HMOL_AES_KEY_LEN)
        # 不同盐应不同
        salt2 = secrets.token_bytes(HMOL_SALT_LEN)
        k3 = derive_master_key(b"password", salt2, iterations=1000)
        ok = ok and (k1 != k3)
        results.append({"op": "pbkdf2", "ok": ok})
    except Exception as e:
        results.append({"op": "pbkdf2", "ok": False, "error": str(e)})

    # 3. HKDF 子密钥
    try:
        mk = secrets.token_bytes(HMOL_AES_KEY_LEN)
        sk1 = derive_subkey(mk, b"context-1")
        sk2 = derive_subkey(mk, b"context-1")
        sk3 = derive_subkey(mk, b"context-2")
        ok = (sk1 == sk2) and (sk1 != sk3) and (len(sk1) == HMOL_AES_KEY_LEN)
        results.append({"op": "hkdf", "ok": ok})
    except Exception as e:
        results.append({"op": "hkdf", "ok": False, "error": str(e)})

    # 4. RSA 加解密
    try:
        priv, pub = RSACipher.generate_keypair()
        pt = b"secret payload for RSA"
        ct = RSACipher.encrypt(pt, pub)
        pt2 = RSACipher.decrypt(ct, priv)
        ok = (pt == pt2)
        # 错误私钥应失败
        priv2, _ = RSACipher.generate_keypair()
        try:
            RSACipher.decrypt(ct, priv2)
            wrong_key_ok = False
        except Exception:
            wrong_key_ok = True
        results.append({"op": "rsa2048", "ok": ok and wrong_key_ok})
    except Exception as e:
        results.append({"op": "rsa2048", "ok": False, "error": str(e)})

    # 5. 密封盒(混合加密)
    try:
        priv, pub = RSACipher.generate_keypair()
        pt = b"large payload " * 1000
        sealed = seal_for_rsa(pt, pub)
        opened = open_sealed(sealed, priv)
        ok = (pt == opened)
        results.append({"op": "seal_open", "ok": ok})
    except Exception as e:
        results.append({"op": "seal_open", "ok": False, "error": str(e)})

    # 6. HMAC
    try:
        k = secrets.token_bytes(HMOL_AES_KEY_LEN)
        d = b"data to authenticate"
        mac = hmac_sha256(k, d)
        ok = verify_hmac(k, d, mac) and not verify_hmac(k, d, b"wrong" * 8)
        results.append({"op": "hmac_sha256", "ok": ok})
    except Exception as e:
        results.append({"op": "hmac_sha256", "ok": False, "error": str(e)})

    # 7. 文件加密
    try:
        import tempfile
        with tempfile.TemporaryDirectory() as td:
            sp = os.path.join(td, "src.bin")
            dp = os.path.join(td, "dst.enc")
            rp = os.path.join(td, "restored.bin")
            with open(sp, "wb") as f:
                f.write(os.urandom(1024))
            key = secrets.token_bytes(HMOL_AES_KEY_LEN)
            encrypt_file(sp, dp, key, b"file-aad")
            decrypt_file(dp, rp, key, b"file-aad")
            with open(sp, "rb") as f1, open(rp, "rb") as f2:
                ok = (f1.read() == f2.read())
            results.append({"op": "file_aes", "ok": ok})
    except Exception as e:
        results.append({"op": "file_aes", "ok": False, "error": str(e)})

    passed = all(r.get("ok") for r in results)
    _audit("self_test", passed, n=len(results))
    return {"passed": passed, "results": results}


# ============================================================
# 统一入口(全局单例)
# ============================================================
_global_mgr: Optional[MasterKeyManager] = None
_global_mgr_lock = threading.Lock()


def get_key_manager(base_path: Optional[str] = None) -> MasterKeyManager:
    """获取全局主密钥管理器(单例)。"""
    global _global_mgr
    if _global_mgr is None:
        with _global_mgr_lock:
            if _global_mgr is None:
                if base_path is None:
                    base_path = os.path.dirname(os.path.abspath(__file__))
                _global_mgr = MasterKeyManager(base_path)
    return _global_mgr


def reset_key_manager() -> None:
    """重置全局单例(测试用)。"""
    global _global_mgr
    with _global_mgr_lock:
        _global_mgr = None


# ============================================================
# 安全 hash 工具
# ============================================================
def constant_time_compare(a: bytes, b: bytes) -> bool:
    """恒定时间比较(防时序攻击)。"""
    return hmac.compare_digest(a, b)


def secure_random_bytes(n: int) -> bytes:
    """密码学安全随机字节。"""
    return secrets.token_bytes(n)


def secure_random_hex(n: int) -> str:
    """密码学安全随机十六进制字符串。"""
    return secrets.token_hex(n)


if __name__ == "__main__":
    # 运行自检
    import pprint
    res = self_test()
    pprint.pprint(res)
    print("\nOverall:", "PASS" if res["passed"] else "FAIL")
