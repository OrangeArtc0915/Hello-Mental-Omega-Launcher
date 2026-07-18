"""
obfuscate.py — HMOL 字符串混淆工具

为新增的硬编码字符串生成 OBF1: 格式的混淆值
用法:
    py obfuscate.py "my_secret"
    py obfuscate.py --file input.txt

混淆说明:
- XOR 加密 (固定密钥, 非安全加密, 仅反爬虫)
- Base64 编码
- 前缀 "OBF1:" 标识版本
- 反混淆通过 crypto_utils.deobfuscate_string()
"""

import sys
from pathlib import Path

# 引入本地工具
sys.path.insert(0, str(Path(__file__).parent))
from crypto_utils import obfuscate_string, deobfuscate_string


def main():
    if len(sys.argv) < 2:
        print("用法:")
        print('  py obfuscate.py "明文字符串"')
        print("  py obfuscate.py --file <filename>")
        print("  py obfuscate.py --verify 'OBF1:xxxx'")
        sys.exit(1)

    if sys.argv[1] == "--file":
        if len(sys.argv) < 3:
            print("错误: --file 需要文件名")
            sys.exit(1)
        path = Path(sys.argv[2])
        if not path.exists():
            print(f"错误: 文件不存在: {path}")
            sys.exit(1)
        # 限制单行 64KB, 防止恶意超大行 OOM
        try:
            text = path.read_text(encoding='utf-8', errors='replace')
        except OSError as e:
            print(f"错误: 读取文件失败: {e}")
            sys.exit(1)
        for line in text.splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            if len(line) > 65536:
                print(f"警告: 跳过超长行 ({len(line)} chars)")
                continue
            obf = obfuscate_string(line)
            print(f'{line!r} -> {obf!r}')
    elif sys.argv[1] == "--verify":
        if len(sys.argv) < 3:
            print("错误: --verify 需要混淆字符串")
            sys.exit(1)
        result = deobfuscate_string(sys.argv[2])
        print(f"还原结果: {result!r}")
    else:
        plaintext = sys.argv[1]
        obf = obfuscate_string(plaintext)
        print(f"明文: {plaintext!r}")
        print(f"混淆: {obf!r}")
        # 自验
        back = deobfuscate_string(obf)
        print(f"还原: {back!r}")
        if back != plaintext:
            print("[ERROR] 还原失败!")
            sys.exit(1)
        print("[OK] 往返验证通过")


if __name__ == "__main__":
    main()
