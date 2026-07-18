"""
input_validation.py — HMOL 输入验证与路径安全

防护:
1. 路径遍历攻击 (../, 符号链接)
2. 文件名注入 (空字节, 控制字符)
3. ZIP/7z 炸弹检测 (压缩比异常)
4. 危险文件类型白名单
5. URL 注入 (javascript:, data:, vbscript:)
"""

import os
import re
import zipfile
from typing import Optional, Tuple, List

# 危险字符 (Windows 文件名禁用 + 路径注入)
_FORBIDDEN_CHARS = re.compile(r'[\x00-\x1f<>:"/\\|?*]')
_PATH_TRAVERSAL = re.compile(r'(?:\.\.[\\/]){2,}|(?:^|[\\/])\.\.[\\/]')
_WINDOWS_DRIVE = re.compile(r'^[a-zA-Z]:[\\/]')

# 危险 MIME / 协议
_DANGEROUS_URL_SCHEMES = ('javascript:', 'data:', 'vbscript:', 'file:')
# 协议正则: 必须从字符串开头匹配 (^), 避免 user:pass@host 中的 'user:' 误判
_URL_RE = re.compile(r'^([a-zA-Z][a-zA-Z0-9+.\-]*):')

# 文件类型白名单 (扩展名)
ALLOWED_PACKAGE_EXTENSIONS = {'.zip', '.7z', '.rar', '.tar', '.gz', '.tgz'}
ALLOWED_IMAGE_EXTENSIONS = {'.jpg', '.jpeg', '.png', '.gif', '.bmp', '.webp', '.svg'}
ALLOWED_DOC_EXTENSIONS = {'.txt', '.md', '.json', '.ini', '.cfg', '.log'}

# ZIP 炸弹检测阈值
MAX_COMPRESSION_RATIO = 100    # 压缩比 > 100 视为异常
MAX_UNCOMPRESSED_SIZE = 10 * 1024 * 1024 * 1024  # 10GB
MAX_FILE_COUNT = 100000


def sanitize_filename(name: str, replacement: str = '_') -> str:
    """
    清理文件名, 移除危险字符
    """
    if not name:
        return replacement
    # 1. 移除控制字符
    name = _FORBIDDEN_CHARS.sub(replacement, name)
    # 2. 移除 Windows 保留名
    reserved = {'CON', 'PRN', 'AUX', 'NUL', 'COM1', 'COM2', 'COM3', 'COM4',
                'COM5', 'COM6', 'COM7', 'COM8', 'COM9', 'LPT1', 'LPT2',
                'LPT3', 'LPT4', 'LPT5', 'LPT6', 'LPT7', 'LPT8', 'LPT9'}
    if name.split('.')[0].upper() in reserved:
        name = replacement + name
    # 3. 限制长度 (Windows 255 字符路径限制)
    if len(name) > 240:
        base, ext = os.path.splitext(name)
        name = base[:240 - len(ext)] + ext
    return name


def safe_path_join(base: str, *parts: str) -> Optional[str]:
    """
    安全路径拼接, 防止目录遍历和符号链接逃逸
    Returns:
        绝对路径 (合法) 或 None (非法)
    """
    # 1. 检查每个部分不包含 ../
    for part in parts:
        if part is None:
            return None
        if _PATH_TRAVERSAL.search(part):
            return None
        if '\x00' in part:
            return None

    # 2. 拼接
    candidate = os.path.normpath(os.path.join(base, *parts))

    # 3. 验证最终路径仍在 base 下
    base_abs = os.path.abspath(base)
    candidate_abs = os.path.abspath(candidate)
    try:
        # 兼容 Windows (大小写不敏感)
        if os.name == 'nt':
            base_abs = base_abs.lower()
            candidate_abs = candidate_abs.lower()
        if not (candidate_abs == base_abs or
                candidate_abs.startswith(base_abs + os.sep)):
            return None
        # 4. 反符号链接逃逸: 如果 candidate 是一个已存在的符号链接,
        #    必须解析 realpath 并再次确认仍在 base 下
        if os.path.islink(candidate):
            try:
                real = os.path.realpath(candidate)
                real_check = real.lower() if os.name == 'nt' else real
                if not (real_check == base_abs or
                        real_check.startswith(base_abs + os.sep)):
                    return None
            except OSError:
                return None
    except Exception:
        return None
    return candidate


def validate_package_path(path: str) -> Tuple[bool, str]:
    """
    验证包文件路径安全性
    Returns:
        (is_valid, error_message)
    """
    if not path:
        return False, "路径为空"
    if '\x00' in path:
        return False, "路径包含空字节"
    if not os.path.exists(path):
        return False, "文件不存在"
    ext = os.path.splitext(path)[1].lower()
    if ext not in ALLOWED_PACKAGE_EXTENSIONS:
        return False, f"不允许的文件类型: {ext}"
    # 文件大小限制 (单文件 4GB)
    try:
        size = os.path.getsize(path)
        if size > 4 * 1024 * 1024 * 1024:
            return False, "文件过大 (>4GB)"
    except OSError:
        return False, "无法读取文件大小"
    return True, ""


def check_zip_bomb(path: str) -> Tuple[bool, str]:
    """
    ZIP 炸弹检测
    Returns:
        (is_safe, error_message)
    """
    try:
        with zipfile.ZipFile(path, 'r') as zf:
            total_uncompressed = 0
            file_count = 0
            for info in zf.infolist():
                total_uncompressed += info.file_size
                file_count += 1
                # 单个文件异常大
                if info.file_size > MAX_UNCOMPRESSED_SIZE:
                    return False, f"文件过大: {info.filename}"
                # 嵌套 ZIP (递归压缩) 检测
                if info.compress_size > 0 and info.file_size > 0:
                    ratio = info.file_size / info.compress_size
                    if ratio > MAX_COMPRESSION_RATIO:
                        return False, f"压缩比异常 ({ratio:.0f}x): {info.filename}"
                elif info.compress_size == 0 and info.file_size > 0:
                    # store 模式 (未压缩) - 警惕嵌套炸弹
                    if info.file_size > 100 * 1024 * 1024:  # > 100MB
                        return False, f"未压缩大文件 (疑似嵌套): {info.filename}"
                if file_count > MAX_FILE_COUNT:
                    return False, f"文件数过多: {file_count}"
            if total_uncompressed > MAX_UNCOMPRESSED_SIZE:
                return False, f"解压后总大小异常: {total_uncompressed} bytes"
    except zipfile.BadZipFile:
        return False, "ZIP 文件格式错误"
    except Exception as e:
        return False, f"ZIP 检查失败: {e}"
    return True, ""


def validate_url(url: str) -> Tuple[bool, str]:
    """
    验证 URL 安全性 (防止 javascript: 等危险协议)
    """
    if not url:
        return False, "URL 为空"
    url = url.strip()
    # 长度限制
    if len(url) > 2048:
        return False, "URL 过长"
    # 检查危险协议 (从字符串开头匹配)
    m = _URL_RE.match(url)
    if m:
        scheme = m.group(1).lower()
        if scheme in _DANGEROUS_URL_SCHEMES:
            return False, f"危险的 URL 协议: {scheme}"
        # 仅允许 http/https
        if scheme not in ('http', 'https'):
            return False, f"不支持的 URL 协议: {scheme}"
    return True, ""


def is_private_ip(host: str) -> bool:
    """
    Check if a host is a private/internal IP address (anti-SSRF).
    Returns True for loopback, private, link-local, and reserved ranges.
    """
    import ipaddress
    try:
        ip = ipaddress.ip_address(host)
        return (
            ip.is_private
            or ip.is_loopback
            or ip.is_link_local
            or ip.is_reserved
            or ip.is_multicast
        )
    except ValueError:
        return False  # not an IP


def is_safe_http_url(url: str, allowed_schemes=("http", "https")) -> Tuple[bool, str]:
    """
    Stricter URL validation: scheme + host + private-IP check.
    Use this for any user-controlled URL that will be fetched.
    """
    ok, err = validate_url(url)
    if not ok:
        return False, err
    from urllib.parse import urlparse
    try:
        parsed = urlparse(url)
        host = (parsed.hostname or "").lower()
        if not host:
            return False, "URL 缺少主机名"
        if allowed_schemes and parsed.scheme.lower() not in allowed_schemes:
            return False, f"协议必须为 {allowed_schemes}"
        if is_private_ip(host):
            return False, f"禁止访问内网 IP: {host}"
        if parsed.port is not None and parsed.port not in (80, 443):
            return False, f"禁止使用非标准端口: {parsed.port}"
        return True, ""
    except Exception as e:
        return False, f"URL 解析失败: {e}"


def is_safe_member_path(member_path: str) -> bool:
    """
    检查 ZIP/7z 成员路径是否安全
    防止 zip slip (路径遍历)
    """
    if not member_path:
        return False
    if _PATH_TRAVERSAL.search(member_path):
        return False
    if member_path.startswith('/') or member_path.startswith('\\'):
        return False
    if _WINDOWS_DRIVE.search(member_path):
        return False
    if '\x00' in member_path:
        return False
    return True


def filter_safe_members(members: List) -> List:
    """
    过滤掉不安全的压缩包成员
    """
    return [m for m in members if is_safe_member_path(getattr(m, 'filename', str(m)))]


__all__ = [
    'sanitize_filename',
    'safe_path_join',
    'validate_package_path',
    'check_zip_bomb',
    'validate_url',
    'is_safe_http_url',
    'is_private_ip',
    'is_safe_member_path',
    'filter_safe_members',
    'ALLOWED_PACKAGE_EXTENSIONS',
    'ALLOWED_IMAGE_EXTENSIONS',
    'ALLOWED_DOC_EXTENSIONS',
]
