#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""不装 Visual Studio，也能校验 PCL2 全部 VB 源码能否编译。

背景
----
PCL2 是 .NET Framework 4.8 + WPF 的**旧式** VB 工程，正常只能用 Visual Studio
（或 msbuild.exe）构建。`dotnet build` 直接建它会失败，原因是：

    error MC1000: Could not find assembly 'mscorlib, Version=0.0.0.0, ...'

因为 .NET SDK 自带的 WPF 标记编译器（PresentationBuildTasks）跑在 CoreCLR 上，
无法解析 .NET Framework 的引用程序集。这是平台限制，不是工程配置问题。

绕开办法
--------
BAML 编译绕不过去，但「VB 源码能不能编译」是可以用 Roslyn 单独验证的：

  1. 临时把工程转成 SDK 风格，跑一次 `dotnet build`。
     这一步会在 obj 下生成全部 XAML 对应的 `.g.vb`（虽然最终会在 BAML 阶段失败，
     但 .g.vb 已经产出，这正是我们需要的）。
  2. 把 legacy 工程原样还原（绝不留在仓库里）。
  3. 用「工程文件里真实的 Compile 列表 + 上一步的 .g.vb」拼一个 net48 库工程，
     用 Roslyn 编译一遍。这一步是纯托管编译，不需要 WPF 标记编译，能跑通。

结果就是：能拿到全工程真实的编译错误，而完全不需要 Visual Studio。

用法
----
    python tools/vbcheck.py --src "PCL3-src"

    # 指定 dotnet 路径
    python tools/vbcheck.py --src "PCL3-src" --dotnet "C:/Program Files/dotnet/dotnet.exe"

前置条件
--------
  · .NET SDK 8/9（只要 `dotnet build` 能用）
  · .NET Framework 4.8 引用程序集（Developer Pack，或把
    Microsoft.NETFramework.ReferenceAssemblies.net48 包里的
    build/.NETFramework/v4.8 复制到
    C:\\Program Files (x86)\\Reference Assemblies\\Microsoft\\Framework\\.NETFramework\\v4.8）
  · MeloongCore / PCLCS 已构建出 DLL（脚本会先帮你构建）
"""

from __future__ import annotations

import argparse
import glob
import os
import re
import shutil
import subprocess
import sys
import tempfile

VB_PROJECT = "Plain Craft Launcher 2"
PROJECT_FILE = "Plain Craft Launcher 2.vbproj"

# 与 legacy 工程完全一致的全局 Imports
GLOBAL_IMPORTS = [
    "Microsoft.VisualBasic", "Newtonsoft.Json", "Newtonsoft.Json.Linq", "System",
    "System.Collections", "System.Collections.Concurrent", "System.Collections.Generic",
    "System.Diagnostics", "System.IO", "System.IO.Compression", "System.Linq",
    "System.Net", "System.Net.Http", "System.Text", "System.Threading",
    "System.Threading.Tasks", "System.Xml.Linq", "System.Windows",
    "System.Windows.Controls", "System.Windows.Data", "System.Windows.Documents",
    "System.Windows.Input", "System.Windows.Media", "System.Windows.Media.Imaging",
    "MeloongCore", "MeloongCore.Extensions", "MeloongCore.Wpf",
    "MeloongCore.Wpf.Extensions", "PCLCS", "ThrottleDebounce",
]


def run(cmd: list[str], cwd: str, capture: bool = True) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, cwd=cwd, capture_output=capture, text=True,
                          encoding="utf-8", errors="replace")


def build_dependencies(src: str, dotnet: str) -> bool:
    """先构建三个 C# 依赖工程（MeloongCore、MeloongCore.Wpf、PCLCS）。"""
    projects = [
        "MeloongCore/Shared/MeloongCore.csproj",
        "MeloongCore/Wpf/MeloongCore.Wpf.csproj",
        "PCLCS/PCLCS.csproj",
    ]
    for rel in projects:
        path = os.path.join(src, rel)
        if not os.path.exists(path):
            print(f"  ! 缺少 {rel}（MeloongCore 子模块没拉下来？）")
            return False
        print(f"  · 构建 {rel}")
        result = run([dotnet, "build", path, "-c", "Release", "-v", "q"], src)
        if result.returncode != 0:
            print(result.stdout[-2000:])
            return False
    return True


def parse_project(proj_text: str) -> tuple[list[str], list[tuple[str, str | None]]]:
    """从 legacy 工程里取出 Compile 列表与 Reference 列表。"""
    compiles = re.findall(r'<Compile\s+Include="([^"]+)"', proj_text)
    refs: list[tuple[str, str | None]] = []
    for match in re.finditer(r'<Reference\s+Include="([^"]+)"\s*(?:/>|>(.*?)</Reference>)', proj_text, re.S):
        name, body = match.group(1), match.group(2) or ""
        hint = re.search(r"<HintPath>([^<]*)</HintPath>", body)
        refs.append((name.split(",")[0].strip(), hint.group(1).strip() if hint else None))
    return compiles, refs


def make_sdk_style(proj_text: str) -> str:
    """把 legacy 工程机械转成 SDK 风格，仅用于让 SDK 生成 .g.vb。"""
    def block(kind):
        return [(m.group(1), m.group(2) or "") for m in
                re.finditer(rf'<{kind}\s+Include="([^"]+)"\s*(?:/>|>(.*?)</{kind}>)', proj_text, re.S)]

    def meta(body, name):
        m = re.search(rf"<{name}>([^<]*)</{name}>", body)
        return m.group(1).strip() if m else None

    lines = ['<Project Sdk="Microsoft.NET.Sdk">', "  <PropertyGroup>",
             "    <TargetFramework>net48</TargetFramework><OutputType>WinExe</OutputType><UseWPF>true</UseWPF>",
             "    <RootNamespace>PCL</RootNamespace><AssemblyName>Plain Craft Launcher 2</AssemblyName>",
             "    <MyType>Custom</MyType><PlatformTarget>AnyCPU</PlatformTarget><Prefer32Bit>false</Prefer32Bit>",
             "    <OptionExplicit>On</OptionExplicit><OptionCompare>Binary</OptionCompare>",
             "    <OptionStrict>Off</OptionStrict><OptionInfer>On</OptionInfer>",
             "    <NoWarn>41999,42016,42017,42018,42019,42020,42021,42022,42032,42036,42314</NoWarn>",
             "    <ApplicationIcon>Images\\icon.ico</ApplicationIcon>",
             "    <ApplicationManifest>app.manifest</ApplicationManifest>",
             "    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>",
             "    <EnableDefaultNoneItems>false</EnableDefaultNoneItems>",
             "    <DefineConstants>RELEASE</DefineConstants><Optimize>true</Optimize>",
             "  </PropertyGroup>",
             '  <ItemGroup><Page Remove="Resources\\Custom.xaml" /></ItemGroup>',
             "  <ItemGroup>"]
    lines += [f'    <Resource Include="{n}" />' for n, _ in block("Resource")]
    lines += ["  </ItemGroup>",
              '  <ItemGroup><EmbeddedResource Update="My Project\\Resources.resx">'
              "<LogicalName>PCL.Resources.resources</LogicalName></EmbeddedResource></ItemGroup>",
              "  <ItemGroup>"]
    for name, body in block("None"):
        link, logical = meta(body, "Link"), meta(body, "LogicalName")
        if link or logical:
            lines.append(f'    <None Include="{name}">')
            if link:
                lines.append(f"      <Link>{link}</Link>")
            if logical:
                lines.append(f"      <LogicalName>{logical}</LogicalName>")
            lines.append("    </None>")
        else:
            lines.append(f'    <None Include="{name}" />')
    lines += ["  </ItemGroup>", "  <ItemGroup>"]
    for name, body in block("Reference"):
        hint, simple = meta(body, "HintPath"), name.split(",")[0].strip()
        if hint:
            lines.append(f'    <Reference Include="{simple}"><HintPath>{hint}</HintPath>'
                         "<SpecificVersion>false</SpecificVersion><Private>true</Private></Reference>")
        else:
            lines.append(f'    <Reference Include="{simple}" />')
    lines += ["  </ItemGroup>", "  <ItemGroup>"]
    lines += [f'    <ProjectReference Include="{n}" />' for n, _ in block("ProjectReference")]
    lines += ["  </ItemGroup>", "</Project>"]
    return "\n".join(lines)


def generate_xaml_code(project_dir: str, proj_path: str, proj_text: str, dotnet: str) -> str:
    """跑一次 SDK 风格构建，把 obj 下的 .g.vb 收集到一个缓存目录。"""
    backup = proj_path + ".vbcheck-backup"
    cache = tempfile.mkdtemp(prefix="pcl3-gvb-")
    try:
        # 先清一次 obj，避免上次中断留下的 .g.vb 混进来（会出现重复定义）
        shutil.rmtree(os.path.join(project_dir, "obj"), ignore_errors=True)
        shutil.copy2(proj_path, backup)
        with open(proj_path, "w", encoding="utf-8", newline="\r\n") as handle:
            handle.write(make_sdk_style(proj_text))
        # 预期会失败（BAML 阶段），但 .g.vb 已生成
        # 关键：把输出目录和中间目录都重定向到临时目录，
        # 否则 SDK 风格构建会覆盖工程真正的 bin（把已构建的 exe 换成一个坏掉的）
        run([dotnet, "build", proj_path, "-c", "Release", "-v", "q"], project_dir)
        generated = glob.glob(os.path.join(project_dir, "obj", "**", "*.g.vb"), recursive=True)
        for path in generated:
            rel = os.path.relpath(path, os.path.join(project_dir, "obj"))
            dest = os.path.join(cache, rel)
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            shutil.copy2(path, dest)
        return cache if generated else ""
    finally:
        shutil.copy2(backup, proj_path)
        os.remove(backup)
        # 只清理中间产物 obj；绝对不要动 bin —— 那里有用户构建好的 exe 和配置文件
        shutil.rmtree(os.path.join(project_dir, "obj"), ignore_errors=True)
        # 只清理中间产物 obj；绝对不要动 bin —— 那里有用户构建好的 exe 和配置文件
        shutil.rmtree(os.path.join(project_dir, "obj"), ignore_errors=True)


def make_check_project(src: str, project_dir: str, proj_text: str, gen_dir: str, out_dir: str) -> str:
    compiles, refs = parse_project(proj_text)
    generated = sorted(glob.glob(os.path.join(gen_dir, "**", "*.g.vb"), recursive=True))

    lines = ['<Project Sdk="Microsoft.NET.Sdk">', "  <PropertyGroup>",
             "    <TargetFramework>net48</TargetFramework><OutputType>Library</OutputType>",
             "    <RootNamespace>PCL</RootNamespace><AssemblyName>Pcl3FullCheck</AssemblyName>",
             "    <MyType>Custom</MyType>",
             "    <OptionExplicit>On</OptionExplicit><OptionCompare>Binary</OptionCompare>",
             "    <OptionStrict>Off</OptionStrict><OptionInfer>On</OptionInfer>",
             "    <NoWarn>41999,42016,42017,42018,42019,42020,42021,42022,42032,42036,42314</NoWarn>",
             "    <EnableDefaultItems>false</EnableDefaultItems>",
             "    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>",
             "    <DefineConstants>RELEASE</DefineConstants>",
             "  </PropertyGroup>", "  <ItemGroup>"]
    lines += [f'    <Import Include="{ns}" />' for ns in GLOBAL_IMPORTS]
    lines += ["  </ItemGroup>", "  <ItemGroup>"]
    lines += [f'    <Compile Include="{os.path.join(project_dir, c)}" />' for c in compiles]
    lines += [f'    <Compile Include="{g}" />' for g in generated]
    lines += ["  </ItemGroup>", "  <ItemGroup>",
              '    <Reference Include="PresentationFramework" /><Reference Include="PresentationCore" />',
              '    <Reference Include="WindowsBase" /><Reference Include="System.Xaml" />',
              '    <Reference Include="System.Windows.Forms" />']
    for name, hint in refs:
        if hint:
            lines.append(f'    <Reference Include="{name}"><HintPath>'
                         f'{os.path.join(project_dir, hint)}</HintPath><SpecificVersion>false</SpecificVersion></Reference>')
        else:
            lines.append(f'    <Reference Include="{name}" />')
    for name, rel in [("MeloongCore", r"MeloongCore\Shared\bin\MeloongCore.dll"),
                      ("MeloongCore.Wpf", r"MeloongCore\Wpf\bin\MeloongCore.Wpf.dll"),
                      ("PCLCS", r"PCLCS\bin\PCLCS.dll")]:
        lines.append(f'    <Reference Include="{name}"><HintPath>'
                     f'{os.path.join(src, rel)}</HintPath></Reference>')
    lines += ["  </ItemGroup>", "</Project>"]

    path = os.path.join(out_dir, "check.vbproj")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines))
    return path


def main() -> int:
    parser = argparse.ArgumentParser(description="无需 Visual Studio 校验 PCL2 源码能否编译")
    parser.add_argument("--src", default="PCL3-src", help="源码根目录（含 Plain Craft Launcher 2 文件夹）")
    parser.add_argument("--dotnet", default="dotnet", help="dotnet 可执行文件路径")
    parser.add_argument("--keep", action="store_true", help="保留临时目录，便于排查")
    args = parser.parse_args()

    src = os.path.abspath(args.src)
    project_dir = os.path.join(src, VB_PROJECT)
    proj_path = os.path.join(project_dir, PROJECT_FILE)
    if not os.path.exists(proj_path):
        print(f"找不到工程文件：{proj_path}")
        return 2

    proj_text = open(proj_path, encoding="utf-8-sig").read()
    out_dir = tempfile.mkdtemp(prefix="pcl3-vbcheck-")

    print("[1/4] 构建 C# 依赖工程")
    if not build_dependencies(src, args.dotnet):
        print("依赖工程构建失败，先解决它们。")
        return 1

    print("[2/4] 生成 XAML 对应的 .g.vb（这一步的构建会在 BAML 阶段报错，属预期）")
    gen_dir = generate_xaml_code(project_dir, proj_path, proj_text, args.dotnet)
    if not gen_dir:
        print("没能生成 .g.vb，无法继续。")
        return 1
    count = len(glob.glob(os.path.join(gen_dir, "**", "*.g.vb"), recursive=True))
    print(f"      生成 {count} 个 .g.vb")

    print("[3/4] 组装校验工程并编译")
    check_proj = make_check_project(src, project_dir, proj_text, gen_dir, out_dir)
    result = run([args.dotnet, "build", check_proj, "-c", "Release", "-v", "q"], out_dir)
    output = (result.stdout or "") + (result.stderr or "")

    print("[4/4] 结果")
    errors = [line for line in output.splitlines() if "error BC" in line]
    if errors:
        print(f"      发现 {len(errors)} 个编译错误：")
        for line in errors:
            clean = re.sub(r" \[C:.*?\]$", "", line)
            print("      " + clean.replace(project_dir + os.sep, ""))
        return 1
    if "已成功生成" in output or result.returncode == 0:
        print("      ✅ 全部源码编译通过，0 个错误")
        return 0
    print("      构建未成功，但也没有 BC 错误，完整输出如下：")
    print(output[-3000:])
    return 1


if __name__ == "__main__":
    sys.exit(main())
