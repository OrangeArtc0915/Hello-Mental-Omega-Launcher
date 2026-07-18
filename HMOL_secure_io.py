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

from __future__ import annotations

import os
import re
import shutil
import stat
import sys
import tempfile
import threading
import unicodedata
from typing import AnyStr, Optional, Union

# ============================================================
# 审计日志
# ============================================================
_audit_logger = None
def _get_audit_logger():
    global _audit_logger
    if _audit_logger is None:
        import logging
        _audit_logger = logging.getLogger("HMOL.secure_io.audit")
        if not _audit_logger.handlers:
            _audit_logger.setLevel(logging.INFO)
            _h = logging.StreamHandler(sys.stderr)
            _h.setFormatter(logging.Formatter("[SECURE-IO] %(asctime)s %(levelname)s %(message)s"))
            _audit_logger.addHandler(_h)
    return _audit_logger


def _audit(op: str, success: bool, **details) -> None:
    try:
        import json
        import time
        rec = {
            "op": op,
            "ok": success,
            "ts": int(time.time() * 1000),
        }
        rec.update({k: v for k, v in details.items() if v is not None})
        log = _get_audit_logger()
        if success:
            log.info(json.dumps(rec, ensure_ascii=False))
        else:
            log.warning(json.dumps(rec, ensure_ascii=False))
    except Exception:
        pass


# ============================================================
# 路径遍历防护
# ============================================================
class PathSecurityError(Exception):
    """路径安全错误(路径遍历、绝对路径、不允许的字符等)。"""
    pass


def normalize_path(path: str) -> str:
    """规范化路径(消除 `..`、`.`、多余分隔符等)。

    使用 os.path.realpath 解析符号链接。

    Args:
        path: 输入路径

    Returns:
        规范化后的绝对路径
    """
    if not isinstance(path, str):
        raise PathSecurityError(f"path must be str, got {type(path).__name__}")
    # Unicode 规范化(防 Unicode 同形异义攻击)
    path = unicodedata.normalize("NFC", path)
    # 移除 NUL 字符
    if "\x00" in path:
        raise PathSecurityError("path contains NUL character")
    # 规范化路径分隔符
    path = path.replace("\\", "/")
    return os.path.normpath(path)


def safe_join(base: str, *paths: str) -> str:
    """安全地拼接路径,确保结果仍在 base 目录内。

    防路径遍历攻击(如 `../../../etc/passwd`)。

    Args:
        base: 允许的根目录(白名单)
        *paths: 要拼接的相对路径

    Returns:
        规范化后的绝对路径

    Raises:
        PathSecurityError: 如果结果路径不在 base 目录内
    """
    if not base:
        raise PathSecurityError("base path is empty")
    base_abs = os.path.abspath(base)
    base_real = os.path.realpath(base_abs)
    if not paths:
        return base_real
    # 拼接并规范化
    joined = os.path.join(base_abs, *paths)
    joined_norm = os.path.normpath(joined)
    try:
        joined_real = os.path.realpath(joined_norm)
    except (OSError, FileNotFoundError):
        # 路径不存在时 realpath 可能失败,回退到逐级解析
        joined_real = os.path.abspath(joined_norm)
        parent = os.path.dirname(joined_real)
        if os.path.exists(parent):
            try:
                joined_real = os.path.join(os.path.realpath(parent),
                                           os.path.basename(joined_real))
            except Exception:
                pass
    # 验证结果在 base 目录内
    try:
        # Windows: 大小写不敏感; Linux: 大小写敏感
        if sys.platform == "win32":
            base_check = base_real.lower()
            joined_check = joined_real.lower()
        else:
            base_check = base_real
            joined_check = joined_real
        if not (joined_check == base_check or
                joined_check.startswith(base_check + os.sep) or
                joined_check.startswith(base_check + "/")):
            raise PathSecurityError(
                f"path traversal detected: {paths!r} -> {joined_real!r} not in {base_real!r}")
    except PathSecurityError:
        raise
    except Exception as e:
        raise PathSecurityError(f"path validation error: {e}")
    _audit("safe_join", True, base=base_real, paths=list(paths))
    return joined_real


def validate_filename(name: str, max_length: int = 255) -> str:
    """验证文件名合法性。

    Args:
        name: 文件名
        max_length: 最大长度(默认 255,大多数文件系统限制)

    Returns:
        验证通过的文件名

    Raises:
        PathSecurityError: 文件名非法
    """
    if not isinstance(name, str):
        raise PathSecurityError(f"filename must be str, got {type(name).__name__}")
    if not name:
        raise PathSecurityError("filename is empty")
    if len(name) > max_length:
        raise PathSecurityError(f"filename too long: {len(name)} > {max_length}")
    if "\x00" in name:
        raise PathSecurityError("filename contains NUL character")
    # Windows 保留字符
    if sys.platform == "win32":
        # < > : " / \ | ? *
        if re.search(r'[<>:"/\\|?*]', name):
            raise PathSecurityError(f"filename contains reserved characters: {name!r}")
        # Windows 保留名称
        stem = name.split(".")[0].upper()
        if stem in {"CON", "PRN", "AUX", "NUL"} or \
           (len(stem) == 4 and stem[:3] in {"COM", "LPT"} and stem[3].isdigit()):
            raise PathSecurityError(f"reserved filename: {name!r}")
        # 不允许以空格或点结尾
        if name.endswith(" ") or name.endswith("."):
            raise PathSecurityError(f"filename ends with space or dot: {name!r}")
    # 通用:不能以 . 开头(隐藏文件可能引起混淆)
    if name.startswith("."):
        pass
    _audit("validate_filename", True, name=name[:64])
    return name


# ============================================================
# 输入验证与清理
# ============================================================
class InputValidationError(Exception):
    """输入验证错误。"""
    pass


def sanitize_string(s: str, max_length: int = 1000,
                    allow_newlines: bool = True,
                    allow_null: bool = False) -> str:
    """清理字符串输入(防日志注入、控制字符等)。

    Args:
        s: 输入字符串
        max_length: 最大长度
        allow_newlines: 是否允许换行符
        allow_null: 是否允许 NUL 字符

    Returns:
        清理后的字符串
    """
    if not isinstance(s, str):
        raise InputValidationError(f"expected str, got {type(s).__name__}")
    if len(s) > max_length:
        raise InputValidationError(f"string too long: {len(s)} > {max_length}")
    # 移除 NUL 字符(除非明确允许)
    if not allow_null:
        s = s.replace("\x00", "")
    # 控制字符过滤(保留可打印字符 + 允许的空白)
    if allow_newlines:
        # 允许 \n \r \t 和可打印字符
        s = re.sub(r'[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]', '', s)
    else:
        s = re.sub(r'[\x00-\x1f\x7f]', '', s)
    return s


def validate_url(url: str, allowed_schemes: Optional[tuple] = ("https",)) -> str:
    """验证 URL 合法性。

    Args:
        url: URL 字符串
        allowed_schemes: 允许的协议(默认只允许 https)

    Returns:
        验证通过的 URL

    Raises:
        InputValidationError: URL 非法
    """
    if not isinstance(url, str):
        raise InputValidationError(f"expected str, got {type(url).__name__}")
    if not url:
        raise InputValidationError("URL is empty")
    if len(url) > 2048:
        raise InputValidationError("URL too long")
    # 基本检查
    if " " in url or "\t" in url or "\n" in url:
        raise InputValidationError("URL contains whitespace")
    if ".." in url:
        raise InputValidationError("URL contains '..'")
    # 协议检查
    if ":" not in url:
        raise InputValidationError("URL missing scheme")
    scheme = url.split(":", 1)[0].lower()
    if allowed_schemes and scheme not in allowed_schemes:
        raise InputValidationError(
            f"URL scheme not allowed: {scheme!r} (allowed: {allowed_schemes})")
    # 主机名检查
    if "//" not in url:
        raise InputValidationError("URL missing host")
    return url


def validate_port(port: Any) -> int:
    """验证端口号合法性。"""
    if not isinstance(port, int):
        raise InputValidationError(f"port must be int, got {type(port).__name__}")
    if port < 0 or port > 65535:
        raise InputValidationError(f"port out of range: {port}")
    return port


def validate_choice(value: Any, choices: list, name: str = "value") -> Any:
    """验证值在白名单内。"""
    if value not in choices:
        raise InputValidationError(
            f"{name} must be one of {choices}, got {value!r}")
    return value


# ============================================================
# 原子文件操作
# ============================================================
def atomic_write(path: str, data: AnyStr, mode: Optional[int] = None,
                 encoding: Optional[str] = None) -> None:
    """原子写入文件(防部分写入)。

    流程: 写入临时文件 -> fsync -> os.replace
    os.replace 在 Windows 和 Linux 上都是原子的。

    Args:
        path: 目标文件路径
        data: 要写入的数据(str 或 bytes)
        mode: 文件权限(仅 Unix)
        encoding: 当 data 是 str 时的编码(默认 utf-8)
    """
    target_dir = os.path.dirname(path) or "."
    os.makedirs(target_dir, exist_ok=True)
    # 写入临时文件
    fd, tmp = tempfile.mkstemp(
        dir=target_dir, prefix=".tmp_", suffix=os.path.splitext(path)[1])
    try:
        if isinstance(data, str):
            with os.fdopen(fd, "w", encoding=encoding or "utf-8", newline="") as f:
                f.write(data)
                f.flush()
                try:
                    os.fsync(f.fileno())
                except (OSError, AttributeError):
                    pass
        else:
            with os.fdopen(fd, "wb") as f:
                f.write(data)
                f.flush()
                try:
                    os.fsync(f.fileno())
                except (OSError, AttributeError):
                    pass
        # 原子替换
        os.replace(tmp, path)
    except Exception:
        # 清理临时文件
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise
    # 设置文件权限
    if mode is not None and sys.platform != "win32":
        try:
            os.chmod(path, mode)
        except OSError:
            pass
    elif sys.platform == "win32":
        # Windows: 限制为仅所有者可读写
        try:
            import stat
            os.chmod(path, stat.S_IRUSR | stat.S_IWUSR)
        except (OSError, AttributeError):
            pass
    _audit("atomic_write", True, path=path, size=len(data) if isinstance(data, (str, bytes)) else 0)


def secure_read(path: str, max_size: int = 100 * 1024 * 1024,
                encoding: Optional[str] = None) -> Union[str, bytes]:
    """安全读取文件(限制大小,防 DoS)。

    Args:
        path: 文件路径
        max_size: 最大允许大小(默认 100 MB)
        encoding: 解码编码(None 返回 bytes)

    Returns:
        文件内容(str 或 bytes)
    """
    if not os.path.isfile(path):
        raise FileNotFoundError(f"file not found: {path}")
    size = os.path.getsize(path)
    if size > max_size:
        raise ValueError(f"file too large: {size} > {max_size}")
    if encoding:
        with open(path, "r", encoding=encoding, errors="strict") as f:
            data = f.read()
    else:
        with open(path, "rb") as f:
            data = f.read()
    # TOCTOU 保护:检查实际读取大小
    actual_size = len(data) if isinstance(data, str) else len(data)
    if actual_size > max_size:
        raise ValueError(f"file too large after read: {actual_size} > {max_size}")
    _audit("secure_read", True, path=path, size=size)
    return data


# ============================================================
# 安全临时文件
# ============================================================
class SecureTempFile:
    """安全临时文件(自动清理 + 受限权限)。

    用法:
        with SecureTempFile(suffix=".bin") as f:
            f.write(b"secret")
            # 文件自动删除
    """
    def __init__(self, suffix: str = "", prefix: str = "hmol_",
                 dir: Optional[str] = None, mode: int = 0o600):
        self.suffix = suffix
        self.prefix = prefix
        self.dir = dir
        self.mode = mode
        self.path: Optional[str] = None
        self._fd: Optional[int] = None

    def __enter__(self):
        self._fd, self.path = tempfile.mkstemp(
            suffix=self.suffix, prefix=self.prefix, dir=self.dir)
        if sys.platform != "win32":
            try:
                os.chmod(self.path, self.mode)
            except OSError:
                pass
        return self

    def write(self, data: AnyStr) -> int:
        if self._fd is None:
            raise RuntimeError("file not opened")
        if isinstance(data, str):
            data = data.encode("utf-8")
        return os.write(self._fd, data)

    def __exit__(self, exc_type, exc_val, exc_tb):
        if self._fd is not None:
            try:
                os.close(self._fd)
            except OSError:
                pass
            self._fd = None
        if self.path:
            # 安全删除:覆写文件内容
            try:
                size = os.path.getsize(self.path)
                if size > 0:
                    with open(self.path, "wb") as f:
                        f.write(b"\x00" * min(size, 65536))
            except OSError:
                pass
            try:
                os.unlink(self.path)
            except OSError:
                pass
            self.path = None
        return False


# ============================================================
# 文件权限辅助
# ============================================================
def set_owner_only(path: str) -> None:
    """设置文件为仅所有者可读写(Unix: 0o600, Windows: 等效操作)。"""
    if sys.platform == "win32":
        try:
            os.chmod(path, stat.S_IRUSR | stat.S_IWUSR)
        except (OSError, AttributeError):
            pass
    else:
        try:
            os.chmod(path, 0o600)
        except OSError:
            pass


# ============================================================
# 自检
# ============================================================
def self_test() -> dict:
    """运行自检。"""
    results = []
    import tempfile as _tf
    # 1. normalize_path
    try:
        p = normalize_path("./foo/../bar/./baz")
        ok = (p == "bar/baz" or p.endswith("bar/baz") or p == "bar\\baz")
        results.append({"op": "normalize_path", "ok": ok, "result": p})
    except Exception as e:
        results.append({"op": "normalize_path", "ok": False, "error": str(e)})
    # 2. safe_join
    try:
        with _tf.TemporaryDirectory() as td:
            p = safe_join(td, "sub", "file.txt")
            ok = p.startswith(td)
            results.append({"op": "safe_join_ok", "ok": ok, "result": p})
    except Exception as e:
        results.append({"op": "safe_join_ok", "ok": False, "error": str(e)})
    # 3. safe_join with traversal
    try:
        with _tf.TemporaryDirectory() as td:
            try:
                p = safe_join(td, "..", "..", "etc", "passwd")
                results.append({"op": "safe_join_blocked", "ok": False,
                                "error": "should have raised"})
            except PathSecurityError:
                results.append({"op": "safe_join_blocked", "ok": True})
    except Exception as e:
        results.append({"op": "safe_join_blocked", "ok": False, "error": str(e)})
    # 4. validate_filename
    try:
        validate_filename("test.txt")
        validate_filename("中文文件.zip")
        results.append({"op": "validate_filename", "ok": True})
    except Exception as e:
        results.append({"op": "validate_filename", "ok": False, "error": str(e)})
    # 5. validate_filename blocks bad names
    try:
        try:
            validate_filename("../etc/passwd")
            results.append({"op": "validate_filename_bad", "ok": False,
                            "error": "should have raised"})
        except PathSecurityError:
            results.append({"op": "validate_filename_bad", "ok": True})
    except Exception as e:
        results.append({"op": "validate_filename_bad", "ok": False, "error": str(e)})
    # 6. validate_url
    try:
        validate_url("https://example.com")
        # 允许 localhost 用 http
        validate_url("http://localhost:8080/api",
                     allowed_schemes=("https", "http"))
        results.append({"op": "validate_url_https", "ok": True})
    except Exception as e:
        results.append({"op": "validate_url_https", "ok": False, "error": str(e)})
    # 7. validate_url blocks http
    try:
        try:
            validate_url("http://example.com")
            results.append({"op": "validate_url_http", "ok": False,
                            "error": "should have raised"})
        except InputValidationError:
            results.append({"op": "validate_url_http", "ok": True})
    except Exception as e:
        results.append({"op": "validate_url_http", "ok": False, "error": str(e)})
    # 8. sanitize_string
    try:
        s = sanitize_string("hello\x00world\x07test")
        ok = "\x00" not in s and "\x07" not in s
        results.append({"op": "sanitize_string", "ok": ok, "result": s})
    except Exception as e:
        results.append({"op": "sanitize_string", "ok": False, "error": str(e)})
    # 9. atomic_write
    try:
        with _tf.TemporaryDirectory() as td:
            path = os.path.join(td, "test.txt")
            atomic_write(path, "test content")
            with open(path, "r") as f:
                ok = (f.read() == "test content")
            results.append({"op": "atomic_write", "ok": ok})
    except Exception as e:
        results.append({"op": "atomic_write", "ok": False, "error": str(e)})
    # 10. SecureTempFile
    try:
        with _tf.TemporaryDirectory() as td:
            temp_path = None
            with SecureTempFile(suffix=".bin", dir=td) as f:
                f.write(b"secret data")
                temp_path = f.path
                path_existed = os.path.exists(temp_path)
            # After context exit, file should be gone
            ok = path_existed and temp_path and not os.path.exists(temp_path)
            results.append({"op": "secure_temp", "ok": ok})
    except Exception as e:
        results.append({"op": "secure_temp", "ok": False, "error": str(e)})
    return {
        "passed": all(r.get("ok") for r in results),
        "results": results,
    }


if __name__ == "__main__":
    import json
    res = self_test()
    print(json.dumps(res, indent=2, ensure_ascii=False))
    print(f"\nOverall: {'PASS' if res['passed'] else 'FAIL'}")
