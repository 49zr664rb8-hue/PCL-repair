# PCL修复版 —— 基于 PCL2 开源源码的修改版

> **本项目与龙腾猫跃及 PCL 官方没有任何关系**，是第三方基于 [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) 开源源码的修改版。
> 名称遵循上游《合理使用指南》（不得使用「PCL 3」等易被误认为官方的名字），详见 [NOTICE.md](NOTICE.md)。

**状态：已构建、实机跑通。**

产物：源码构建后位于 `Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe`；
另有一份免构建的发布包，里面是 `release/PCL修复版.exe`（单文件，约 5.9 MB，解压即用）。

---

## 一、做了什么

只有一件事：**把新版 PCL 删掉的下载功能还原。**

`Modules/ModSecret.vb` 里有个常量把下载功能锁死了：

```vb
Public Const RestrictMcDownload As Boolean = True
```

配套 14 处调用点全是 `If RestrictMcDownload Then Throw New Exception("为遵守相关法律法规，PCL 不再提供 MC 下载功能…")`，
而且下载页被整块隐藏。同一文件里 `CurseForgeAPIKey`、`OAuthClientId` 也被清空成 `""`。

改成从 `PCL3.config.ini` 读取、默认解锁，另外给版本列表加了 `piston-meta` 作为第三条通路。

### 启动交给官方启动器

正版登录需要 Azure 应用的 ClientId，开源版把它删空了，自己注册又要求有 Azure 订阅
（个人微软账号默认没有目录，注册应用会被拒）。这条路对普通用户基本走不通。

所以「开始游戏」改成了**唤起 Minecraft 官方启动器**（它自带 ClientId，正版登录、Realms、披风全部正常），
在那边登录并启动。PCL 继续负责下载、模组、联机——两边共用同一个 `.minecraft` 目录。

```
PCL 点「开始游戏」 → 打开官方启动器 → 那边点开始 → 进游戏 → 对局域网开放 → 回 PCL 开隧道
```

找不到官方启动器时会**自动回退**到 PCL 自己启动（此时只能用离线登录），所以不会彻底打不开。

**游戏目录会自动对齐。** 版本隔离开着时，mod 和存档都在 `versions\<版本>\` 里，
而官方启动器默认读 `.minecraft` 根目录——直接启动会"看不到 mod 和存档"。
所以 PCL 在唤起官方启动器前，会把当前版本的 `gameDir` 写进 `launcher_profiles.json`
（首次会备份成 `.pcl3bak`），并把 `lastUsed` 设为当前时间，让官方启动器按"最近使用"默认选中它。

不需要你手动配置任何东西。

开关在配置里：

```ini
[Launch]
UseOfficialLauncher = true
```

---

## 二、联机：PCL 联机页改成 bore 隧道

PCL2 原本的联机页基于 EasyTier，但**上游把它依赖的会合节点停了**：

| 地址 | 结果 |
|---|---|
| `public.easytier.cn` / `public.easytier.top` | NXDOMAIN |
| `uptime.easytier.cn`（节点列表 API） | NXDOMAIN |
| `mc1.easytier.cn:55558`（PCL 自建） | DNS 可解析，TCP 超时 |

没有会合节点，两个 NAT 后面的机器就无法互相发现。官方版不受影响（能从 PCL 服务器动态下发新节点），
开源版被切断了这条路。

所以整个联机页重写成了 **bore 隧道**方案，交互和你原本在 PCL 里用的一模一样：

### 房主

1. 游戏里进世界 → `Esc` → 「对局域网开放」→ 记住端口
2. PCL 顶部「联机」→「创建房间」→ 输入那个端口
3. 自动下载 bore（约 1.7MB，只下一次）→ 自动开隧道
4. 显示 `bore.pub:12345` 并自动复制，发给朋友

### 朋友

1. 把地址粘到 PCL 的「加入房间」里（或者复制地址后切回 PCL，会自动弹窗问要不要加入）
2. PCL 提示「请在游戏里「多人游戏 → 直接连接」填这串地址」
3. 进游戏连上

### 特点

- **只有房主需要联网下载一次 bore**，朋友什么都不用装
- 不需要注册、不需要登录、不需要实名
- 关掉联机页隧道就断

### 为什么是 bore 而不是自己写 P2P

自己写一个 P2P 引擎，仍然需要一个「帮两台机器互相发现」的会合点；如果有会合点，
现成的 EasyTier / Tailscale 就不需要我再造一遍。bore 是这里最轻的选择：单文件、无账号、公共节点。

---

## 三、为什么不用 IPv6 直连

这台机器有公网 IPv6（`2409:8a55:...`，中国移动），理论上朋友填 `[IPv6]:端口` 就能直连，零依赖。

**但实测不通**：手机（有 IPv6）访问本机 IPv6 地址时超时，而本机端口在监听、Windows 防火墙已放行、
`netstat` 看不到任何外部连接——包被拦在光猫或运营商那一层。

关掉光猫的 IPv6 防火墙理论上能解决，但那会让家里所有设备直接暴露在公网，不值得为一次联机这么做。

**如果你以后换了路由器、或者确认入站放行**，IPv6 直连是最干净的方案，不需要任何第三方。

---

## 四、目录结构

```
<仓库根目录>
├── README.md                  本文件
├── README-upstream.md         上游 PCL 的原始 README
├── NOTICE.md                  合规与署名（发布前必读）
├── BUILD.md                   构建说明（含踩坑记录）
├── LICENCE                    上游许可证，勿改动
├── PCL3.config.ini.example    配置模板（下载开关 / 微软登录 / CurseForge）
├── build.bat                  一键构建（双击）
├── Plain Craft Launcher 2.sln 解决方案
├── Plain Craft Launcher 2/    启动器源码
├── MeloongCore/               基础库（锁定上游指定提交）
├── PCLCS/                     压缩包处理库
├── patches/                   上游自带的版本补丁集
├── patches-pcl3/              本改版相对上游的补丁，2 个
│   ├── 0001-feat-download-piston-meta.patch
│   └── 0002-feat-PCL-bore.patch
└── tools/
    ├── preflight.py           构建前文件完整性预检
    ├── vbcheck.py             无需 VS 的全量编译校验（Roslyn）
    └── vb_sanity.py           VB 源码结构检查
```

构建产物在 `Plain Craft Launcher 2\bin\`，`bin/` 与 `obj/` 已在 `.gitignore` 里排除，推送时不会带上。

> 另外打包了一份「解压即用」的发布包，里面是同样这些文档加上 `release/PCL修复版.exe`，
> 不需要构建环境的人下那个就行。

---

## 五、快速开始

### 直接下载就能用

`release/` 里是编译好的成品，**解压到任意目录，双击 `PCL修复版.exe` 即可**：

```
release/
├── PCL修复版.exe          主程序（单文件，约 5.9 MB，无需安装）
├── PCL修复版.exe.config   ← 必须和 exe 放在同一目录，否则网络请求会出问题
└── PCL3.config.ini        配置模板，按需修改
```

要求：Windows 10/11 x64 + .NET Framework 4.8（Win10 1809 以上自带）。

> `.exe.config` 里有一项 `useUnsafeHeaderParsing`，缺了它部分网络请求会被 .NET 拒绝，
> 所以**改名或移动 exe 时，配置文件必须跟着改成同名**。

### 从源码自己编

双击仓库根目录的 `build.bat`，产物在 `Plain Craft Launcher 2\bin\`。
需要 Visual Studio 2022 生成工具（含 .NET 桌面开发工作负载），详见 [BUILD.md](BUILD.md)。

### 配置

配置在 exe 同目录的 `PCL3.config.ini`：

```ini
[Download]
UnlockVanilla = true

[Auth]
MsClientId =
CurseForgeApiKey =
```

### 想用正版登录（进正版服务器必须）

PCL 用**设备码登录**，只要一个 `client_id`，不要密钥。但上游开源版把 PCL 自己的 Client ID 删了，
得自己注册一个免费应用（约 3 分钟）：

1. https://portal.azure.com → 「Microsoft Entra ID」→「应用注册」→「新注册」
2. 名称随便填；**「受支持的账户类型」必须选「仅个人 Microsoft 账户」**
3. 左侧「身份验证」→ 拉到底 → **「允许公共客户端流」选「是」→ 保存**
4. 复制「应用程序(客户端) ID」，填到 `MsClientId =`
5. 重启 PCL，启动页选「正版」

### 重新构建

双击 `PCL3\build.bat`。

---

## 六、验证记录

| 项目 | 方法 | 结果 |
|---|---|---|
| 全量编译 | `tools/vbcheck.py`（Roslyn 编译 114 源文件 + 63 个 XAML 生成文件） | **0 错误** |
| 构建 | 双击 `build.bat` | BUILD OK，约 5.9 MB |
| 启动 | 实机运行，读 `PCL\Log1.txt` | 三阶段加载，零错误零异常 |
| 恢复下载 | 日志 `切换主要页面：Download` + `DlClientList Mojang` 完成 | ✅ 官方源 690ms |
| 模组下载 | 日志 `Modrinth 搜索到了 40 个工程（共 77608 个）` | ✅ 免密钥可用 |
| 保留功能 | 识别 2 个 Java、MC 版本、皮肤加载 | ✅ |
| **bore 隧道** | 本地起服务 → bore 打洞 → **从公网 curl 成功取到内容** | ✅ 端到端验证 |
| 补丁可应用性 | 干净基线上 `git am` | ✅ 成功 |

**没验证的：**

- 两台机器跑真实游戏（需要第二个人）
- 免费 bore 节点的长期稳定性

---

## 七、构建环境踩过的坑

### 1. `dotnet build` 建不了 .NET Framework 的 WPF 工程

```
error MC1000: Could not find assembly 'mscorlib, Version=0.0.0.0'
```

用三文件的最小工程复现过，跟本工程配置无关——.NET SDK 的 WPF 标记编译器跑在 CoreCLR 上，
解析不了 .NET Framework 的引用程序集。**必须用 Visual Studio / Framework 版 MSBuild。**

### 2. VS 安装器报 5002 的真实原因

```
Error 0x80070057: 已添加项。字典中的关键字:"HTTPS_PROXY"所添加的关键字:"https_proxy"
```

安装器内部用大小写不敏感的字典存环境变量，`HTTPS_PROXY` 和 `https_proxy` 同时存在就崩。
只保留大写那份（`env -u http_proxy -u https_proxy`）即可。

### 3. MeloongCore 必须用 PCL 锁定的提交

`4c60c2962c3b50bc3fe96ed5dd9259b980173580`，不是 master 最新版。

### 4. `Controls\MySliderDot.xaml.vb` 是上游遗留的孤儿文件

依赖一批在任何版本里都不存在的 API，**不在工程文件里**，不参与构建。目录扫描类工具会误报 12 个错误。

### 5. 没有 VS 也能校验源码

```bash
python tools/vbcheck.py --src PCL3-src
# ✅ 全部源码编译通过，0 个错误
```

原理：临时把工程转 SDK 风格跑一次构建拿到 63 个 `.g.vb`（BAML 阶段必失败，但产物已落盘）→ `finally` 还原 →
用「工程真实的 Compile 列表 + 这 63 个文件」拼一个库工程交给 Roslyn。

---

## 八、合规提醒

- **不能叫「PCL 3」。** 《PCL 分发有限许可》把 `PCL 3`、`PCL++`、`PCL Pro` 列为禁止名称。
  自己用不公开不构成影响，要分享就改名成 `PCL xxx修改版`。
- **主题色相必须保持蓝色**（许可证第 5 条硬性要求）。
- **正版劝导不能删**（许可证第 4 条）。
- 详见 [NOTICE.md](NOTICE.md)。
