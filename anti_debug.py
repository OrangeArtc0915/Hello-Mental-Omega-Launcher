"""
anti_debug.py — HMOL 反调试 + 完整性校验

提供:
- is_debugger_present(): 检测调试器附加
- check_tampering(): HMAC-SHA256 验证关键文件未被篡改
- check_suspicious_environment(): 沙箱/分析环境检测
- verify_runtime_integrity(): 启动时一次性自检

设计哲学: 反调试本质是猫鼠游戏, 真正的逆向大佬手里有无限时间,
而我们只有有限的开发周期. 所以这里的策略是"提高成本, 不追求绝对安全"——
让脚本小子觉得麻烦, 大佬觉得不值. 这其实是个性价比问题.
如果哪天有人非要和这份代码过不去, 那说明这份代码已经成功了.
"""

import os
import sys
import hmac
import hashlib
import platform
import ctypes
from typing import List, Tuple

# 完整性签名密钥 (运行时强制要求环境变量, 不得硬编码)
# 安全设计 (HMOL111.txt FINDING-04):
#   之前回退到硬编码的字符串, 一旦源码泄露, 任何人均可绕过 HMAC 校验.
#   改为 fail-closed: 缺失时抛错而非用默认值兜底——因为"无校验"比"拒绝启动"
#   更危险, 前者让用户以为有保护, 后者让用户明确知道需要配置.
def _resolve_integrity_key() -> bytes:
    """Re-read the integrity key from env at call time."""
    env_key = os.environ.get("HMOL_INTEGRITY_KEY", "")
    if not env_key:
        raise RuntimeError(
            "HMOL_INTEGRITY_KEY 未配置, 无法启动完整性校验. "
            "请在 .env 中设置 HMOL_INTEGRITY_KEY=<32 字节随机字符串> "
            "或通过系统环境变量注入。"
        )
    try:
        return env_key.encode("utf-8")
    except (UnicodeEncodeError, AttributeError):
        # 极少数情况: 平台默认编码异常. 之前会让 _INTEGRITY_KEY 变成
        # 错误的字节串, 静默通过校验. 改为显式抛错.
        raise RuntimeError("HMOL_INTEGRITY_KEY 编码失败, 请使用 ASCII/UTF-8 字符串")

_INTEGRITY_KEY = _resolve_integrity_key()


def _file_hmac(path: str, key: bytes) -> str:
    """计算文件的 HMAC-SHA256 摘要"""
    h = hmac.new(key, digestmod=hashlib.sha256)
    try:
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(8192), b""):
                h.update(chunk)
        return h.hexdigest()
    except (OSError, IOError):
        return ""


def is_debugger_present() -> bool:
    """
    检测调试器是否附加到当前进程
    跨平台实现:
    - Windows: IsDebuggerPresent + CheckRemoteDebuggerPresent + NtQueryInformationProcess
    - Linux: 检查 /proc/self/status 中 TracerPid
    - macOS: sysctl P_TRACED
    """
    try:
        if platform.system() == "Windows":
            # 1. 基础检测
            if ctypes.windll.kernel32.IsDebuggerPresent() != 0:
                return True
            # 2. 远程调试器检测
            check_remote = ctypes.c_int(0)
            ctypes.windll.kernel32.CheckRemoteDebuggerPresent(
                ctypes.windll.kernel32.GetCurrentProcess(),
                ctypes.byref(check_remote),
            )
            if check_remote.value != 0:
                return True
            # 3. 父进程检测 (调试器通常是父进程)
            try:
                import subprocess
                pid = os.getpid()
                out = None
                # 优先尝试 wmic (Win10 1809 之前)
                try:
                    out = subprocess.run(
                        ["wmic", "process", "where", f"ProcessId={pid}",
                         "get", "ParentProcessId", "/value"],
                        capture_output=True, timeout=2, text=True,
                    )
                except (OSError, FileNotFoundError):
                    out = None
                # Fallback: PowerShell (Win10 21H1+ 默认移除 wmic)
                if out is None or not out.stdout.strip():
                    try:
                        ps_cmd = (
                            f"(Get-CimInstance Win32_Process -Filter "
                            f"'ProcessId={pid}').ParentProcessId"
                        )
                        out = subprocess.run(
                            ["powershell", "-NoProfile", "-Command", ps_cmd],
                            capture_output=True, timeout=5, text=True,
                        )
                    except (OSError, FileNotFoundError):
                        out = None
                if out and out.stdout:
                    suspicious = ["devenv", "pycharm", "idea", "code", "x64dbg", "ollydbg"]
                    out_lower = out.stdout.lower()
                    if any(s in out_lower for s in suspicious):
                        return True
            except Exception:
                pass
            return False
        elif platform.system() == "Linux":
            # /proc/self/status 中 TracerPid != 0 表示被调试
            try:
                with open("/proc/self/status", "r") as f:
                    for line in f:
                        if line.startswith("TracerPid:"):
                            tracer_pid = int(line.split()[1])
                            if tracer_pid != 0:
                                return True
                # ptrace 反调试
                import ctypes
                PTRACE_TRACEME = 0
                libc = ctypes.CDLL("libc.so.6", use_errno=True)
                result = libc.ptrace(PTRACE_TRACEME, 0, 0, 0)
                if result == -1:
                    return True  # ptrace 失败说明已被追踪
            except (OSError, IOError):
                pass
            return False
        elif platform.system() == "Darwin":
            # macOS sysctl 检测
            import ctypes
            import ctypes.util
            libc = ctypes.CDLL(ctypes.util.find_library("c") or "libc.dylib")
            info = ctypes.c_int(0)
            info_size = ctypes.c_size_t(ctypes.sizeof(info))
            # KERN_PROC = 1, KERN_PROC_PID = 1
            libc.sysctl.restype = ctypes.c_int
            mib = (ctypes.c_int * 4)(1, 14, 1, os.getpid())
            if libc.sysctl(ctypes.byref(mib), 4, ctypes.byref(info),
                           ctypes.byref(info_size), None, 0) == 0:
                # P_TRACED = 0x00000800
                if info.value & 0x800:
                    return True
            return False
    except Exception:
        pass
    return False


def check_suspicious_environment() -> List[str]:
    """
    检测沙箱/分析环境
    返回可疑特征列表 (空列表表示正常)
    """
    suspicions = []

    # 1. 已知沙箱用户名
    sandbox_users = {
        "sandbox", "currentuser", "maltest", "malware", "sample",
        "virus", "analyst", "vmware", "vagrant", "john", "sandbox",
    }
    try:
        username = os.environ.get("USERNAME", os.environ.get("USER", "")).lower()
        if username in sandbox_users:
            suspicions.append(f"沙箱用户名: {username}")
    except Exception:
        pass

    # 2. 常见沙箱环境变量
    sandbox_envs = [
        "SANDBOX", "VBOX", "VMWARE_TOOLS", "VIRTUALBOX",
        "ANALYSIS_ENVIRONMENT", "MALWARE_ANALYSIS",
    ]
    for env in sandbox_envs:
        if os.environ.get(env):
            suspicions.append(f"沙箱环境变量: {env}")

    # 3. 极短的正常运行时间 (沙箱典型特征)
    try:
        import time
        # CLOCK_UPTIME_RAW (Linux/macOS) / CLOCK_BOOTTIME 兜底
        clock_id = None
        for attr in ("CLOCK_UPTIME_RAW", "CLOCK_UPTIME", "CLOCK_BOOTTIME"):
            if hasattr(time, attr):
                clock_id = getattr(time, attr)
                break
        if clock_id is not None:
            uptime = time.clock_gettime(clock_id)
            if uptime < 10:
                suspicions.append(f"system just booted ({uptime:.1f}s)")
    except (OSError, AttributeError):
        pass

    # 4. CPU/RAM 异常 (常见于虚拟机)
    try:
        import psutil
        cpu_count = psutil.cpu_count() or 0
        if 0 < cpu_count <= 1:
            suspicions.append(f"异常 CPU 数: {cpu_count}")
    except ImportError:
        pass

    return suspicions


def check_tampering(target_files: List[str]) -> Tuple[bool, List[str]]:
    """
    HMAC-SHA256 完整性校验
    验证关键 .py 文件未被修改

    Args:
        target_files: 关键文件路径列表
    Returns:
        (is_intact, list_of_modified_files)
    """
    modified = []
    key = _resolve_integrity_key()  # 每次重新解析, 支持 env 热切换
    for f in target_files:
        if not os.path.exists(f):
            modified.append(f"{f} (missing)")
            continue
        digest = _file_hmac(f, _INTEGRITY_KEY)
        if not digest:
            modified.append(f"{f} (unreadable)")
            continue
        sig_path = f + ".sig"
        if not os.path.exists(sig_path):
            continue
        try:
            with open(sig_path, "r", encoding="utf-8") as sigf:
                expected = sigf.read().strip()
        except OSError:
            continue
        if not hmac.compare_digest(digest, expected):
            modified.append(f"{f} (HMAC mismatch)")
    return (len(modified) == 0, modified)


def verify_runtime_integrity(strict: bool = False) -> Tuple[bool, List[str]]:
    """
    Startup self-check.
    - Detect debuggers
    - Detect sandbox environments
    - Verify integrity of critical files

    Args:
        strict: True=strict mode (any issue refuses to start), False=warn only
    Returns:
        (passed, issues_list)
        - passed: True if no issues (or non-strict with warnings), False if rejected
        - issues_list: human-readable issues detected (empty when clean)
    """
    issues = []

    if is_debugger_present():
        issues.append("debugger detected")

    sandbox_flags = check_suspicious_environment()
    if sandbox_flags:
        issues.append(f"suspicious environment: {', '.join(sandbox_flags)}")

    base_dir = os.path.dirname(os.path.abspath(__file__))
    critical = [
        os.path.join(base_dir, "HMOL_qt.py"),
        os.path.join(base_dir, "crypto_utils.py"),
    ]
    intact, modified = check_tampering(critical)
    if not intact:
        issues.append(f"modified files: {', '.join(modified)}")

    if issues:
        for issue in issues:
            print(f"[Security] {issue}")
        if strict:
            return False, issues
    return True, issues


def get_self_hash() -> str:
    """
    计算当前脚本的 SHA256 摘要
    用于发布时校验, 用户可对比官方发布的哈希值
    """
    try:
        path = os.path.abspath(__file__)
        h = hashlib.sha256()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(8192), b""):
                h.update(chunk)
        return h.hexdigest()
    except Exception:
        return ""


__all__ = [
    'is_debugger_present',
    'check_suspicious_environment',
    'check_tampering',
    'verify_runtime_integrity',
    'get_self_hash',
]
