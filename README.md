# TurzxDisplay

**图灵智显 TURZX 5 英寸 USB 显示屏**的非官方 Windows 控制器 —— WinUI 3 原生应用，
把这块小屏幕变成一块可自定义的桌面副屏：时钟、便签、相册、天气、硬件监控、
大模型额度、正在播放 + 歌词。

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4) ![WinUI 3](https://img.shields.io/badge/WinUI-3-0078D4) ![C#](https://img.shields.io/badge/C%23-12-239120)

> ⚠️ 本项目为个人兴趣开发的**非官方第三方软件**，与图灵智显（TURZX）无任何关联。
> 「TURZX / 图灵智显」为其所有者的商标，此处仅用于描述兼容的硬件产品。

## 目标硬件

| 项目 | 说明 |
|---|---|
| 设备 | [图灵智显 TURZX 5 英寸 USB 显示屏](https://www.turzx.com/2025/05/26/5_inch/) |
| 购买版本 | **2022 年 11 月购买**的批次 |
| 面板 | 480×800 物理竖屏，按 800×480 横屏驱动（USB 供电 + 数据） |
| 固件版本 | `chs_5inch.dev1_rom1.87`（ROM 1.87） |
| 通信协议 | USB CDC 串口（本机为 COM3），Turing rev C 命令族，`XX EF 69 …` 帧 |

协议细节（连接握手、全屏位图分块推送、亮度、存储介质等命令）全部通过
黑盒方式分析得到：观察官方驱动的 USB 流量特征并与开源参考实现比对，
**未反编译、未使用任何官方代码或资源**。协议文档见开发者的本地研究笔记
（不随本仓库分发）。

## 开发方式声明

本项目的**硬件协议分析与全部软件代码，均由 Claude Code（命令行 AI 编程代理）
与 智谱 GLM-5.3 大模型完成**，包括：串口协议逆向、WinUI 3 界面、七种显示模式、
和风天气 JWT(Ed25519) 认证、SMTC 媒体集成、LRC 歌词解析等。人类开发者负责
提出需求、插拔 USB 线和验收结果。🙂

## 系统要求

| | 要求 |
|---|---|
| 操作系统 | Windows 10 19041（20H1）及以上 / Windows 11，**x64** |
| 运行时 | 无需安装 —— 发布包自包含 .NET 8 运行时与 Windows App SDK |
| 硬件 | 无特殊要求：任意能流畅运行 WinUI 3 的 PC（GPU 需支持 WDDM 2.0+，约 2010 年后均可）；内存 ≲200 MB |
| 从源码构建 | .NET 8 SDK + Windows App SDK（`dotnet build` 即可）；生成城市数据需 Node.js 20.11+（见下） |

## 功能一览

| 模式 | 说明 |
|---|---|
| 🕐 时钟 + 日历 | 左时钟右月历，今日高亮 |
| 📝 便签 | 最多 6 张彩色便签，应用内实时编辑 |
| 🖼 相册 | 本地照片轮播：文件夹、间隔、随机、铺满/完整；大图自动降采样解码 |
| ⛅ 天气 | 24 小时温度曲线 / 未来 7 天切换。双数据源：**Open-Meteo**（免密钥开箱即用）与**和风天气**（需自备密钥；区县级位置、空气质量、日出日落与月相、信息条） |
| 📈 监控 | CPU 型号/占用、内存、GPU 型号/占用/显存（NVIDIA·AMD·Intel 皆可，独显优先）、网速。只用系统安全 API（注册表 / GetSystemTimes / DXGI / 性能计数器），**不加载任何内核驱动** |
| 🪙 额度 | 智谱 **GLM Coding Plan** 的 5 小时 / 一周额度：剩余量、进度条、重置倒计时、近 24 小时 token 消耗图。Token 自动读取本机 Claude Code 配置或手动填写 |
| 🎵 音乐 | 正在播放（曲目/歌手/专辑封面/进度），基于 Windows 系统媒体控制（SMTC），支持 PotPlayer、foobar2000、Media Player、Spotify、浏览器等；超长歌名自动滚动；**LRC 歌词逐句同步**（UTF-8/GB18030 自动识别） |

## 简单使用

1. 运行 `TurzxDisplay.exe` → 底部连接栏选择串口（通常 COM3）→ 点「连接」；
2. 底部模式段切换 7 种画面，右侧面板配置各模式参数；
3. 关闭窗口 = 最小化到托盘（画面继续推送），右键托盘图标可切换模式或退出；
4. 「自动推送」保持开启，屏幕才会实时刷新动态画面。

**和风天气**：在 `qweather/` 目录放置你的 `ed25519-private.pem`（生成方法见
[该目录 README](qweather/README.md)），应用内填写 API Host / 开发者ID / 项目ID /
凭据ID，然后搜索并选择区县级位置。

**GLM 额度**：默认自动读取 `%USERPROFILE%\.claude\settings.json` 中的
`ANTHROPIC_AUTH_TOKEN`，无需配置。

**城市数据**：首次从源码构建请运行 `node tools/update-city-list.mjs`
生成 `Data/china-cities.tsv`（许可原因仓库不带此文件，见
[Data/README](Data/README.md)）。

## 第三方依赖与许可

### NuGet 软件包（均为 MIT License）

| 包 | 用途 | 许可 |
|---|---|---|
| [Microsoft.WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) | WinUI 3 框架 | MIT |
| [Microsoft.Windows.SDK.BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools) | 构建工具链 | MIT |
| [System.IO.Ports](https://github.com/dotnet/runtime) | 串口（SerialPort） | MIT |
| [System.Diagnostics.PerformanceCounter](https://github.com/dotnet/runtime) | GPU 性能计数器 | MIT |
| [System.Text.Encoding.CodePages](https://github.com/dotnet/runtime) | GB18030 编码（中文歌词） | MIT |
| [BouncyCastle.Cryptography](https://github.com/bcgit/bc-csharp) | Ed25519 JWT 签名（和风天气） | MIT |

### 参考项目（非代码依赖）

| 项目 | 用途 | 许可 |
|---|---|---|
| [mathoudebine/turing-smart-screen-python](https://github.com/mathoudebine/turing-smart-screen-python) | Turing rev C 协议参考实现 | MIT |
| [qwd/LocationList](https://github.com/qwd/LocationList) | 中国城市/区县数据来源（仓库不分发其数据，提供转换脚本） | ⚠️ 未附带许可 |

### 在线服务（API，非软件依赖；使用需遵守各自服务条款）

[Open-Meteo](https://open-meteo.com/)（免密钥天气）、[ipwho.is](https://ipwho.is/)（IP 定位）、
[和风天气 QWeather](https://dev.qweather.com/)、[智谱 BigModel](https://open.bigmodel.cn/)
（GLM Coding Plan 额度查询）。字体（Segoe UI / Segoe Fluent Icons）为 Windows
系统自带，运行时按需调用，不随本项目分发。

## 许可

本项目采用 **[MIT License](LICENSE)**。选择理由：全部软件依赖均为 MIT，
无传染性义务冲突；MIT 简短、宽松、业界接受度最高，最适合这种硬件周边
工具被自由使用、改造和分发。（若需要明确的专利授权条款，衍生项目可自行
改用 Apache-2.0。）

## 免责声明

本软件按「原样」（AS IS）提供，不提供任何形式的保证。使用本软件向
USB 显示屏写入内容属于该硬件的设计用途，但**作者不对使用本软件造成的
任何直接或间接损失负责，包括但不限于硬件故障、数据丢失或业务中断**。
本软件为非官方第三方作品，与图灵智显（TURZX）无任何关联；所涉在线服务
（天气、额度查询等）以使用者自己的账户与密钥调用，请遵守相应服务条款。

## 公开发布的法律风险探讨（不构成法律意见）

1. **商标**：项目名与描述中的「TURZX / 图灵智显」属于指明兼容用途的描述性使用
   （nominative fair use 一类），一般可行；本 README 已显著声明非官方、无关联。
   若收到权利人异议，应更名（如 `TuringScreen Companion`）并仅保留描述性提及。
2. **协议逆向**：串口协议通过对官方驱动的**黑盒观察**（USB 流量长度/时序特征）
   并与 MIT 开源参考实现比对得出；未反编译、未复制官方代码或资源。多数司法
   辖区（包括中国《计算机软件保护条例》下「为实现兼容进行必要分析」的条款）
   对互操作目的的黑盒分析持允许态度，风险较低。仓库中也不要收录官方驱动的
   任何文件。
3. **数据再分发**：城市表源数据（qwd/LocationList）未附带许可，本仓库已改为
   不收录数据、仅提供转换脚本，由使用者自行下载，规避了再分发争议。
4. **在线服务**：天气/额度功能使用使用者自己的密钥与免费额度，本项目不分发
   任何密钥；需遵守各服务商条款（免费额度、频率限制等，应用已内置保守的
   轮询间隔）。
5. **免责**：向硬件写屏属正常设计用途，但建议保留「按原样提供、不对硬件
   损坏负责」的免责声明（MIT 第 5-6 条已覆盖大部分）。
6. 以上为一般性工程判断，**不构成法律意见**；正式公开发布（尤其商业分发）
   前建议咨询执业律师。

## 致谢

- [TURZX 图灵智显](https://www.turzx.com/2025/05/26/5_inch/) 的有趣硬件
- [turing-smart-screen-python](https://github.com/mathoudebine/turing-smart-screen-python) 社区
- Claude Code + GLM-5.3：这个仓库里几乎每一行代码的作者
