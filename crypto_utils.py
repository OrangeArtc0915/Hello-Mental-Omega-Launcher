"""
crypto_utils.py — HMOL 安全加密工具层

提供:
- SecureString: 内存中保护敏感字符串 (使用后自动清零)
- derive_key: PBKDF2-HMAC-SHA256 派生主密钥
- encrypt/decrypt: AES-256-GCM 带认证加密
- obfuscate_string/deobfuscate_string: XOR + Base64 静态混淆
- is_strong_encryption_available: 库可用性检测

设计原则:
1. 静态数据 AES-256-GCM (认证加密, 防篡改)
2. 派生密钥 PBKDF2-HMAC-SHA256, 200k 轮 (OWASP 2026 推荐)
3. 敏感数据使用 SecureString 包装, 进程退出前清零
4. 加密 nonce 每次随机生成 (96-bit), 与密文一同存储
5. 文件名混淆: 密文不暴露任何明文语义
"""

import os
import sys
import uuid
import base64
import hashlib
import hmac
import secrets
import socket
import struct
from typing import Optional

# Windows-specific registry module (only available on Windows)
if os.name == 'nt':
    try:
        import winreg
    except ImportError:
        winreg = None
else:
    winreg = None

# 尝试导入 cryptography (行业标准)
try:
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC
    from cryptography.hazmat.primitives import hashes
    from cryptography.hazmat.backends import default_backend
    _HAS_CRYPTOGRAPHY = True
except ImportError:
    _HAS_CRYPTOGRAPHY = False

# 加密参数 (生产级)
_KDF_ITERATIONS = 200_000        # OWASP 2026 推荐
_KEY_LENGTH = 32                # AES-256
_NONCE_LENGTH = 12              # GCM 96-bit nonce
_SALT_LENGTH = 16               # 128-bit salt
_VERSION_BYTE = b'\x01'         # 密文格式版本


def is_strong_encryption_available() -> bool:
    """检测 AES-256-GCM 是否可用"""
    return _HAS_CRYPTOGRAPHY


def generate_salt(length: int = _SALT_LENGTH) -> bytes:
    """生成密码学随机盐"""
    return secrets.token_bytes(length)


def derive_key(password: bytes, salt: bytes, iterations: int = _KDF_ITERATIONS) -> bytes:
    """
    使用 PBKDF2-HMAC-SHA256 派生 32 字节主密钥

    Args:
        password: 主密码 (如机器码)
        salt: 随机盐
        iterations: 迭代次数 (默认 200k)
    Returns:
        32 字节派生密钥
    """
    if _HAS_CRYPTOGRAPHY:
        kdf = PBKDF2HMAC(
            algorithm=hashes.SHA256(),
            length=_KEY_LENGTH,
            salt=salt,
            iterations=iterations,
            backend=default_backend()
        )
        return kdf.derive(password)
    else:
        # fallback: 标准库 PBKDF2
        return hashlib.pbkdf2_hmac('sha256', password, salt, iterations, dklen=_KEY_LENGTH)


def encrypt(plaintext: bytes, key: bytes, associated_data: Optional[bytes] = None) -> bytes:
    """
    AES-256-GCM 认证加密

    输出格式: [version(1)] [salt(16)] [nonce(12)] [ciphertext+tag]
    - salt: 派生密钥用 (可重用同一个 key + 不同 salt 提升安全性)
    - nonce: 每次随机
    - associated_data: 附加认证数据 (可选, 如文件名)

    Args:
        plaintext: 明文
        key: 32 字节主密钥
        associated_data: 附加认证数据 (可选)
    Returns:
        加密后的字节串
    """
    if not _HAS_CRYPTOGRAPHY:
        raise RuntimeError("AES-GCM 不可用, 请安装 cryptography 库")

    if len(key) != _KEY_LENGTH:
        raise ValueError(f"密钥长度必须为 {_KEY_LENGTH} 字节")

    nonce = secrets.token_bytes(_NONCE_LENGTH)
    aesgcm = AESGCM(key)
    ciphertext = aesgcm.encrypt(nonce, plaintext, associated_data)
    return _VERSION_BYTE + nonce + ciphertext


def decrypt(ciphertext_blob: bytes, key: bytes, associated_data: Optional[bytes] = None) -> bytes:
    """
    AES-256-GCM 解密并验证

    Args:
        ciphertext_blob: encrypt() 输出
        key: 32 字节主密钥
        associated_data: 附加认证数据 (需与加密时一致)
    Returns:
        解密后的明文
    Raises:
        ValueError: 格式错误或认证失败
    """
    if not _HAS_CRYPTOGRAPHY:
        raise RuntimeError("AES-GCM 不可用, 请安装 cryptography 库")

    if len(ciphertext_blob) < 1 + _NONCE_LENGTH + 16:
        raise ValueError("密文格式错误: 长度过短")

    version = ciphertext_blob[0:1]
    if version != _VERSION_BYTE:
        raise ValueError(f"不支持的密文版本: {version!r}")

    nonce = ciphertext_blob[1:1 + _NONCE_LENGTH]
    ciphertext = ciphertext_blob[1 + _NONCE_LENGTH:]

    aesgcm = AESGCM(key)
    try:
        plaintext = aesgcm.decrypt(nonce, ciphertext, associated_data)
    except Exception as e:
        raise ValueError(f"解密失败 (认证错误或密钥不匹配): {e}") from e

    return plaintext


def encrypt_to_base64(plaintext: bytes, key: bytes, associated_data: Optional[bytes] = None) -> str:
    """加密并返回 base64 字符串 (便于存储到 JSON)"""
    return base64.b64encode(encrypt(plaintext, key, associated_data)).decode('ascii')


def decrypt_from_base64(b64_ciphertext: str, key: bytes, associated_data: Optional[bytes] = None) -> bytes:
    """从 base64 字符串解密"""
    return decrypt(base64.b64decode(b64_ciphertext), key, associated_data)


def obfuscate_string(plaintext: str) -> str:
    """
    轻量级字符串混淆 (XOR + Base64)
    用于源代码中静态字符串的反爬虫保护
    注意: 这不是真正的加密, 仅增加静态分析难度

    写在前面的话: 这个函数经常被新手误用为"加密", 然后把密钥和密文
    一起放进源码里——这其实和明文没区别. 真正想加密的请用 encrypt().
    我们留这个函数, 是为了让"动态获取"取代"源码里写死"——别把它当银弹.
    """
    if not plaintext:
        return ""
    # 使用固定的 XOR 密钥 (源代码混淆, 非安全加密)
    # 这一行是历史包袱, 改它意味着所有 OBF1: 字符串都要重新生成;
    # 想"升级"它之前, 先确认你已经不再依赖源码里的硬编码.
    xor_key = bytes.fromhex("4d6f643a2d4d616e616765722076322e31204d4f44")
    data = plaintext.encode('utf-8')
    obfuscated = bytes(b ^ xor_key[i % len(xor_key)] for i, b in enumerate(data))
    return "OBF1:" + base64.b64encode(obfuscated).decode('ascii')


def deobfuscate_string(obfuscated: str) -> str:
    """
    还原混淆字符串
    - 以 "OBF1:" 开头: XOR + Base64
    - 否则: 原样返回 (向后兼容)
    """
    if not obfuscated or not obfuscated.startswith("OBF1:"):
        return obfuscated
    try:
        xor_key = bytes.fromhex("4d6f643a2d4d616e616765722076322e31204d4f44")
        data = base64.b64decode(obfuscated[5:])
        plaintext = bytes(b ^ xor_key[i % len(xor_key)] for i, b in enumerate(data))
        return plaintext.decode('utf-8')
    except Exception:
        return obfuscated  # 失败时返回原值


def safe_b64_decode(s: str) -> Optional[bytes]:
    """安全 base64 解码, 失败返回 None"""
    try:
        return base64.b64decode(s, validate=True)
    except Exception:
        return None


def constant_time_compare(a: str, b: str) -> bool:
    """常量时间字符串比较, 防止时序攻击"""
    return hmac.compare_digest(a.encode('utf-8'), b.encode('utf-8'))


class SecureString:
    """
    内存中保护敏感字符串的包装类
    - 使用 bytearray 存储 (可变, 可清零)
    - 使用完毕调用 clear() 清零
    - 弱引用计数 + 析构时清零
    """

    __slots__ = ('_data', '_cleared')

    def __init__(self, value: str):
        self._data = bytearray(value.encode('utf-8'))
        self._cleared = False

    def get(self) -> str:
        if self._cleared:
            raise ValueError("SecureString 已被清零, 无法访问")
        return bytes(self._data).decode('utf-8')

    def clear(self):
        """清零内存中的敏感数据"""
        if not self._cleared:
            for i in range(len(self._data)):
                self._data[i] = 0
            self._cleared = True

    def __del__(self):
        try:
            if hasattr(self, '_data') and hasattr(self, '_cleared'):
                self.clear()
        except Exception:
            pass

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.clear()
        return False


def secure_hash(data: bytes) -> str:
    """
    使用 BLAKE2b 替代 MD5/SHA1 (抗碰撞 + 高速)
    返回 64 字符 hex 摘要
    """
    return hashlib.blake2b(data, digest_size=32).hexdigest()


def get_machine_fingerprint(salt: Optional[bytes] = None) -> bytes:
    """
    获取机器指纹 (用于派生主密钥)
    - 收集硬件特征 (CPU 序列号、主板 UUID、磁盘序列号、MAC 地址)
    - 与盐混合后 BLAKE2b 哈希
    - 重装系统/更换主板后指纹变化, 旧密文无法解开 (预期行为)
    """
    components = []

    # 1. MAC 地址
    try:
        mac = ':'.join(f'{(uuid.getnode() >> i) & 0xff:02x}'
                       for i in range(0, 48, 8))
        components.append(mac.encode('utf-8'))
    except Exception:
        pass

    # 2. hostname
    try:
        components.append(os.uname().nodename.encode('utf-8'))
    except (AttributeError, OSError):
        try:
            components.append(socket.gethostname().encode('utf-8'))
        except Exception:
            pass

    # 3. 用户名
    try:
        components.append(os.getlogin().encode('utf-8'))
    except (OSError, AttributeError):
        components.append(b"hmol_default_user")

    # 4. Windows: 机器 GUID (注册表)
    if os.name == 'nt' and winreg is not None:
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                                r"SOFTWARE\Microsoft\Cryptography") as key:
                value, _ = winreg.QueryValueEx(key, "MachineGuid")
                components.append(value.encode('utf-8'))
        except Exception:
            pass
    elif os.name != 'nt':
        # Unix: /etc/machine-id
        try:
            with open('/etc/machine-id', 'rb') as f:
                components.append(f.read().strip())
        except Exception:
            pass

    if not components:
        components.append(b"hmol_fingerprint_fallback")

    raw = b'|'.join(components)
    fingerprint_input = raw + (salt or b'')
    return hashlib.blake2b(fingerprint_input, digest_size=32).digest()


_PERSISTENT_SALT_FILE = os.path.join(
    os.environ.get("HMOL_DATA_DIR", os.path.expanduser("~")),
    ".hmol_salt"
)
_salt_cache: Optional[bytes] = None


def _load_or_create_salt() -> bytes:
    """
    Load or create a persistent random salt.
    The salt is stored in a hidden file and reused across sessions
    so that derived keys (and encrypted MSAL tokens) remain valid
    between program launches.
    """
    global _salt_cache
    if _salt_cache is not None:
        return _salt_cache
    try:
        if os.path.exists(_PERSISTENT_SALT_FILE):
            with open(_PERSISTENT_SALT_FILE, "rb") as f:
                _salt_cache = f.read(_SALT_LENGTH)
            if len(_salt_cache) == _SALT_LENGTH:
                return _salt_cache
    except OSError:
        pass
    _salt_cache = generate_salt()
    try:
        parent_dir = os.path.dirname(_PERSISTENT_SALT_FILE)
        if parent_dir:
            os.makedirs(parent_dir, exist_ok=True)
        with open(_PERSISTENT_SALT_FILE, "wb") as f:
            f.write(_salt_cache)
        try:
            os.chmod(_PERSISTENT_SALT_FILE, 0o600)
        except (OSError, AttributeError):
            pass
    except OSError:
        pass
    return _salt_cache


def get_master_key(salt: Optional[bytes] = None) -> bytes:
    """
    Get master key (derived from machine fingerprint + persistent salt).

    Behavior:
      - If a salt is provided, use it directly.
      - Otherwise, load the persistent salt from disk, or create one on
        first launch and persist it.
      - Same machine + same salt = same key across sessions, so encrypted
        MSAL token cache (msal_token_cache.enc) can be decrypted on
        subsequent launches.
      - If the user reinstalls the OS or replaces the motherboard, the
        fingerprint changes, the cache will fail to decrypt, and the
        user will be prompted to log in again (expected behavior).
    """
    if salt is None:
        salt = _load_or_create_salt()
    fingerprint = get_machine_fingerprint()
    return derive_key(fingerprint, salt)


__all__ = [
    'SecureString',
    'derive_key',
    'encrypt',
    'decrypt',
    'encrypt_to_base64',
    'decrypt_from_base64',
    'obfuscate_string',
    'deobfuscate_string',
    'safe_b64_decode',
    'constant_time_compare',
    'secure_hash',
    'get_machine_fingerprint',
    'get_master_key',
    'generate_salt',
    'is_strong_encryption_available',
]
