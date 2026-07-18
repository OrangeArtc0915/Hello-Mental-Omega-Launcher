"""
cleanup_temp.py — HMOL 临时文件自动清理工具

功能:
- 清理缓存文件、日志文件、临时下载文件
- 支持指定目录、文件类型、保留时间
- 日志记录 (时间、文件数、字节数)
- 干跑模式 (--dry-run) 仅显示不删除
- 安全: 白名单保护关键文件
"""

import os
import sys
import argparse
import time
import json
import logging
from pathlib import Path
from datetime import datetime, timedelta
from typing import List, Dict, Tuple

# 脚本版本
SCRIPT_VERSION = "1.0"

# 默认配置
DEFAULT_CONFIG = {
    "log_file": "cleanup_temp.log",
    "log_level": "INFO",
    "log_max_bytes": 1 * 1024 * 1024,  # 1MB
    "log_backup_count": 3,
}

# 文件类型分类
FILE_CATEGORIES = {
    "cache": [".cache", ".tmp", ".temp", ".bak", ".old", ".swp", ".swo"],
    "log": [".log", ".log.1", ".log.2"],
    "download": [".crdownload", ".part", ".download", ".partial"],
    "compile": [".pyc", ".pyo", ".pyd", ".o", ".obj"],
    "python": ["__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache"],
    "editor": ["~", ".swp", ".swo", ".vscode-tmp"],
    "system": [".ds_store", "thumbs.db", "desktop.ini"],
}

# 默认排除的关键文件/目录 (绝对不能删)
PROTECTED_PATTERNS = {
    "HMOL_qt.py", "crypto_utils.py", "anti_debug.py",
    "input_validation.py", "rate_limiter.py", "security_audit.py",
    "obfuscate.py", "cleanup_temp.py",
    "HMOL_config.json", "msal_token_cache.enc",
    "README.md", "LICENSE", "EULA.md", "requirements.txt",
    "icon.ico", "build.bat", "build_enhanced.bat",
    ".git", ".gitignore", ".env", ".env.example",
}


def setup_logging(log_file: str, level: str = "INFO") -> logging.Logger:
    """初始化日志记录器 (支持日志轮转)"""
    from logging.handlers import RotatingFileHandler

    logger = logging.getLogger("cleanup_temp")
    logger.setLevel(getattr(logging, level.upper(), logging.INFO))
    # 阻止向上传播到 root logger, 避免重复输出
    logger.propagate = False
    logger.handlers.clear()

    # 文件 handler (带轮转)
    try:
        fh = RotatingFileHandler(
            log_file,
            maxBytes=DEFAULT_CONFIG["log_max_bytes"],
            backupCount=DEFAULT_CONFIG["log_backup_count"],
            encoding="utf-8",
        )
        fh.setFormatter(logging.Formatter(
            "[%(asctime)s] [%(levelname)s] %(message)s",
            datefmt="%Y-%m-%d %H:%M:%S"
        ))
        logger.addHandler(fh)
    except Exception as e:
        print(f"[WARN] 无法创建日志文件: {e}", file=sys.stderr)

    # 控制台 handler
    ch = logging.StreamHandler(sys.stdout)
    ch.setFormatter(logging.Formatter("[%(levelname)s] %(message)s"))
    logger.addHandler(ch)
    return logger


def is_protected(path: Path) -> bool:
    """检查文件/目录是否受保护 (白名单)"""
    name = path.name
    if name in PROTECTED_PATTERNS:
        return True
    # 保护关键目录
    for protected in {".git", "logs"}:
        if protected in path.parts:
            return True
    return False


def matches_extension(path: Path, extensions: List[str]) -> bool:
    """检查文件扩展名是否匹配"""
    if not extensions:
        return True
    name = path.name.lower()
    for ext in extensions:
        if ext.startswith("."):
            if name.endswith(ext.lower()):
                return True
        else:
            if name == ext.lower():
                return True
    return False


def is_older_than(path: Path, days: int) -> bool:
    """检查文件/目录是否早于指定天数"""
    try:
        mtime = path.stat().st_mtime
        cutoff = time.time() - (days * 86400)
        return mtime < cutoff
    except (OSError, FileNotFoundError):
        return False


def scan_directory(
    target_dir: Path,
    extensions: List[str],
    keep_days: int,
    logger: logging.Logger
) -> List[Tuple[Path, int]]:
    """
    扫描目录, 找出符合清理条件的文件
    Returns: [(file_path, file_size), ...]
    """
    candidates = []
    if not target_dir.exists():
        logger.warning(f"目录不存在: {target_dir}")
        return candidates

    skipped_protected = 0
    skipped_extension = 0
    skipped_recent = 0

    for root, dirs, files in os.walk(target_dir):
        root_path = Path(root)
        # 跳过保护的目录 (如 .git, logs)
        dirs[:] = [d for d in dirs if not is_protected(root_path / d)]

        for filename in files:
            file_path = root_path / filename

            # 白名单检查
            if is_protected(file_path):
                skipped_protected += 1
                continue

            # 扩展名检查
            if not matches_extension(file_path, extensions):
                skipped_extension += 1
                continue

            # 时间检查
            if not is_older_than(file_path, keep_days):
                skipped_recent += 1
                continue

            # 大小
            try:
                size = file_path.stat().st_size
                candidates.append((file_path, size))
            except (OSError, FileNotFoundError):
                continue

    logger.debug(
        f"扫描完成: 命中 {len(candidates)} | "
        f"保护跳过 {skipped_protected} | "
        f"类型不匹配 {skipped_extension} | "
        f"时间未到 {skipped_recent}"
    )
    return candidates


def format_size(size_bytes: int) -> str:
    """格式化字节数为人类可读"""
    if size_bytes < 1024:
        return f"{size_bytes} B"
    elif size_bytes < 1024 * 1024:
        return f"{size_bytes / 1024:.2f} KB"
    elif size_bytes < 1024 * 1024 * 1024:
        return f"{size_bytes / (1024 * 1024):.2f} MB"
    else:
        return f"{size_bytes / (1024 * 1024 * 1024):.2f} GB"


def clean_files(
    files: List[Tuple[Path, int]],
    dry_run: bool,
    logger: logging.Logger
) -> Dict[str, int]:
    """
    执行清理
    Returns: {"deleted": N, "failed": N, "total_size": bytes}
    """
    result = {"deleted": 0, "failed": 0, "total_size": 0, "skipped": 0}

    for file_path, size in files:
        if dry_run:
            logger.info(f"[DRY-RUN] 将删除: {file_path} ({format_size(size)})")
            result["skipped"] += 1
            continue
        try:
            file_path.unlink()
            result["deleted"] += 1
            result["total_size"] += size
            logger.debug(f"已删除: {file_path}")
        except PermissionError:
            logger.warning(f"无权限: {file_path}")
            result["failed"] += 1
        except FileNotFoundError:
            # 并发删除时正常
            pass
        except Exception as e:
            logger.error(f"删除失败 {file_path}: {e}")
            result["failed"] += 1

    return result


def print_summary(
    target_dirs: List[Path],
    extensions: List[str],
    keep_days: int,
    dry_run: bool,
    result: Dict[str, int],
    elapsed: float,
    logger: logging.Logger
):
    """打印清理摘要"""
    logger.info("=" * 60)
    logger.info("清理摘要 / Cleanup Summary")
    logger.info("=" * 60)
    logger.info(f"  目标目录: {', '.join(str(d) for d in target_dirs)}")
    logger.info(f"  文件类型: {', '.join(extensions) if extensions else '(全部)'}")
    logger.info(f"  保留天数: {keep_days}")
    logger.info(f"  模式: {'干跑 (DRY-RUN)' if dry_run else '实际删除'}")
    logger.info(f"  耗时: {elapsed:.2f} 秒")
    logger.info("-" * 60)
    logger.info(f"  成功删除: {result['deleted']} 个文件")
    logger.info(f"  删除失败: {result['failed']} 个文件")
    logger.info(f"  释放空间: {format_size(result['total_size'])}")
    if dry_run:
        logger.info(f"  干跑跳过: {result['skipped']} 个文件")
    logger.info("=" * 60)


def parse_args() -> argparse.Namespace:
    """解析命令行参数"""
    parser = argparse.ArgumentParser(
        description="HMOL 临时文件清理工具 v" + SCRIPT_VERSION,
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
示例:
  # 清理 PyInstaller 中间产物 (超过 7 天)
  py cleanup_temp.py --dir ./build --dir ./dist --category compile --days 7

  # 清理 HMOL 日志 (干跑模式预览)
  py cleanup_temp.py --dir ./logs --category log --dry-run

  # 清理所有 .tmp 缓存 (超过 1 天)
  py cleanup_temp.py --dir . --ext .tmp --ext .cache --days 1

  # 清理 __pycache__ 和 .pyc
  py cleanup_temp.py --dir . --category compile --days 0
        """
    )

    parser.add_argument(
        "--dir", "-d", action="append", required=True,
        help="要清理的目录 (可多次指定)"
    )
    parser.add_argument(
        "--ext", "-e", action="append", default=[],
        help="文件扩展名 (如 .log, .tmp), 可多次指定"
    )
    parser.add_argument(
        "--category", "-c", action="append", default=[],
        choices=list(FILE_CATEGORIES.keys()),
        help="预定义类型: cache / log / download / compile / python / editor / system"
    )
    parser.add_argument(
        "--days", "-D", type=int, default=7,
        help="保留天数 (默认 7, 0=全部清理)"
    )
    parser.add_argument(
        "--dry-run", "-n", action="store_true",
        help="干跑模式, 仅显示不删除"
    )
    parser.add_argument(
        "--log-file", "-l", default=DEFAULT_CONFIG["log_file"],
        help=f"日志文件路径 (默认 {DEFAULT_CONFIG['log_file']})"
    )
    parser.add_argument(
        "--log-level", default="INFO",
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
        help="日志级别 (默认 INFO)"
    )
    parser.add_argument(
        "--quiet", "-q", action="store_true",
        help="静默模式, 仅输出错误"
    )
    parser.add_argument(
        "--yes", "-y", action="store_true",
        help="跳过确认提示"
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()

    # 收集所有扩展名
    extensions = list(args.ext or [])
    for cat in args.category or []:
        extensions.extend(FILE_CATEGORIES.get(cat, []))

    # 去重
    extensions = list(dict.fromkeys(extensions))

    # 解析目录
    target_dirs = [Path(d).resolve() for d in args.dir]

    # 初始化日志
    log_level = "WARNING" if args.quiet else args.log_level
    logger = setup_logging(args.log_file, log_level)

    logger.info(f"HMOL 临时文件清理工具 v{SCRIPT_VERSION} 启动")
    logger.info(f"  目标: {[str(d) for d in target_dirs]}")
    logger.info(f"  扩展名: {extensions if extensions else '(全部)'}")
    logger.info(f"  保留: {args.days} 天")

    # 确认提示 (非干跑且无 --yes)
    if not args.dry_run and not args.yes and not args.quiet:
        print(f"\n即将清理以下目录:")
        for d in target_dirs:
            print(f"  - {d}")
        print(f"扩展名: {extensions if extensions else '(全部)'}")
        print(f"保留天数: {args.days} 天")
        try:
            confirm = input("确认执行? (yes/no): ").strip().lower()
            if confirm not in ("yes", "y"):
                print("已取消")
                return 0
        except EOFError:
            # 非交互式
            pass

    start_time = time.time()
    all_candidates = []
    for target_dir in target_dirs:
        candidates = scan_directory(target_dir, extensions, args.days, logger)
        all_candidates.extend(candidates)
        logger.info(f"扫描 {target_dir}: 找到 {len(candidates)} 个候选文件")

    if not all_candidates:
        logger.info("没有符合条件的文件, 退出")
        print_summary(target_dirs, extensions, args.days, args.dry_run,
                      {"deleted": 0, "failed": 0, "total_size": 0, "skipped": 0},
                      time.time() - start_time, logger)
        return 0

    total_size = sum(s for _, s in all_candidates)
    logger.info(f"共找到 {len(all_candidates)} 个文件, 总大小 {format_size(total_size)}")

    # 执行清理
    result = clean_files(all_candidates, args.dry_run, logger)

    elapsed = time.time() - start_time
    print_summary(target_dirs, extensions, args.days, args.dry_run, result, elapsed, logger)

    return 0 if result["failed"] == 0 else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\n[INTERRUPT] 用户取消")
        sys.exit(130)
    except Exception as e:
        print(f"[FATAL] {e}", file=sys.stderr)
        sys.exit(2)
