# 构建说明

## 零、这台机器上已经装好了

| 组件 | 版本 / 位置 | 状态 |
|---|---|---|
| Visual Studio Build Tools 2022 | 17.14.37806.7 · `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools` | ✅ |
| `.NET desktop build tools` 工作负载 | `Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools` | ✅ |
| .NET Framework 4.8 目标包 | `Microsoft.Net.Component.4.8.TargetingPack` | ✅ |
| .NET SDK | 9.0.100 · `C:\Program Files\dotnet` | ✅ |
| NuGet 包 | 已对整个 sln 执行过 restore | ✅ |
| MeloongCore | 已放好 PCL 锁定的 `4c60c29` 版本 | ✅ |

**构建已经成功过了**，产物在：

```
PCL3-src\Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe
```

已实机跑通（下载页恢复、Modrinth 搜到 77608 个工程），详见 README 第六节。

> ⚠️ **改过源码之后必须重新构建**，否则跑的还是旧 exe。
> 双击 `PCL3\build.bat` 即可，它会自动找 MSBuild、构建、校验产物。

想先自查一遍文件完整性，可以跑：

```bash
python tools/preflight.py --src PCL3-src
# ✅ 工程引用的文件全部就位，可以开始构建
```

（它会检查 264 个工程引用 + 18 个 resx 内嵌文件。已跑过，全部就位。）

> 装 VS 时踩到一个坑，记在这里免得你重装时再踩：
> **如果安装器报 exit code 5002，是因为环境里同时存在 `HTTPS_PROXY` 和 `https_proxy`
> （以及 `HTTP_PROXY` / `http_proxy`）。** VS 安装器内部用大小写不敏感的字典存环境变量，
> 两个都塞进去就直接抛异常。解决办法是只保留大写那一份再跑安装器：
>
> ```bash
> env -u http_proxy -u https_proxy ./vs_buildtools.exe --quiet --wait --norestart \
>     --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --includeRecommended
> ```

---

这份文档里的结论都是**实测**得出的，不是照抄通用教程。把踩过的坑写在最前面，因为它决定了你必须用什么工具链。

---

## 一、先看这个：`dotnet build` 建不了这个工程

如果你只有 .NET SDK，`dotnet build` 会在 WPF 标记编译阶段失败：

```
error MC1000: 未知的生成错误"Could not find assembly 'mscorlib, Version=0.0.0.0,
Culture=neutral, PublicKeyToken=null'. Either explicitly load this assembly using a
method such as LoadFromAssemblyPath() or use a MetadataAssemblyResolver that returns
a valid assembly."
```

我用一个**最小工程**（net48 + UseWPF + 3 个文件）复现了同样的错误，所以这跟本工程怎么配置无关：

> .NET SDK 自带的 WPF 标记编译器（`PresentationBuildTasks`）运行在 CoreCLR 上，
> 它无法解析 .NET Framework 引用程序集里的 `mscorlib`（版本号 `0.0.0.0`）。
> **.NET Framework 的 WPF 工程，只能用 .NET Framework 版的 MSBuild 构建。**

结论：**必须用 Visual Studio 2022（或 VS Build Tools 里的 `msbuild.exe`）**。

我也试过把工程转成 SDK 风格：那能让 `dotnet build` 走得更远（XAML 的 `.g.vb` 都能生成），但最后一样卡在 BAML 编译。所以那条路走不通，工程文件保持上游原样。

---

## 二、环境要求

| 项目 | 要求 | 说明 |
|---|---|---|
| 操作系统 | Windows 10 / 11 x64 | WPF 只能跑在 Windows |
| Visual Studio | 2022（17.12 或更新） | 需要 **.NET 桌面开发** + **Visual Basic** 工作负载 |
| .NET Framework | 4.8 Developer Pack | 主工程与 PCLCS 都目标 `net48`；缺了会报 `MSB3644` |
| .NET SDK | 8.0 或更新 | 只为构建 MeloongCore / MeloongCore.Wpf / PCLCS 三个 SDK 风格工程 |

> 如果只想**校验源码能不能编译**，不需要装 Visual Studio——见第六节。

---

## 三、MeloongCore 必须是锁定版本

上游用 git submodule 引用 `MeloongCore`，仓库里是空目录。**版本不能随便取最新**，要取 PCL 这个提交锁定的那个：

```bash
# PCL main 分支锁定的子模块提交：
# 4c60c2962c3b50bc3fe96ed5dd9259b980173580
curl -L -o mc.tar.gz https://codeload.github.com/Meloong-Git/MeloongCore/tar.gz/4c60c2962c3b50bc3fe96ed5dd9259b980173580
mkdir -p PCL3-src/MeloongCore && tar -xzf mc.tar.gz -C PCL3-src/MeloongCore --strip-components=1
```

或者，如果你有完整 git 仓库：`git submodule update --init --recursive`

本工程交付时已经把这个版本的 MeloongCore 放好了，正常情况下不用动。

---

## 四、用 Visual Studio 构建

1. 打开 `PCL3-src/Plain Craft Launcher 2.sln`
2. 解决方案配置选 `Release`（另有 `Debug` / `Snapshot`）
3. 平台选 `Any CPU`
4. 右键解决方案 →「还原 NuGet 包」
5. 生成解决方案（`Ctrl+Shift+B`）

## 五、用命令行构建

在「Developer Command Prompt for VS 2022」里：

```bat
cd /d "PCL3-src"
msbuild "Plain Craft Launcher 2.sln" /restore /p:Configuration=Release /p:Platform="Any CPU" /m
```

产物：

```
PCL3-src\Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe
```

注意主工程的 `OutputPath` 就是 `bin\`，**没有 Debug/Release 子目录**，切换配置会互相覆盖。

### 构建顺序

`sln` 里已经写好依赖，正常会按这个顺序走：

1. `MeloongCore`（netstandard2.0）→ `MeloongCore.dll`
2. `MeloongCore.Wpf`（net48）→ `MeloongCore.Wpf.dll`
3. `PCLCS`（net48）→ `PCLCS.dll`
4. `Plain Craft Launcher 2`（VB + WPF）→ exe

主工程把这些 DLL 通过 `My Project\Resources.resx` 里的 `ResXFileRef` **在编译期内嵌进 exe**，运行时由 `Application.xaml.vb` 的 `AssemblyResolve` 从 `My.Resources` 取出加载——所以发布出来是单文件。

---

## 六、没有 Visual Studio 时，怎么校验源码

`tools/vbcheck.py` 就是为此写的：绕开 BAML 编译，用 Roslyn 把**全部 VB 源码**（含 XAML 生成的 `.g.vb`）真实编译一遍。

```bash
python tools/vbcheck.py --src PCL3-src
```

输出：

```
[1/4] 构建 C# 依赖工程
  · 构建 MeloongCore/Shared/MeloongCore.csproj
  · 构建 MeloongCore/Wpf/MeloongCore.Wpf.csproj
  · 构建 PCLCS/PCLCS.csproj
[2/4] 生成 XAML 对应的 .g.vb（这一步的构建会在 BAML 阶段报错，属预期）
      生成 63 个 .g.vb
[3/4] 组装校验工程并编译
[4/4] 结果
      ✅ 全部源码编译通过，0 个错误
```

它做的事：

1. 先构建三个 C# 依赖工程；
2. 临时把主工程转成 SDK 风格跑一次构建——**BAML 会失败，但 XAML 的 `.g.vb` 已经生成**，这正是需要的产物；跑完立刻在 `finally` 里把工程文件还原，不会把仓库弄脏；
3. 用「工程文件里真实的 Compile 列表 + 上一步的 `.g.vb`」拼一个 net48 库工程，用 Roslyn 编译。

前提：.NET SDK + .NET Framework 4.8 引用程序集。

**这个校验能抓出所有真实的 VB 编译错误**（类型、签名、`Handles`、`Option Strict` 相关等），但它不能代替最终构建——BAML 打包仍然只能靠 Visual Studio。

---

## 七、常见问题

**Q：`MSB3644 找不到 .NETFramework,Version=v4.8 的引用程序集`**
没装 .NET Framework 4.8 Developer Pack。VS Installer 里勾「.NET Framework 4.8 SDK / targeting pack」。

**Q：`MC1000: Could not find assembly 'mscorlib, Version=0.0.0.0'`**
你在用 `dotnet build` 建 WPF 工程。换 Visual Studio / `msbuild.exe`，见第一节。

**Q：编译报「找不到 MeloongCore」**
子模块没在位，或者版本不对。见第三节。

**Q：`LangVersion 13` 相关报错**
MeloongCore 用了 C# 13 语法。升级 VS 2022 到 17.12+，或者用 .NET SDK 9 的 `dotnet build` 建那三个 C# 工程。

**Q：`MySliderDot` 相关的编译错误**
`Controls\MySliderDot.xaml.vb` 是上游误提交的半成品控件（依赖 `Logger.Warning`、`LogLevel.Hint`、`MathRange`、`SetTop` 等在任何版本里都不存在的 API），**它不在工程文件里**，不参与构建。如果你的工具把目录下所有 `.vb` 都扫进去了就会看到这些错误——按工程文件的 Compile 列表来就不会。

---

## 八、目录结构

```
<仓库根目录>
├── README.md                  面向用户的说明
├── README-upstream.md         上游 PCL 的原始 README
├── NOTICE.md                  合规与署名
├── BUILD.md                   本文件
├── LICENCE                    上游许可证，勿改动
├── PCL3.config.ini.example    配置模板
├── build.bat                  一键构建
├── Plain Craft Launcher 2.sln
├── Plain Craft Launcher 2/    启动器源码
├── MeloongCore/               子模块，锁定 4c60c296
├── PCLCS/                     压缩包处理库
├── patches/                   上游自带的版本补丁集
├── patches-pcl3/              本改版相对上游的补丁，2 个
└── tools/                     preflight / vbcheck / vb_sanity
```

---

## 九、联机为什么改成 bore

上游联机页基于 EasyTier，但它依赖的会合节点全部停服了：

| 地址 | 结果 |
|---|---|
| `public.easytier.cn` / `public.easytier.top` | NXDOMAIN |
| `uptime.easytier.cn`（节点列表 API） | NXDOMAIN |
| `mc1.easytier.cn:55558`（PCL 自建） | DNS 可解析，TCP 超时 |

没有会合节点，两个 NAT 后面的机器就无法互相发现，这是网络拓扑的硬限制，写死节点没有意义。

**试过 IPv6 直连**：本机有公网 IPv6，理论上填 `[IPv6]:端口` 就能连。但实测被拦在光猫那一层
（本机端口在听、防火墙已放行、`netstat` 看不到任何外部连接），移动宽带 + 光猫封入站。
关掉光猫的 IPv6 防火墙能解决，但那会让家里所有设备暴露在公网，不值得。

**最后选 bore**：单文件、免注册、公共节点可用，且只有房主需要运行。

---

## 十、验证记录

| 项目 | 方法 | 结果 |
|---|---|---|
| 全量编译 | `tools/vbcheck.py`（Roslyn 编译 114 源文件 + 63 个 XAML 生成文件） | 0 错误 |
| 构建 | 双击 `build.bat` | BUILD OK，约 5.9 MB |
| 启动 | 实机运行，读 `PCL\Log1.txt` | 三阶段加载，零错误 |
| 恢复下载 | 日志 `切换主要页面：Download` + `DlClientList Mojang` | 官方源 690ms |
| 模组下载 | 日志 `Modrinth 搜索到了 40 个工程` | 免密钥可用 |
| bore 隧道 | 本地起服务 → bore 打洞 → 从公网 curl 取到内容 | 端到端通过 |
| 单文件分发 | 只放 exe + exe.config 到空目录启动 | 正常，三阶段 66/187/50ms |
| 产物可复现 | 删掉 bin/obj 重新编译，比对 MD5 | 与发布版完全一致 |
| 子模块 | 本地 `git clone` 后检查 | 301 个文件齐全 |

**没验证的**：两台机器跑真实游戏（需要第二个人）、免费 bore 节点的长期稳定性。
