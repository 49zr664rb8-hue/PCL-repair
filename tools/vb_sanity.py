#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""VB.NET 源码结构检查（不是编译器，只做能可靠判定的静态校验）。

本仓库在没有 Visual Studio 的环境里无法编译，这个脚本用来兜住
「括号没配对 / End Sub 多了一个」这类低级错误。

刻意只检查可靠项，避免误报：
  · 全文件圆括号配平（先剔除字符串字面量与注释）
  · Sub / Function / Class / Module / Using / Try / While / Do / For / With / Select 配平
不检查 If 与 Property——单行 If、多行条件、自动属性都会让启发式判断失真。

用法：
    python tools/vb_sanity.py "Plain Craft Launcher 2/Modules/Minecraft/ModMultiplayer.vb"
"""

from __future__ import annotations

import sys
from collections import Counter

MODIFIERS = r"(?:Public|Private|Friend|Protected|Shared|Overloads|Overrides|Partial|Async|Iterator|ReadOnly|WriteOnly|MustOverride|NotOverridable|Default|Custom|Narrowing|Widening|Shadows|Static|Optional|ParamArray|\s)*"

# (开始块正则, 结束块正则, 名称)
BLOCKS = [
    (rf"^\s*{MODIFIERS}\bSub\b", r"^\s*End Sub\b", "Sub"),
    (rf"^\s*{MODIFIERS}\bFunction\b", r"^\s*End Function\b", "Function"),
    (rf"^\s*{MODIFIERS}\bClass\b", r"^\s*End Class\b", "Class"),
    (rf"^\s*{MODIFIERS}\bModule\b", r"^\s*End Module\b", "Module"),
    (rf"^\s*{MODIFIERS}\bStructure\b", r"^\s*End Structure\b", "Structure"),
    (r"^\s*Using\b", r"^\s*End Using\b", "Using"),
    (r"^\s*Try\b", r"^\s*End Try\b", "Try"),
    (r"^\s*While\b", r"^\s*End While\b", "While"),
    (r"^\s*Do\b", r"^\s*Loop\b", "Do"),
    (r"^\s*For\b", r"^\s*Next\b", "For"),
    (r"^\s*With\b", r"^\s*End With\b", "With"),
    (r"^\s*Select\b", r"^\s*End Select\b", "Select"),
    (r"^\s*Namespace\b", r"^\s*End Namespace\b", "Namespace"),
]


def strip_noise(text: str) -> str:
    """剔除字符串字面量与注释，保留其余字符（含换行），用于全局配平。"""
    out: list[str] = []
    in_string = False
    i = 0
    length = len(text)
    while i < length:
        ch = text[i]
        if in_string:
            if ch == '"':
                if i + 1 < length and text[i + 1] == '"':
                    i += 2
                    continue
                in_string = False
            elif ch in "\r\n":
                out.append(ch)  # 保留换行，行号仍能对齐
            i += 1
            continue
        if ch == '"':
            in_string = True
            i += 1
            continue
        if ch == "'":
            while i < length and text[i] not in "\r\n":
                i += 1
            continue
        out.append(ch)
        i += 1
    return "".join(out)


def check_quotes(path: str) -> list[str]:
    """逐行检查双引号数量是否为偶数。

    VB 没有跨行字符串（除了跨行的插值字符串拼接），所以一行里引号数必须是偶数，
    否则说明字符串没闭合——这是最隐蔽、最难靠肉眼发现的一类编译错误。
    注释部分不参与统计。
    """
    problems: list[str] = []
    with open(path, encoding="utf-8-sig") as handle:
        lines = handle.read().splitlines()

    for lineno, raw in enumerate(lines, 1):
        # 找到注释起点（字符串内部的 ' 不算）
        in_string = False
        cut = len(raw)
        i = 0
        while i < len(raw):
            ch = raw[i]
            if in_string:
                if ch == '"':
                    if i + 1 < len(raw) and raw[i + 1] == '"':
                        i += 2
                        continue
                    in_string = False
            elif ch == '"':
                in_string = True
            elif ch == "'":
                cut = i
                break
            i += 1

        code = raw[:cut]
        if code.count('"') % 2 != 0:
            problems.append(f"{path}:{lineno}: 引号数量为奇数，字符串可能没闭合 -> {code.strip()[:70]}")
    return problems


def check(path: str) -> list[str]:
    problems: list[str] = []
    with open(path, encoding="utf-8-sig") as handle:
        text = handle.read()

    clean = strip_noise(text)

    # 1. 圆括号配平
    opens = clean.count("(")
    closes = clean.count(")")
    if opens != closes:
        problems.append(f"{path}: 圆括号不配平（( = {opens}，) = {closes}）")

    # 2. 块配平
    counters: Counter[str] = Counter()
    lines = clean.splitlines()
    for lineno, line in enumerate(lines, 1):
        for start_pat, end_pat, name in BLOCKS:
            import re

            is_end = re.match(end_pat, line, re.IGNORECASE) is not None
            is_start = re.match(start_pat, line, re.IGNORECASE) is not None
            if is_end:
                counters[name] -= 1
                if counters[name] < 0:
                    problems.append(f"{path}:{lineno}: 多余的 End {name}")
            elif is_start:
                counters[name] += 1

    for name, count in counters.items():
        if count != 0:
            problems.append(f"{path}: {name} 块未配平，差值 {count}")

    problems.extend(check_quotes(path))
    return problems


def main() -> int:
    args = sys.argv[1:]
    baseline_rev = None
    if "--baseline" in args:
        index = args.index("--baseline")
        baseline_rev = args[index + 1]
        del args[index:index + 2]

    if not args:
        print("用法：python tools/vb_sanity.py <文件...> [--baseline <git 修订>]")
        print("  加 --baseline 时，只报告相对该修订新增的问题（上游本身存在误报，用差值判断才有意义）")
        return 2

    all_problems: list[str] = []
    for path in args:
        problems = check(path)
        if baseline_rev:
            base_problems = check_git(baseline_rev, path)
            if base_problems is None:
                print(f"[新增文件，{len(problems)} 处可疑] {path}")
            else:
                added = len(problems) - len(base_problems)
                if added > 0:
                    print(f"[回归 {added} 处] {path}")
                else:
                    print(f"[无回归] {path}")
                    problems = []
        else:
            print(f"[{'OK' if not problems else str(len(problems)) + ' 处可疑'}] {path}")
        all_problems.extend(problems)

    for problem in all_problems:
        print("  ! " + problem)
    return 1 if all_problems else 0


def check_git(rev: str, path: str) -> list[str] | None:
    """取某个 git 修订里的同名文件做同样检查；文件不存在返回 None。"""
    import subprocess
    import tempfile

    try:
        result = subprocess.run(
            ["git", "show", f"{rev}:{path}"],
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
    except OSError:
        return None
    if result.returncode != 0 or not result.stdout:
        return None
    with tempfile.NamedTemporaryFile("w", suffix=".vb", delete=False, encoding="utf-8") as handle:
        handle.write(result.stdout)
        temp_path = handle.name
    try:
        return check(temp_path)
    finally:
        import os

        os.unlink(temp_path)


if __name__ == "__main__":
    sys.exit(main())
