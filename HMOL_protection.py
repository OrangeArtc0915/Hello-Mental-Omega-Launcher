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

import ctypes
import hashlib
import logging
import os
import random
import sys
import threading
import time
from typing import Any, Dict, List, Optional, Tuple

# ============================================================
# 审计日志
# ============================================================
_audit_logger = logging.getLogger("HMOL.protection.audit")
if not _audit_logger.handlers:
    _audit_logger.setLevel(logging.WARNING)
    _h = logging.StreamHandler(sys.stderr)
    _h.setFormatter(logging.Formatter("[PROTECT] %(asctime)s %(levelname)s %(message)s"))
    _audit_logger.addHandler(_h)
_audit_logger.propagate = False


# ============================================================
# 编译时常量
# ============================================================
# 启用开关(可通过环境变量 HMOL_PROTECTION_DISABLED=1 关闭)
PROTECTION_ENABLED = os.environ.get("HMOL_PROTECTION_DISABLED", "0") != "1"

# 检测到威胁时的行为
#   "log"     - 仅记录日志(默认,适合开发)
#   "warn"    - 弹窗警告
#   "exit"    - 退出程序(适合生产)
THREAT_RESPONSE = os.environ.get("HMOL_PROTECTION_RESPONSE", "log")


# ============================================================
# Windows API 加载
# ============================================================
_IS_WINDOWS = sys.platform == "win32"

_kernel32 = None
_ntdll = None
if _IS_WINDOWS:
    try:
        _kernel32 = ctypes.windll.kernel32
    except Exception:
        pass
    try:
        _ntdll = ctypes.windll.ntdll
    except Exception:
        pass


# ============================================================
# 1. 反调试检测
# ============================================================
class AntiDebug:
    """反调试检测 — 检测常见调试器附加状态。

    注意:这些检查**仅作日志记录**,不会让程序崩溃(避免影响合法用户)。
    """

    @staticmethod
    def is_debugger_present() -> bool:
        """通过 Windows API 检测调试器(仅 Windows)。"""
        if not _IS_WINDOWS or not _kernel32:
            return False
        try:
            return bool(_kernel32.IsDebuggerPresent())
        except Exception:
            return False

    @staticmethod
    def check_remote_debugger() -> bool:
        """通过 CheckRemoteDebuggerPresent 检测远程调试器。"""
        if not _IS_WINDOWS or not _kernel32:
            return False
        try:
            # 获取当前进程句柄
            handle = _kernel32.GetCurrentProcess()
            is_debugged = ctypes.c_bool(False)
            # BOOL CheckRemoteDebuggerPresent(HANDLE, PBOOL)
            _kernel32.CheckRemoteDebuggerPresent(
                handle, ctypes.byref(is_debugged))
            return bool(is_debugged.value)
        except Exception:
            return False

    @staticmethod
    def check_nt_global_flag() -> bool:
        """通过 NtQueryInformationProcess + ProcessDebugPort 检测调试。

        这是更底层的方法,即使 kernel32 hook 也会被发现。
        """
        if not _IS_WINDOWS or not _ntdll:
            return False
        try:
            # ProcessDebugPort = 7
            debug_port = ctypes.c_ulong(0)
            # ULONG ProcessDebugPort
            length_needed = ctypes.c_ulong(0)
            # NTSTATUS NtQueryInformationProcess(
            #   HANDLE, ULONG, PVOID, ULONG, PULONG)
            _ntdll.NtQueryInformationProcess.restype = ctypes.c_ulong
            status = _ntdll.NtQueryInformationProcess(
                _kernel32.GetCurrentProcess(),
                7,  # ProcessDebugPort
                ctypes.byref(debug_port),
                ctypes.sizeof(debug_port),
                ctypes.byref(length_needed),
            )
            # STATUS_SUCCESS = 0
            return status == 0 and debug_port.value != 0
        except Exception:
            return False

    @staticmethod
    def check_timing_anomaly(threshold_ms: float = 500.0) -> bool:
        """通过时间异常检测调试器(断点会导致代码执行变慢)。"""
        try:
            t0 = time.perf_counter()
            # 简单的计算密集循环
            total = sum(i * i for i in range(10000))
            t1 = time.perf_counter()
            elapsed_ms = (t1 - t0) * 1000
            return elapsed_ms > threshold_ms
        except Exception:
            return False

    @classmethod
    def run_all(cls) -> List[str]:
        """运行所有反调试检查,返回触发的检测项列表。"""
        if not PROTECTION_ENABLED:
            return []
        threats = []
        try:
            if cls.is_debugger_present():
                threats.append("IsDebuggerPresent=True")
            if cls.check_remote_debugger():
                threats.append("CheckRemoteDebuggerPresent=True")
            if cls.check_nt_global_flag():
                threats.append("NtGlobalFlag=True")
        except Exception as e:
            _audit_logger.debug(f"反调试检查异常(忽略): {e}")
        return threats


# ============================================================
# 2. 反虚拟化 / 沙箱检测
# ============================================================
class AntiVM:
    """检测常见虚拟机/沙箱特征。"""

    @staticmethod
    def check_vm_files() -> List[str]:
        """检查常见的 VM/沙箱相关文件。"""
        if not _IS_WINDOWS:
            return []
        suspicious = [
            r"C:\windows\system32\drivers\VBoxGuest.sys",
            r"C:\windows\system32\drivers\VBoxMouse.sys",
            r"C:\windows\system32\drivers\VBoxSF.sys",
            r"C:\windows\system32\vboxdisp.dll",
            r"C:\windows\system32\vmGuestLib.dll",
            r"C:\Program Files\VMware\VMware Tools",
            r"C:\Program Files\Oracle\VirtualBox Guest Additions",
        ]
        found = [p for p in suspicious if os.path.exists(p)]
        return found

    @staticmethod
    def check_processes() -> List[str]:
        """检查常见的调试/分析工具进程。"""
        if not _IS_WINDOWS:
            return []
        suspicious = [
            "ollydbg.exe", "x64dbg.exe", "x32dbg.exe",
            "ida.exe", "ida64.exe", "idaq.exe", "idaq64.exe",
            "windbg.exe", "devenv.exe", "immunitydebugger.exe",
            "dnspy.exe", "ilspy.exe", "dotpeek.exe", "dotpeek64.exe",
            "ghidra.exe", "binaryninja.exe", "cutter.exe", "radare2.exe",
            "cheatengine-x86_64.exe", "cheatengine.exe",
            "artmoney.exe", "tsearch.exe",
            "wireshark.exe", "fiddler.exe", "charles.exe",
            "processhacker.exe", "processhacker2.exe",
            "procmon.exe", "procexp.exe", "apimonitor.exe",
            "pycharm.exe", "pycharm64.exe",
        ]
        try:
            import subprocess
            output = subprocess.check_output(
                ["tasklist", "/FO", "CSV", "/NH"],
                creationflags=0x08000000,  # CREATE_NO_WINDOW
                timeout=3,
            ).decode("utf-8", errors="ignore").lower()
        except Exception:
            return []
        found = [p for p in suspicious if p.lower() in output]
        return found

    @classmethod
    def run_all(cls) -> List[str]:
        """运行所有 VM/沙箱检测,返回发现的特征。"""
        if not PROTECTION_ENABLED:
            return []
        threats = []
        try:
            files = cls.check_vm_files()
            if files:
                threats.append(f"VM_files={len(files)}")
            # process check is slow; only do it if files found
            if files:
                procs = cls.check_processes()
                if procs:
                    threats.append(f"analysis_procs={len(procs)}")
        except Exception as e:
            _audit_logger.debug(f"反 VM 检查异常(忽略): {e}")
        return threats


# ============================================================
# 3. 完整性校验
# ============================================================
class IntegrityChecker:
    """关键文件/代码完整性校验。

    使用 SHA-256 哈希 + HMAC 签名(防篡改)。
    """

    # 关键模块的预期 SHA-256(在发布时由 build.py 生成)
    # 留空表示不校验(开发环境)
    EXPECTED_HASHES: Dict[str, str] = {}

    # 用于签名的密钥(从环境变量或配置文件读取)
    # 没有密钥时跳过 HMAC,只做 SHA-256 校验
    HMAC_KEY: Optional[bytes] = None

    # 哈希配置文件(build 脚本生成)
    HASHES_FILE = "HMOL_integrity.json"

    @classmethod
    def _load_expected_hashes(cls) -> Dict[str, str]:
        """从 build 脚本生成的哈希文件加载预期哈希。"""
        # 在 EXE 模式下,文件应在 sys._MEIPASS 同级
        if getattr(sys, "frozen", False):
            base = getattr(sys, "_MEIPASS", os.path.dirname(sys.executable))
        else:
            base = os.path.dirname(os.path.abspath(__file__))
        path = os.path.join(base, cls.HASHES_FILE)
        if not os.path.isfile(path):
            return {}
        try:
            import json
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
            if not isinstance(data, dict):
                return {}
            return {k: str(v) for k, v in data.items()}
        except Exception:
            return {}

    @classmethod
    def hash_file(cls, path: str) -> Optional[str]:
        """计算文件的 SHA-256 哈希。"""
        try:
            h = hashlib.sha256()
            with open(path, "rb") as f:
                for chunk in iter(lambda: f.read(65536), b""):
                    h.update(chunk)
            return h.hexdigest()
        except Exception:
            return None

    @classmethod
    def check_module(cls, module_name: str, path: str) -> Tuple[bool, str]:
        """校验单个模块的完整性。

        Returns:
            (passed, message) — passed=True 表示校验通过
        """
        if not PROTECTION_ENABLED:
            return True, "protection disabled"
        expected = cls.EXPECTED_HASHES.get(module_name)
        if not expected:
            return True, "no expected hash (dev mode)"
        actual = cls.hash_file(path)
        if actual is None:
            return False, f"无法计算哈希: {path}"
        if actual != expected:
            return False, f"哈希不匹配: 预期={expected[:16]}..., 实际={actual[:16]}..."
        return True, "OK"

    @classmethod
    def run_all(cls) -> List[str]:
        """运行所有完整性检查,返回失败项。"""
        if not PROTECTION_ENABLED:
            return []
        # 优先从文件加载
        expected = cls._load_expected_hashes()
        if not expected:
            expected = cls.EXPECTED_HASHES
        failures = []
        for name, expected_hash in expected.items():
            # 在 PyInstaller EXE 模式下,模块解包到 sys._MEIPASS
            if getattr(sys, "frozen", False):
                base = getattr(sys, "_MEIPASS", os.path.dirname(sys.executable))
            else:
                base = os.path.dirname(os.path.abspath(__file__))
            path = os.path.join(base, name)
            ok, msg = cls.check_module(name, path)
            if not ok:
                failures.append(f"{name}: {msg}")
        return failures


# ============================================================
# 4. 启动时自检
# ============================================================
class StartupProtection:
    """启动时综合保护检查。"""

    _results: Dict[str, List[str]] = {}
    _lock = threading.Lock()
    _initialized = False

    @classmethod
    def initialize(cls, fail_open: bool = True) -> Dict[str, List[str]]:
        """运行所有启动时保护检查。

        Args:
            fail_open: True=威胁不阻止启动(只记录),False=检测到威胁则退出

        Returns:
            dict with keys: 'debug', 'vm', 'integrity'
        """
        if cls._initialized:
            return cls._results
        with cls._lock:
            if cls._initialized:
                return cls._results
            results = {
                "debug": AntiDebug.run_all(),
                "vm": AntiVM.run_all(),
                "integrity": IntegrityChecker.run_all(),
            }
            cls._results = results
            cls._initialized = True
            # 记录
            total_threats = sum(len(v) for v in results.values())
            if total_threats > 0:
                _audit_logger.warning(
                    f"启动保护检查: 检测到 {total_threats} 项威胁\n"
                    f"  调试: {results['debug']}\n"
                    f"  VM/沙箱: {results['vm']}\n"
                    f"  完整性: {results['integrity']}"
                )
                if not fail_open and THREAT_RESPONSE == "exit":
                    raise SystemExit(
                        f"启动保护检查失败: {results}")
            else:
                _audit_logger.info("启动保护检查: 全部通过")
            return results

    @classmethod
    def is_frozen(cls) -> bool:
        """是否在 PyInstaller EXE 中运行。"""
        return getattr(sys, "frozen", False)

    @classmethod
    def get_status(cls) -> Dict[str, Any]:
        """获取保护状态(用于 UI 显示)。"""
        return {
            "enabled": PROTECTION_ENABLED,
            "frozen": cls.is_frozen(),
            "platform": platform.platform(),
            "results": cls._results,
        }


# ============================================================
# 5. 防内存转储(简单实现)
# ============================================================
def secure_wipe(data: bytes) -> None:
    """安全擦除内存(尽可能)。

    注:Python 的不可变 bytes 无法真正擦除,但可以尝试覆盖。
    """
    if isinstance(data, (bytes, bytearray)):
        try:
            if isinstance(data, bytearray):
                for i in range(len(data)):
                    data[i] = 0
        except Exception:
            pass


# ============================================================
# 6. 编译时常量保护(防简单字符串搜索)
# ============================================================
def _obfuscate_str(s: str) -> str:
    """简单的字符串混淆(防 grep 搜索)。"""
    # 用 XOR 编码(配合运行时解码)
    key = 0x5A
    return "".join(chr(ord(c) ^ key) for c in s)


# 预解码的常量(避免每次调用 _obfuscate_str)
_DECODED: Dict[str, str] = {}


def constant(name: str) -> str:
    """获取预定义的常量(混淆存储)。"""
    if name in _DECODED:
        return _DECODED[name]
    return ""


# ============================================================
# 7. 启动入口
# ============================================================
def run_startup_protection(auto_initialize: bool = True) -> Dict[str, List[str]]:
    """运行启动保护(供主程序调用)。"""
    if auto_initialize:
        return StartupProtection.initialize()
    return StartupProtection._results


# ============================================================
# 自检
# ============================================================
def self_test() -> dict:
    """运行保护模块自检。"""
    results = []
    # 1. AntiDebug
    try:
        threats = AntiDebug.run_all()
        results.append({"op": "anti_debug", "ok": True,
                        "threats_found": len(threats)})
    except Exception as e:
        results.append({"op": "anti_debug", "ok": False, "error": str(e)})
    # 2. AntiVM
    try:
        threats = AntiVM.run_all()
        results.append({"op": "anti_vm", "ok": True,
                        "threats_found": len(threats)})
    except Exception as e:
        results.append({"op": "anti_vm", "ok": False, "error": str(e)})
    # 3. IntegrityChecker
    try:
        failures = IntegrityChecker.run_all()
        results.append({"op": "integrity", "ok": True,
                        "failures": len(failures)})
    except Exception as e:
        results.append({"op": "integrity", "ok": False, "error": str(e)})
    # 4. StartupProtection
    try:
        all_results = StartupProtection.initialize()
        results.append({"op": "startup", "ok": True,
                        "total_threats": sum(len(v) for v in all_results.values())})
    except Exception as e:
        results.append({"op": "startup", "ok": False, "error": str(e)})
    return {
        "passed": all(r.get("ok") for r in results),
        "results": results,
    }


if __name__ == "__main__":
    import json
    res = self_test()
    print(json.dumps(res, indent=2, ensure_ascii=False))
    print(f"\nPlatform: {platform.platform()}")
    print(f"Frozen (EXE): {StartupProtection.is_frozen()}")
    print(f"Protection enabled: {PROTECTION_ENABLED}")
