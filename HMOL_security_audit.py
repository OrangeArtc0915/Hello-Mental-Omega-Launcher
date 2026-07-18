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

import argparse
import json
import os
import re
import sys
from typing import Dict, List, Optional


# ============================================================
# 规则定义
# ============================================================
class AuditRule:
    """安全审计规则。"""

    def __init__(self, rid: str, name: str, severity: str, pattern: str,
                 file_pattern: str = r".*\.py$",
                 description: str = "",
                 recommendation: str = ""):
        self.id = rid
        self.name = name
        self.severity = severity  # "critical" | "high" | "medium" | "low" | "info"
        self.pattern = re.compile(pattern)
        self.file_pattern = re.compile(file_pattern)
        self.description = description
        self.recommendation = recommendation


# 硬编码凭据检测(基于已知前缀和模式)
HARDCODED_PATTERNS = [
    # 通用模式
    (r'(?i)(api[_-]?key|secret|token|password|passwd|pwd)\s*=\s*["\'][^"\']{16,}["\']',
     "可能的硬编码凭据"),
    # AWS Access Key
    (r"AKIA[0-9A-Z]{16}", "AWS Access Key ID"),
    # GitHub Personal Access Token
    (r"ghp_[a-zA-Z0-9]{36}", "GitHub Personal Access Token"),
    # Slack Token
    (r"xox[baprs]-[0-9a-zA-Z]{10,}", "Slack Token"),
    # 通用 JWT
    (r"eyJ[a-zA-Z0-9_-]{10,}\.eyJ[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,}",
     "JSON Web Token"),
    # QQ Bot AppID(10 位数字)
    (r'QQ_BOT_APPID\s*=\s*["\']\d{8,}["\']', "QQ Bot AppID 硬编码"),
    # QQ Bot AppSecret
    (r'QQ_BOT_APPSECRET\s*=\s*["\'][A-Za-z0-9]{12,}["\']',
     "QQ Bot AppSecret 硬编码"),
    # MSAL Client ID
    (r'MSAL_CLIENT_ID\s*=\s*["\'][a-f0-9-]{30,}["\']', "MSAL Client ID 硬编码"),
    # 通用 GUID 硬编码
    (r'["\'][a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}["\']',
     "GUID 硬编码"),
]


# 危险代码模式(精确匹配)
# 使用 \b 单词边界,且只匹配函数调用 `exec(` 而不是 `dlg.exec(`
DANGEROUS_PATTERNS = [
    # exec 作为内置函数调用(不是方法名)
    (r"(?<![\w.])exec\s*\(", "exec() 内置函数(代码注入风险)", "critical"),
    (r"(?<![\w.])eval\s*\((?!.*ast\.literal_eval)", "eval() 使用(代码注入风险)", "critical"),
    (r"(?<![\w.])compile\s*\(", "compile() 使用(谨慎)", "medium"),
    (r"(?<![\w.])__import__\s*\(", "__import__() 使用(谨慎)", "medium"),
    (r"pickle\.loads?\s*\(", "pickle 反序列化(任意代码执行风险)", "high"),
    (r"yaml\.load\s*\((?![^)]*Loader\s*=)", "yaml.load 不安全(需指定 SafeLoader)",
     "high"),
    (r"subprocess\.[a-z_]+\([^)]*shell\s*=\s*True", "subprocess shell=True (命令注入风险)",
     "high"),
    (r"os\.system\s*\(", "os.system() (命令注入风险)", "high"),
    (r"os\.popen\s*\(", "os.popen() (命令注入风险)", "high"),
    (r"(?<!\w)shell=True", "shell=True 使用", "medium"),
    (r"verify\s*=\s*False", "SSL 验证禁用", "high"),
    (r"http://(?!localhost|127\.0\.0\.1|0\.0\.0\.0|example)", "HTTP 明文(非 https)",
     "medium"),
    # 路径拼接(避免路径遍历)
    (r"open\s*\([^)]*\+[^)]*\)", "路径拼接(可能路径遍历)", "low"),
    # 硬编码临时目录
    (r'["\']/tmp/|["\']C:\\\\', "硬编码临时路径", "low"),
]


# 敏感信息泄露风险(日志/异常) — 更精确的匹配
# 只匹配全大写的常量名(如 SECRET, API_KEY),不匹配普通变量
LEAK_PATTERNS = [
    # 全大写常量名(更可能是凭据常量)
    (r"log(?:_info|_warn|_error|_debug)?\s*\([^)]*\b(?:SECRET|TOKEN|PASSWORD|API_KEY|CREDENTIAL|PRIVATE_KEY)\b",
     "日志中可能记录凭据常量", "high"),
    (r"print\s*\([^)]*\b(?:SECRET|TOKEN|PASSWORD|API_KEY|CREDENTIAL|PRIVATE_KEY)\b",
     "print 中可能输出凭据常量", "high"),
    (r"traceback\.print_exc", "traceback 输出(可能泄露内部细节)", "low"),
]


# 排除的文件/目录(不应扫描)
EXCLUDE_PATTERNS = [
    r"\.git/",
    r"\.vscode/",
    r"__pycache__/",
    r"\.pyc$",
    r"build/",
    r"dist/",
    r"\.egg-info/",
    r"node_modules/",
    r"logs/",
    r"HMOL_secrets_seal\.py$",  # 加密后的密文,本身合法
    r"HMOL_seal_secrets\.py$",  # 加密工具本身,需要明文凭据
    r"HMOL_security_audit\.py$",  # 审计工具自身
    r"^security_audit_report\.json$",
    r"^security_audit_report_\d{8,}\.json$",  # 带时间戳的历史报告
    r"^HMOL_security_audit_report.*\.json$",
    r"^111\.txt$",  # 安全改进清单自身
    r"^SECURITY_HARDENING\.md$",
    r"^docs/.*\.md$",
    r"test_",
    r"_test\.py$",
]


# ============================================================
# 扫描器
# ============================================================
class SecurityAuditor:
    """项目级安全审计器。"""

    def __init__(self, project_path: str, strict: bool = False):
        self.project_path = os.path.abspath(project_path)
        self.strict = strict
        self.findings: List[Dict] = []
        self.scanned_files: int = 0
        self.scanned_lines: int = 0

    def _should_exclude(self, path: str) -> bool:
        rel = os.path.relpath(path, self.project_path)
        for pat in EXCLUDE_PATTERNS:
            if re.search(pat, rel):
                return True
        return False

    def _scan_file(self, file_path: str) -> None:
        """扫描单个文件。"""
        if self._should_exclude(file_path):
            return
        if not file_path.endswith((".py", ".bat", ".sh", ".json", ".md", ".txt")):
            return
        try:
            with open(file_path, "r", encoding="utf-8", errors="ignore") as f:
                content = f.read()
        except Exception:
            return
        lines = content.split("\n")
        self.scanned_files += 1
        self.scanned_lines += len(lines)
        rel = os.path.relpath(file_path, self.project_path)
        for line_no, line in enumerate(lines, 1):
            # 跳过纯注释行(简化判断)
            stripped = line.strip()
            if stripped.startswith("#") and not stripped.startswith("#!") and "=" not in stripped:
                continue
            # 1. 硬编码凭据
            for pat, desc in HARDCODED_PATTERNS:
                if re.search(pat, line):
                    self.findings.append({
                        "rule_id": "HARDCODED",
                        "rule_name": desc,
                        "severity": "critical",
                        "file": rel,
                        "line": line_no,
                        "code": stripped[:200],
                        "recommendation": (
                            "移除硬编码凭据,使用 HMOL_secret_resolver.py 加密存储"
                            "或 HMOL_env_config.py 从环境变量加载"
                        ),
                    })
            # 2. 危险代码
            for pat, desc, sev in DANGEROUS_PATTERNS:
                if re.search(pat, line):
                    # 注释中的使用不算违规
                    if stripped.startswith("#"):
                        continue
                    self.findings.append({
                        "rule_id": "DANGEROUS",
                        "rule_name": desc,
                        "severity": sev,
                        "file": rel,
                        "line": line_no,
                        "code": stripped[:200],
                        "recommendation": self._get_recommendation(desc),
                    })
            # 3. 信息泄露(精确大小写 — 只匹配全大写常量名)
            for pat, desc, sev in LEAK_PATTERNS:
                if re.search(pat, line):
                    if stripped.startswith("#"):
                        continue
                    self.findings.append({
                        "rule_id": "LEAK",
                        "rule_name": desc,
                        "severity": sev,
                        "file": rel,
                        "line": line_no,
                        "code": stripped[:200],
                        "recommendation": "避免在日志中输出敏感信息",
                    })

    def _get_recommendation(self, desc: str) -> str:
        recs = {
            "eval": "使用 ast.literal_eval() 替代 eval()",
            "exec": "避免动态代码执行,如需使用请严格限制输入",
            "pickle": "使用 json 或 msgpack 等安全序列化",
            "yaml.load": "使用 yaml.safe_load()",
            "subprocess": "避免 shell=True,使用列表形式参数",
            "os.system": "使用 subprocess.run() 替代",
            "verify=False": "启用 SSL 证书验证",
        }
        for k, v in recs.items():
            if k in desc.lower():
                return v
        return "请审查并修复"

    def scan(self) -> Dict:
        """运行完整扫描。"""
        if not os.path.isdir(self.project_path):
            return {"error": f"路径不存在: {self.project_path}"}
        for root, dirs, files in os.walk(self.project_path):
            # 过滤目录
            dirs[:] = [d for d in dirs
                       if not any(re.search(p, os.path.join(root, d))
                                   for p in EXCLUDE_PATTERNS)]
            for fn in files:
                fp = os.path.join(root, fn)
                self._scan_file(fp)
        return self._summary()

    def _summary(self) -> Dict:
        """生成扫描摘要。"""
        severity_count: Dict[str, int] = {
            "critical": 0, "high": 0, "medium": 0, "low": 0, "info": 0,
        }
        for f in self.findings:
            severity_count[f["severity"]] = severity_count.get(f["severity"], 0) + 1
        # 严格模式: critical / high 必须为 0
        passed = True
        if self.strict:
            passed = (severity_count["critical"] == 0 and
                      severity_count["high"] == 0)
        else:
            passed = severity_count["critical"] == 0
        return {
            "passed": passed,
            "scanned_files": self.scanned_files,
            "scanned_lines": self.scanned_lines,
            "total_findings": len(self.findings),
            "severity_count": severity_count,
            "findings": self.findings,
        }


# ============================================================
# 输出格式
# ============================================================
def format_text(result: Dict) -> str:
    """格式化为可读文本。"""
    lines = []
    lines.append("=" * 70)
    lines.append("HMOL 安全审计报告")
    lines.append("=" * 70)
    lines.append(f"扫描文件: {result['scanned_files']}")
    lines.append(f"扫描行数: {result['scanned_lines']}")
    lines.append(f"发现问题: {result['total_findings']}")
    lines.append("")
    lines.append("按严重程度统计:")
    for sev, count in result["severity_count"].items():
        if count > 0:
            icon = {
                "critical": "🔴", "high": "🟠",
                "medium": "🟡", "low": "🔵", "info": "ℹ️",
            }.get(sev, "")
            lines.append(f"  {icon} {sev:10s}: {count}")
    lines.append("")
    lines.append(f"审计结果: {'✅ 通过' if result['passed'] else '❌ 未通过'}")
    if not result["passed"]:
        lines.append("")
        lines.append("关键问题:")
        for f in result["findings"]:
            if f["severity"] in ("critical", "high"):
                lines.append(
                    f"  [{f['severity'].upper()}] {f['file']}:{f['line']} "
                    f"— {f['rule_name']}"
                )
                if f.get("recommendation"):
                    lines.append(f"    建议: {f['recommendation']}")
    return "\n".join(lines)


# ============================================================
# 主入口
# ============================================================
def main():
    parser = argparse.ArgumentParser(description="HMOL 安全审计")
    parser.add_argument("path", nargs="?", default=".",
                        help="项目路径(默认:当前目录)")
    parser.add_argument("--strict", action="store_true",
                        help="严格模式(critical + high 必须为 0)")
    parser.add_argument("--json", action="store_true",
                        help="输出 JSON 格式")
    parser.add_argument("--output", type=str, default=None,
                        help="写入报告文件")
    args = parser.parse_args()

    auditor = SecurityAuditor(args.path, strict=args.strict)
    result = auditor.scan()
    if "error" in result:
        print(f"错误: {result['error']}", file=sys.stderr)
        return 1
    if args.json:
        text = json.dumps(result, indent=2, ensure_ascii=False)
    else:
        text = format_text(result)
    print(text)
    if args.output:
        try:
            with open(args.output, "w", encoding="utf-8") as f:
                if args.json:
                    f.write(text)
                else:
                    f.write(text + "\n")
        except Exception as e:
            print(f"写入失败: {e}", file=sys.stderr)
    return 0 if result["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
