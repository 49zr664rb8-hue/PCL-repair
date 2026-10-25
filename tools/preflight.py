#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建前预检：确认工程引用的文件全都在磁盘上。

在按下生成之前跑一遍，可以提前发现「某个图片/依赖不见了」这类会让 msbuild 中途
报错的问题，比等到构建失败再回头找省事。

    python tools/preflight.py --src PCL3-src

检查四类引用：
  1. 工程文件里 Compile / Page / Resource / None / EmbeddedResource 指向的文件
  2. <Reference> 的 HintPath
  3. <ProjectReference> 的目标工程
  4. My Project\\Resources.resx 里 ResXFileRef 内嵌的二进制（运行期从这里取 DLL）

已知无害项（脚本会单独标注，不算失败）：
  Resources\\java-wrapper.jar 与 Resources\\lwjgl-unsafe-agent.jar 在 vbproj 里
  各有一条 <None>，但文件并不存在。这两个 jar 实际由 PCLCS 内嵌、运行时通过
  LaunchUtils.ExtractPatch 释放，PCL 自己不用它们，所以这两条是上游遗留的无效条目。
"""

from __future__ import annotations

import argparse
import io
import os
import re
import sys

# 上游遗留的无效 None 条目，缺了不影响构建与运行
KNOWN_HARMLESS = {
    "resources\\java-wrapper.jar",
    "resources\\lwjgl-unsafe-agent.jar",
}


def main() -> int:
    parser = argparse.ArgumentParser(description="PCL3 构建前文件预检")
    parser.add_argument("--src", default="PCL3-src", help="源码根目录")
    args = parser.parse_args()

    project_dir = os.path.join(os.path.abspath(args.src), "Plain Craft Launcher 2")
    proj_path = os.path.join(project_dir, "Plain Craft Launcher 2.vbproj")
    if not os.path.exists(proj_path):
        print(f"找不到工程文件：{proj_path}")
        return 2

    proj = io.open(proj_path, encoding="utf-8-sig").read()
    os.chdir(project_dir)

    missing: list[str] = []
    harmless: list[str] = []
    checked = 0

    def verify(kind: str, raw: str) -> None:
        nonlocal checked
        rel = raw.replace("\\", os.sep)
        checked += 1
        if os.path.exists(rel):
            return
        if raw.lower().replace("/", "\\") in KNOWN_HARMLESS:
            harmless.append(f"{kind}: {raw}")
        else:
            missing.append(f"{kind}: {raw}")

    for kind in ("Compile", "Page", "Resource", "None", "EmbeddedResource", "ApplicationDefinition"):
        for match in re.finditer(rf'<{kind}\s+Include="([^"]+)"', proj):
            verify(kind, match.group(1))

    for match in re.finditer(r'<Reference\s+Include="([^"]+)"\s*>(.*?)</Reference>', proj, re.S):
        hint = re.search(r"<HintPath>([^<]*)</HintPath>", match.group(2))
        if hint:
            verify("HintPath", hint.group(1))

    for match in re.finditer(r'<ProjectReference\s+Include="([^"]+)"', proj):
        verify("ProjectReference", match.group(1))

    resx = os.path.join("My Project", "Resources.resx")
    embedded = 0
    if os.path.exists(resx):
        text = io.open(resx, encoding="utf-8-sig").read()
        for ref in re.findall(r"<value>([^<;]+);System\.Byte\[\]", text):
            embedded += 1
            path = os.path.normpath(os.path.join("My Project", ref.replace("\\", os.sep)))
            if not os.path.exists(path):
                missing.append(f"ResXFileRef: {ref}  ->  {path}")

    print(f"检查了 {checked} 个工程引用 + {embedded} 个 resx 内嵌文件")
    if harmless:
        print(f"\n已知无害的遗留条目 {len(harmless)} 个（不影响构建）：")
        for item in harmless:
            print("  - " + item)
    if missing:
        print(f"\n❌ 真正缺失 {len(missing)} 个：")
        for item in missing:
            print("  ! " + item)
        return 1
    print("\n✅ 工程引用的文件全部就位，可以开始构建")
    return 0


if __name__ == "__main__":
    sys.exit(main())
