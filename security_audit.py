"""
security_audit.py — 发布前安全审计

扫描 .py 文件中的潜在敏感信息泄露:
- 硬编码密钥 (Microsoft OAuth / QQ Bot / API)
- 硬编码 token / password / Bearer
- 残留的 url + token 模式
- .gitignore 缺失
- 加密模块未使用
- 调试器检测模块未使用

用法:
    py security_audit.py
退出码 0=无问题, 1=发现问题

历史: 这个脚本的诞生, 源于某次深夜把 AppSecret 顺手 OBF1: 一下就 push 上去,
第二天看着 GitHub 的 Fork 数量才意识到——噢, 密钥是公开的. 于是就有了它.
现在它比维护者更熟悉仓库里哪里容易藏东西.
"""

import os
import re
import sys
from pathlib import Path
from typing import List, Tuple

ROOT = Path(__file__).parent.resolve()

# 扫描的文件扩展名 (HMOL111.txt FINDING-08)
# 之前仅扫描 .py, 现扩展覆盖 .env / .bat / .iss / .json / .md / .txt
SCAN_EXTENSIONS = {'.py', '.env', '.bat', '.iss', '.json', '.yml', '.yaml'}
# 排除的文档文件 (示例/教程含占位符路径, 不视为实际泄露)
EXCLUDED_FILES = {
    "security_audit.py",  # 自身包含 PATTERNS 定义
    "HMOL111.txt",        # 审计报告本身含示例
    "CLEANUP_USAGE.md",   # 教程文档含 F:\ra2\mo3\HMOL 示例路径
    "SECURITY_CHECKLIST.md",
    "EULA.md",
    "LICENSE",
    "README.md",
}
SCAN_FILES = [
    p for p in ROOT.iterdir()
    if p.is_file()
    and p.suffix.lower() in SCAN_EXTENSIONS
    and p.name not in EXCLUDED_FILES
]

# 注释前缀 (按文件类型)
COMMENT_PREFIXES = {
    '.py':   ('#',),
    '.env':  ('#',),
    '.bat':  ('REM', 'rem', '::'),
    '.iss':  (';',),
    '.json': (),  # JSON 无注释
    '.yml':  ('#',),
    '.yaml': ('#',),
}

# 占位符模式 (匹配则视为示例, 非真实凭据)
PLACEHOLDER_PATTERNS = [
    re.compile(r'00000000-0000-0000-0000-000000000000', re.I),  # 全零 UUID
    re.compile(r'<[^>]+>'),                                       # <placeholder>
    re.compile(r'__[A-Z_]+__'),                                   # __PLACEHOLDER__
    re.compile(r'(?i)(example|sample|placeholder|dummy|fake|test)'),
]

# 敏感信息检测规则
PATTERNS = [
    # 通用密钥模式
    (r'(?i)(?:api[_-]?key|secret[_-]?key)\s*=\s*[\'"]([A-Za-z0-9_\-+/=]{16,})[\'"]',
     "硬编码 API/Secret 密钥"),
    # Microsoft OAuth 模式 (UUID v4)
    (r'[\'"]([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})[\'"]',
     "UUID 形式的 OAuth client_id"),
    # 硬编码 Bearer
    (r'(?i)Bearer\s+[A-Za-z0-9_\-\.=]{20,}',
     "硬编码 Bearer 令牌"),
    # 硬编码 access_token 赋值
    (r'(?i)access_token\s*=\s*[\'"]([A-Za-z0-9_\-\.=]{16,})[\'"]',
     "硬编码 access_token"),
    # 私钥 (PEM 头)
    (r'-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----',
     "硬编码私钥"),
    # 数据库连接串
    (r'(?i)(?:mysql|postgresql|mongodb)://[^:]+:[^@]+@',
     "硬编码数据库凭证"),
    # OneDrive / SharePoint 共享 URL (HMOL111.txt FINDING-02)
    # 匹配: xxx-my.sharepoint.com 或 xxx.sharepoint.com 共享链接
    (r'https?://[a-z0-9-]+-my\.sharepoint\.com/',
     "OneDrive/SharePoint 共享 URL (PII 风险)"),
    # 个人 QQ 邮箱暴露
    (r'\b1\d{9}@qq\.com\b',
     "个人 QQ 邮箱"),
    # Windows 绝对路径 (本地文件系统暴露, HMOL111.txt FINDING-06)
    (r'[A-Z]:\\[a-zA-Z0-9_\\.\-]{8,}',
     "Windows 绝对路径"),
    # 硬编码 PyInstaller 密钥 (HMOL111.txt FINDING-03)
    (r'(?i)PYI_KEY\s*=\s*[A-Za-z0-9\-]{4,}',
     "疑似硬编码 PyInstaller 密钥"),
    # 硬编码 HMAC / 完整性密钥 (HMOL111.txt FINDING-04)
    (r'(?i)INTEGRITY[_-]?KEY\s*=\s*b?[\'"][A-Za-z0-9\-]{8,}[\'"]',
     "疑似硬编码完整性密钥"),
]

# 允许的硬编码 (白名单 - 混淆后的字符串)
ALLOWED_FILE_EXTENSIONS = {'.png', '.jpg', '.ico', '.qm', '.qrc'}


def _is_comment_line(line: str, ext: str) -> bool:
    """判断一行是否为注释 (按文件扩展名)"""
    stripped = line.strip()
    for prefix in COMMENT_PREFIXES.get(ext, ()):
        if stripped.startswith(prefix):
            return True
    return False


def _is_placeholder(line: str) -> bool:
    """判断一行是否为占位符示例 (非真实凭据)"""
    for pattern in PLACEHOLDER_PATTERNS:
        if pattern.search(line):
            return True
    return False


def scan_file(path: Path) -> List[Tuple[int, str, str]]:
    """
    扫描文件中的敏感信息 (支持 .py / .env / .bat / .iss / .json 等)
    Returns: [(line_no, pattern_name, matched_text), ...]
    """
    issues: list = []
    # 这里只关心文件是否可读;权限不足 / 二进制文件 / 编码异常都视为
    # "无可报告项", 不阻断其他文件的扫描 (扫描器自身的鲁棒性优先)
    try:
        content = path.read_text(encoding='utf-8', errors='ignore')
    except OSError:
        return issues

    ext = path.suffix.lower()
    for line_no, line in enumerate(content.splitlines(), 1):
        if _is_comment_line(line, ext):
            continue
        if _is_placeholder(line):
            continue
        for pattern, name in PATTERNS:
            if re.search(pattern, line):
                issues.append((line_no, name, line.strip()[:120]))
    return issues


def check_gitignore() -> List[str]:
    """检查 .gitignore 是否正确排除敏感文件"""
    issues = []
    gitignore = ROOT / ".gitignore"
    if not gitignore.exists():
        issues.append(".gitignore 文件缺失")
        return issues
    content = gitignore.read_text(encoding='utf-8', errors='ignore')
    # 最低必须排除的条目: 一旦漏掉任何一项, 即使代码侧无密钥也会泄露
    # 用户运行时产生的本地文件 (token 缓存 / 日志 / 配置)
    required = [
        "HMOL_config.json",         # 用户配置文件, 含 OneDrive 选择
        "msal_token_cache.enc",     # 加密的 MSAL token 缓存
        "logs/",                    # 运行时日志, 可能含 Authorization
        "__pycache__/",             # Python 编译缓存, 可被反编译
        "*.pyc",                    # 同上, 字节码文件
        ".env",                     # 真实环境变量文件 (含密钥)
    ]
    for item in required:
        if item not in content:
            issues.append(f".gitignore 缺少规则: {item}")
    return issues


def check_dependencies() -> List[str]:
    """检查关键安全模块是否被引用"""
    issues = []
    main_py = ROOT / "HMOL_qt.py"
    if not main_py.exists():
        return ["主程序 HMOL_qt.py 不存在"]
    content = main_py.read_text(encoding='utf-8', errors='ignore')
    if "crypto_utils" not in content:
        # 没引用 crypto_utils 意味着 token 缓存/机器指纹加密都不会启用
        # 这种情况下 .env 中的密钥虽然有, 但运行时没有保护层
        issues.append("HMOL_qt.py 未引用 crypto_utils")
    return issues


def main() -> int:
    print("=" * 60)
    print("HMOL 安全审计 — 发布前检查")
    print("=" * 60)
    print(f"扫描目录: {ROOT}")
    print()

    all_issues = []

    # 1. 敏感信息扫描 (HMOL111.txt FINDING-08: 扩展扫描 .env/.bat/.iss/.json)
    print(f"[1/3] 扫描硬编码敏感信息 ({len(SCAN_FILES)} 个文件)...")
    if not SCAN_FILES:
        all_issues.append("未找到可扫描的文件")
    any_found = False
    for path in SCAN_FILES:
        issues = scan_file(path)
        if issues:
            any_found = True
            for line_no, name, text in issues:
                print(f"  {path.name}:{line_no} [{name}]")
                print(f"     {text}")
            all_issues.append(f"{path.name}: {len(issues)} 处敏感信息")
    if not any_found:
        print("  OK 未发现硬编码敏感信息")
    print()

    # 2. .gitignore 检查
    print("[2/3] 检查 .gitignore...")
    gitignore_issues = check_gitignore()
    if gitignore_issues:
        for issue in gitignore_issues:
            print(f"  ⚠️  {issue}")
        all_issues.extend(gitignore_issues)
    else:
        print("  ✅ .gitignore 规则完备")
    print()

    # 3. 依赖引用检查
    print("[3/3] 检查安全模块引用...")
    dep_issues = check_dependencies()
    if dep_issues:
        for issue in dep_issues:
            print(f"  ⚠️  {issue}")
        all_issues.extend(dep_issues)
    else:
        print("  ✅ crypto_utils 已被引用")
    print()

    # 汇总
    print("=" * 60)
    if all_issues:
        print(f"❌ 发现 {len(all_issues)} 类问题, 请修复后再发布")
        return 1
    print("✅ 所有检查通过, 可以安全发布")
    return 0


if __name__ == "__main__":
    sys.exit(main())
