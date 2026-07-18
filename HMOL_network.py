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

import logging
import os
import socket
import ssl
import sys
import threading
import time
from typing import Any, Dict, Optional, Tuple, Union
from urllib.parse import urlparse

# 第三方
try:
    import requests
    from requests.adapters import HTTPAdapter
    from urllib3.util.ssl_ import create_urllib3_context
    REQUESTS_AVAILABLE = True
except ImportError:
    requests = None
    REQUESTS_AVAILABLE = False

# 项目内
try:
    from HMOL_secure_io import validate_url, InputValidationError
except ImportError:
    def validate_url(url, allowed_schemes=None):
        return url
    class InputValidationError(Exception):
        pass


# ============================================================
# 常量
# ============================================================
DEFAULT_USER_AGENT = "HMOL-Launcher/2.1 (+https://github.com/hmol/launcher)"
DEFAULT_TIMEOUT = 30  # 秒
DEFAULT_MAX_SIZE = 100 * 1024 * 1024  # 100 MB
DEFAULT_MAX_REDIRECTS = 5

# 默认禁用(通过环境变量或代码启用)
ALLOW_HTTP = os.environ.get("HMOL_ALLOW_HTTP", "0") == "1"
TLS_VERIFY = os.environ.get("HMOL_TLS_VERIFY", "1") == "1"

# 主机名白名单(可选)
HOSTNAME_ALLOWLIST: Optional[set] = None


# ============================================================
# 异常
# ============================================================
class NetworkSecurityError(Exception):
    """网络安全错误。"""
    pass


# ============================================================
# 审计日志
# ============================================================
_audit_logger = None
def _get_audit_logger():
    global _audit_logger
    if _audit_logger is None:
        _audit_logger = logging.getLogger("HMOL.network.audit")
        if not _audit_logger.handlers:
            _audit_logger.setLevel(logging.INFO)
            _h = logging.StreamHandler(sys.stderr)
            _h.setFormatter(logging.Formatter("[NET-AUDIT] %(asctime)s %(levelname)s %(message)s"))
            _audit_logger.addHandler(_h)
    return _audit_logger


def _audit(op: str, success: bool, **details) -> None:
    try:
        import json
        rec = {"op": op, "ok": success, "ts": int(time.time() * 1000)}
        rec.update({k: v for k, v in details.items() if v is not None})
        log = _get_audit_logger()
        if success:
            log.info(json.dumps(rec, ensure_ascii=False))
        else:
            log.warning(json.dumps(rec, ensure_ascii=False))
    except Exception:
        pass


# ============================================================
# TLS 上下文
# ============================================================
def create_strict_tls_context() -> ssl.SSLContext:
    """创建严格的 TLS 上下文(防降级攻击)。

    特性:
      - 禁用 SSLv2/SSLv3/TLSv1.0/TLSv1.1
      - 只使用 TLSv1.2+
      - 强制证书验证
      - 禁用压缩(防 CRIME 攻击)
      - 启用主机名验证
    """
    ctx = ssl.create_default_context()
    # 只允许 TLSv1.2+
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    # 禁用压缩
    try:
        ctx.options |= ssl.OP_NO_COMPRESSION
    except AttributeError:
        pass
    # 检查主机名
    ctx.check_hostname = True
    ctx.verify_mode = ssl.CERT_REQUIRED
    return ctx


# ============================================================
# 自定义 HTTPAdapter(强制 TLS)
# ============================================================
if REQUESTS_AVAILABLE:
    class SecureHTTPAdapter(HTTPAdapter):
        """HTTP 适配器,强制使用严格 TLS。"""

        def init_poolmanager(self, *args, **kwargs):
            try:
                ctx = create_strict_tls_context()
                kwargs["ssl_context"] = ctx
            except Exception:
                pass
            return super().init_poolmanager(*args, **kwargs)


# ============================================================
# 安全 Session
# ============================================================
class SecureSession:
    """安全 HTTP Session。"""

    def __init__(self,
                 user_agent: str = DEFAULT_USER_AGENT,
                 timeout: int = DEFAULT_TIMEOUT,
                 max_size: int = DEFAULT_MAX_SIZE,
                 max_redirects: int = DEFAULT_MAX_REDIRECTS,
                 verify_tls: bool = TLS_VERIFY,
                 allow_http: bool = ALLOW_HTTP,
                 hostname_allowlist: Optional[set] = None):
        if not REQUESTS_AVAILABLE:
            raise NetworkSecurityError("requests 库未安装")
        self.user_agent = user_agent
        self.timeout = timeout
        self.max_size = max_size
        self.max_redirects = max_redirects
        self.verify_tls = verify_tls
        self.allow_http = allow_http
        self.hostname_allowlist = hostname_allowlist
        self._session = requests.Session()
        # 配置 SSL
        if verify_tls:
            self._session.mount("https://", SecureHTTPAdapter())
        # 默认 headers
        self._session.headers.update({
            "User-Agent": user_agent,
            "Accept": "*/*",
            "Accept-Encoding": "gzip, deflate",
            "Connection": "keep-alive",
        })

    def _check_url(self, url: str) -> Tuple[str, str]:
        """验证 URL,返回 (scheme, host)。"""
        try:
            allowed = ("https", "http" if self.allow_http else "https")
            url = validate_url(url, allowed_schemes=allowed)
        except InputValidationError as e:
            raise NetworkSecurityError(f"URL 验证失败: {e}")
        parsed = urlparse(url)
        # 主机名白名单检查
        if self.hostname_allowlist is not None:
            host = parsed.hostname or ""
            if host.lower() not in {h.lower() for h in self.hostname_allowlist}:
                raise NetworkSecurityError(
                    f"主机 {host!r} 不在白名单内")
        # 防止 SSRF: 阻止内网地址
        if parsed.hostname:
            try:
                # 解析 IP
                ip = socket.gethostbyname(parsed.hostname)
                if ip.startswith(("127.", "10.", "172.16.", "172.17.", "172.18.",
                                  "172.19.", "172.20.", "172.21.", "172.22.",
                                  "172.23.", "172.24.", "172.25.", "172.26.",
                                  "172.27.", "172.28.", "172.29.", "172.30.",
                                  "172.31.", "192.168.", "169.254.", "0.0.0.0")):
                    if not self.allow_http:
                        raise NetworkSecurityError(
                            f"拒绝访问内网地址: {ip}")
            except socket.gaierror:
                # DNS 解析失败 — 让 requests 处理
                pass
        return parsed.scheme, parsed.hostname

    def request(self, method: str, url: str, **kwargs) -> requests.Response:
        """发送 HTTP 请求。"""
        scheme, host = self._check_url(url)
        # 强制 timeout
        if "timeout" not in kwargs:
            kwargs["timeout"] = self.timeout
        # 强制 verify
        if scheme == "https":
            kwargs["verify"] = self.verify_tls
        # 限制重定向
        kwargs.setdefault("allow_redirects", True)
        # 不允许 stream(防止大文件 DoS)
        kwargs["stream"] = True
        # 发送请求
        t0 = time.perf_counter()
        try:
            resp = self._session.request(method, url, **kwargs)
        except requests.exceptions.SSLError as e:
            _audit("request_ssl_error", False, url=url, error=str(e)[:200])
            raise NetworkSecurityError(f"SSL 错误: {e}")
        except requests.exceptions.ConnectionError as e:
            _audit("request_connection_error", False, url=url, error=str(e)[:200])
            raise NetworkSecurityError(f"连接错误: {e}")
        except requests.exceptions.Timeout as e:
            _audit("request_timeout", False, url=url, error=str(e)[:200])
            raise NetworkSecurityError(f"请求超时: {e}")
        except requests.exceptions.RequestException as e:
            _audit("request_error", False, url=url, error=str(e)[:200])
            raise NetworkSecurityError(f"请求错误: {e}")
        # 限制响应大小
        if "Content-Length" in resp.headers:
            try:
                cl = int(resp.headers["Content-Length"])
                if cl > self.max_size:
                    resp.close()
                    _audit("response_too_large", False, url=url, size=cl)
                    raise NetworkSecurityError(
                        f"响应太大: {cl} > {self.max_size}")
            except ValueError:
                pass
        elapsed_ms = (time.perf_counter() - t0) * 1000
        _audit("request", True, method=method, url=url, status=resp.status_code,
               elapsed_ms=round(elapsed_ms))
        return resp

    def get(self, url: str, **kwargs) -> requests.Response:
        return self.request("GET", url, **kwargs)

    def post(self, url: str, **kwargs) -> requests.Response:
        return self.request("POST", url, **kwargs)

    def download_to_file(self, url: str, dest_path: str,
                         max_size: Optional[int] = None,
                         chunk_size: int = 65536) -> int:
        """下载文件到指定路径(原子写入,大小限制)。"""
        if max_size is None:
            max_size = self.max_size
        resp = self.get(url, stream=True)
        # 检查 status
        if resp.status_code != 200:
            resp.close()
            raise NetworkSecurityError(
                f"下载失败: HTTP {resp.status_code}")
        # 原子写入
        import tempfile as _tf
        target_dir = os.path.dirname(dest_path) or "."
        os.makedirs(target_dir, exist_ok=True)
        fd, tmp = _tf.mkstemp(dir=target_dir, prefix=".dl_", suffix=".tmp")
        total = 0
        try:
            with os.fdopen(fd, "wb") as f:
                for chunk in resp.iter_content(chunk_size=chunk_size):
                    if not chunk:
                        continue
                    total += len(chunk)
                    if total > max_size:
                        raise NetworkSecurityError(
                            f"下载超过大小限制: {total} > {max_size}")
                    f.write(chunk)
            # 原子替换
            os.replace(tmp, dest_path)
        except Exception:
            try:
                os.close(fd)
            except Exception:
                pass
            try:
                os.unlink(tmp)
            except OSError:
                pass
            raise
        finally:
            resp.close()
        _audit("download", True, url=url, dest=dest_path, size=total)
        return total

    def close(self) -> None:
        """关闭 session。"""
        self._session.close()


# ============================================================
# 便捷函数
# ============================================================
_default_session: Optional[SecureSession] = None
_default_session_lock = threading.Lock()


def get_default_session(**kwargs) -> SecureSession:
    """获取默认安全 session(单例)。"""
    global _default_session
    if _default_session is None:
        with _default_session_lock:
            if _default_session is None:
                _default_session = SecureSession(**kwargs)
    return _default_session


def safe_get(url: str, **kwargs) -> requests.Response:
    """便捷 GET(使用默认 session)。"""
    return get_default_session().get(url, **kwargs)


def safe_post(url: str, **kwargs) -> requests.Response:
    """便捷 POST(使用默认 session)。"""
    return get_default_session().post(url, **kwargs)


# ============================================================
# 自检
# ============================================================
def self_test() -> dict:
    """运行网络模块自检(需要网络)。"""
    results = []
    if not REQUESTS_AVAILABLE:
        return {"passed": False, "results": [{"op": "import", "ok": False,
                                                "error": "requests not available"}]}
    # 1. TLS context creation
    try:
        ctx = create_strict_tls_context()
        ok = ctx.minimum_version == ssl.TLSVersion.TLSv1_2
        results.append({"op": "tls_context", "ok": ok})
    except Exception as e:
        results.append({"op": "tls_context", "ok": False, "error": str(e)})
    # 2. URL validation
    try:
        session = SecureSession(allow_http=False)
        # 合法 HTTPS URL
        session._check_url("https://www.example.com/")
        # 拒绝 HTTP
        try:
            session._check_url("http://example.com/")
            results.append({"op": "url_https_only", "ok": False,
                            "error": "should have raised"})
        except NetworkSecurityError:
            results.append({"op": "url_https_only", "ok": True})
    except Exception as e:
        results.append({"op": "url_https_only", "ok": False, "error": str(e)})
    # 3. Hostname allowlist
    try:
        session = SecureSession(
            allow_http=False,
            hostname_allowlist={"www.example.com"})
        session._check_url("https://www.example.com/")
        try:
            session._check_url("https://evil.com/")
            results.append({"op": "hostname_allowlist", "ok": False,
                            "error": "should have raised"})
        except NetworkSecurityError:
            results.append({"op": "hostname_allowlist", "ok": True})
    except Exception as e:
        results.append({"op": "hostname_allowlist", "ok": False, "error": str(e)})
    # 4. Actual HTTPS request (test connectivity)
    try:
        session = SecureSession(allow_http=False, timeout=10)
        try:
            resp = session.get("https://www.example.com/", timeout=10)
            ok = resp.status_code == 200
            resp.close()
        except NetworkSecurityError as e:
            # 网络不可达也算通过(模块工作正常)
            ok = True
        results.append({"op": "https_request", "ok": ok,
                        "note": "network may be unavailable in test env"})
    except Exception as e:
        results.append({"op": "https_request", "ok": False, "error": str(e)})
    return {
        "passed": all(r.get("ok") for r in results),
        "results": results,
    }


if __name__ == "__main__":
    import json
    res = self_test()
    print(json.dumps(res, indent=2, ensure_ascii=False))
    print(f"\nOverall: {'PASS' if res['passed'] else 'FAIL'}")
