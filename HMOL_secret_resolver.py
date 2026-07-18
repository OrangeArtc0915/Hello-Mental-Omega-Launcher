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

import os
import sys
import threading
from typing import Dict, Optional

# 导入本目录的 HMOL_crypto
_dir = os.path.dirname(os.path.abspath(__file__))
if _dir not in sys.path:
    sys.path.insert(0, _dir)

from HMOL_crypto import (
    get_key_manager,
    decrypt_str,
    _audit,
    CTX_QQ_BOT,
    CTX_MSAL,
    CTX_OD_SHARE,
    CRYPTO_AVAILABLE,
)


# ============================================================
# 单例解封缓存
# ============================================================
class SecretResolver:
    """运行时凭据解封器(进程内单例)。"""

    def __init__(self):
        self._lock = threading.Lock()
        self._sealed_data: Optional[Dict[str, str]] = None
        self._contexts: Optional[Dict[str, bytes]] = None
        self._plaintext_cache: Dict[str, str] = {}
        self._loaded = False
        self._load_error: Optional[str] = None

    def _load_seal_module(self) -> bool:
        """加载 HMOL_secrets_seal.py 模块。

        注意:此方法**不**获取 self._lock — 由调用方负责同步。
        设计:在持有锁的情况下,避免再次尝试获取同一锁造成死锁。

        路径解析顺序(PyInstaller 兼容):
          1. sys._MEIPASS  (PyInstaller --onefile 临时目录)
          2. EXE 同目录    (PyInstaller --onedir)
          3. _internal/    (PyInstaller --onedir 的 Python 模块目录)
          4. 源码位置      (开发模式,os.path.dirname(__file__))
          5. 当前工作目录  (回退)
        """
        if self._loaded:
            return self._load_error is None

        # 收集候选路径
        candidates = []

        # 0. 环境变量 HMOL_SECRETS_PATH(最高优先级,用户显式指定)
        custom_path = os.environ.get("HMOL_SECRETS_PATH", "").strip()
        if custom_path and os.path.isfile(custom_path):
            candidates.append(custom_path)
        elif custom_path:
            # 用户指定了路径但文件不存在 — 仍然记录,便于错误诊断
            _audit("seal_load_warn", False,
                       error=f"HMOL_SECRETS_PATH set but not found: {custom_path}")

        # 1. PyInstaller --onefile 临时目录
        meipass = getattr(sys, '_MEIPASS', None)
        if meipass:
            candidates.append(os.path.join(meipass, "HMOL_secrets_seal.py"))

        # 2. EXE 同目录
        try:
            exe_dir = os.path.dirname(os.path.abspath(sys.executable))
            if exe_dir:
                candidates.append(os.path.join(exe_dir, "HMOL_secrets_seal.py"))
        except Exception:
            pass

        # 3. PyInstaller --onedir 的 _internal 目录
        #    (优先 sys._MEIPASS 作为基目录,回退到 EXE 目录)
        internal_base = getattr(sys, '_MEIPASS', None)
        if not internal_base:
            try:
                internal_base = os.path.dirname(os.path.abspath(sys.executable))
            except Exception:
                pass
        if internal_base:
            candidates.append(os.path.join(
                internal_base, "_internal", "HMOL_secrets_seal.py"))
            # 备用:EXE 目录下的 _internal
            try:
                exe_dir = os.path.dirname(os.path.abspath(sys.executable))
                exe_internal = os.path.join(exe_dir, "_internal", "HMOL_secrets_seal.py")
                if exe_internal not in candidates:
                    candidates.append(exe_internal)
            except Exception:
                pass

        # 4. 源码位置(os.path.dirname(__file__))
        candidates.append(os.path.join(
            os.path.dirname(os.path.abspath(__file__)),
            "HMOL_secrets_seal.py",
        ))

        # 5. 当前工作目录(回退)
        try:
            cwd = os.getcwd()
            if cwd:
                candidates.append(os.path.join(cwd, "HMOL_secrets_seal.py"))
        except Exception:
            pass

        # 去重并按顺序尝试
        seen = set()
        for path in candidates:
            if path in seen:
                continue
            seen.add(path)
            if os.path.isfile(path):
                return self._try_load_seal(path)

        # 没找到任何候选路径
        attempted = "\n    ".join(candidates)
        self._load_error = (
            f"seal module not found. Searched:\n    {attempted}"
        )
        _audit("seal_load", False, error=self._load_error)
        self._loaded = True
        return False

    def _verify_seal_integrity(self, path: str) -> bool:
        """验证 seal 模块的 HMAC 签名。

        如果有 .sig 文件,必须校验通过;没有 .sig 则允许(向后兼容)但记录警告。
        """
        sig_path = path + ".sig"
        if not os.path.isfile(sig_path):
            _audit("seal_load", True, path=path, note="no_sig_file")
            return True
        try:
            import hashlib as _hl
            import hmac as _hmac
            from HMOL_crypto import get_key_manager
            mgr = get_key_manager(os.path.dirname(os.path.abspath(__file__)))
            hmac_key = mgr.get_subkey(b"HMOL-seal-module-v1")
            with open(path, "rb") as f:
                payload = f.read()
            with open(sig_path, "rb") as f:
                expected_sig = f.read()
            actual_sig = _hmac.new(hmac_key, payload, _hl.sha256).digest()
            return _hmac.compare_digest(expected_sig, actual_sig)
        except Exception as e:
            _audit("seal_sig_verify", False, path=path, error=str(e))
            return False

    def _try_load_seal(self, seal_path: str) -> bool:
        """尝试从指定路径加载 seal 模块。"""
        if not self._verify_seal_integrity(seal_path):
            self._load_error = f"seal 模块签名校验失败: {seal_path}"
            _audit("seal_load", False, error=self._load_error)
            self._loaded = True
            return False
        try:
            import importlib.util
            spec = importlib.util.spec_from_file_location(
                "HMOL_secrets_seal_dynamic", seal_path)
            if not spec or not spec.loader:
                self._load_error = f"failed to create spec from {seal_path}"
                _audit("seal_load", False, error=self._load_error)
                self._loaded = True
                return False
            mod = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(mod)
            self._sealed_data = mod.HMOL_SEALED_SECRETS
            self._contexts = mod._HMOL_CTX
            self._loaded = True
            _audit("seal_load", True, path=seal_path,
                   n=len(self._sealed_data))
            return True
        except Exception as e:
            self._load_error = f"seal load error from {seal_path}: {e}"
            _audit("seal_load", False, error=str(e))
            self._loaded = True
            return False

    def get(self, name: str) -> str:
        """获取解封后的凭据。

        优先级(从高到低):
          1. 环境变量(HMOL_<NAME>)
          2. 外部 JSON 配置文件(HMOL_secrets.json)
          3. 加密 seal 模块(本类主要职责)

        Args:
            name: 凭据名(如 "QQ_BOT_APPSECRET")

        Returns:
            明文凭据

        Raises:
            RuntimeError: 无法加载/解封
        """
        # 0. 外部配置优先(环境变量 / JSON 文件)
        #    — 让用户/运维无需修改代码即可注入凭据
        try:
            from HMOL_env_config import get_loader, ConfigSource
            loader = get_loader()
            external_val = loader.get(name)
            if external_val:
                source = loader.get_source(name)
                _audit("unseal_secret_external", True, name=name, source=source)
                # 也缓存到 plaintext cache(避免重复读取)
                with self._lock:
                    self._plaintext_cache[name] = external_val
                return external_val
        except Exception as e:
            # 外部配置失败不算致命,继续走 seal 路径
            _audit("unseal_secret_external", False, name=name, error=str(e))

        # 1. 先查缓存
        if name in self._plaintext_cache:
            return self._plaintext_cache[name]
        with self._lock:
            # 双重检查
            if name in self._plaintext_cache:
                return self._plaintext_cache[name]
            if not self._load_seal_module():
                raise RuntimeError(
                    f"无法加载凭据 '{name}': {self._load_error}\n"
                    f"提示:将 HMOL_secrets_seal.py 放在 EXE 同目录下,"
                    f"或设置环境变量 HMOL_SECRETS_PATH 指定完整路径"
                )
            if self._sealed_data is None or name not in self._sealed_data:
                raise KeyError(f"未知的凭据名: {name}")
            if not CRYPTO_AVAILABLE:
                raise RuntimeError("cryptography 库未安装,无法解封")
            ctx = self._contexts.get(name) if self._contexts else None
            aad = f"HMOL/{name}/v1".encode("utf-8")
            mgr = get_key_manager(os.path.dirname(os.path.abspath(__file__)))
            if ctx is None:
                raise RuntimeError(f"凭据 '{name}' 缺少上下文标签,无法派生子密钥")
            sub_key = mgr.get_subkey(ctx)
            try:
                pt = decrypt_str(self._sealed_data[name], sub_key, aad)
            except Exception as e:
                _audit("unseal_secret", False, name=name, error=str(e))
                raise RuntimeError(
                    f"解封凭据 '{name}' 失败(机器不匹配?): {e}")
            self._plaintext_cache[name] = pt
            _audit("unseal_secret", True, name=name, source="seal")
            return pt

    def clear_cache(self) -> None:
        """清空明文缓存(用于测试或紧急锁定)。"""
        with self._lock:
            for k in list(self._plaintext_cache.keys()):
                self._plaintext_cache[k] = "\x00" * len(self._plaintext_cache[k])
            self._plaintext_cache.clear()


# ============================================================
# 全局单例
# ============================================================
_resolver: Optional[SecretResolver] = None
_resolver_lock = threading.Lock()


def get_resolver() -> SecretResolver:
    """获取全局 SecretResolver 单例。"""
    global _resolver
    if _resolver is None:
        with _resolver_lock:
            if _resolver is None:
                _resolver = SecretResolver()
    return _resolver


# ============================================================
# 便捷接口(向后兼容 — 替换硬编码常量)
# ============================================================
def get_qq_bot_appid() -> str:
    return get_resolver().get("QQ_BOT_APPID")


def get_qq_bot_appsecret() -> str:
    return get_resolver().get("QQ_BOT_APPSECRET")


def get_qq_channel_id() -> str:
    return get_resolver().get("QQ_BOT_CHANNEL_ID")


def get_qq_group_id() -> str:
    return get_resolver().get("QQ_BOT_GROUP_ID")


def get_msal_client_id() -> str:
    return get_resolver().get("MSAL_CLIENT_ID")


def reset_resolver() -> None:
    """重置全局解析器(测试用)。"""
    global _resolver
    with _resolver_lock:
        if _resolver is not None:
            _resolver.clear_cache()
        _resolver = None


# ============================================================
# 自检
# ============================================================
def self_test() -> bool:
    """验证所有凭据可正确解封。"""
    try:
        r = get_resolver()
        for name in ("QQ_BOT_APPID", "QQ_BOT_APPSECRET",
                      "QQ_BOT_CHANNEL_ID", "QQ_BOT_GROUP_ID",
                      "MSAL_CLIENT_ID"):
            val = r.get(name)
            assert isinstance(val, str) and len(val) > 0
        print("[OK] 所有凭据解封成功")
        return True
    except Exception as e:
        print(f"[FAIL] 凭据解封失败: {e}")
        return False


if __name__ == "__main__":
    self_test()
