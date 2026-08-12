# 第三方声明

[English](THIRD-PARTY-NOTICES.md) | [简体中文](THIRD-PARTY-NOTICES.zh-CN.md)

> 最近复核：2026-08-13

本清单区分公开源码树与未来二进制发行版。仓库只包含源码、项目元数据、选定的许可证文本和
引入的 Lucide 图标几何；不包含构建输出、NuGet 包载荷、可选原生二进制、模型文件、私有测试
材料或发行包。

提及依赖或外部运行时不授予复制或再分发它的权利。发行者在将任何制品纳入二进制发行版前，
必须核对其准确制品、版本、许可证、声明、署名要求和再分发条件。

## 本仓库中存在的组件

### Motara 源码

除文件另有说明外，`src/` 和 `native/` 下由 Motara 编写的源码采用
[MPL-2.0](LICENSE)。授权范围及文档和法律材料的处理详见[声明](NOTICE.zh-CN.md)。
MPL-2.0 不授予产品或组织标识的使用权；请参阅[商标政策](TRADEMARKS.zh-CN.md)。

### Lucide 0.468.0

- **项目：** [Lucide](https://lucide.dev/)
- **使用来源：** `lucide-static` 0.468.0
- **许可证：** ISC，原文位于 `LICENSES/LUCIDE-ISC.txt`。
- **仓库范围：** 桌面外壳导航、菜单和控件图标所使用的已生成矢量几何；运行时不会解析
  图标包。

### 第三方许可证文本

`LICENSES/` 还保留了 AvaloniaEdit、Bouncy Castle、PurismCore 和 SharpCompress 的许可证
原文，它们的源码或依赖关系在本文中均有记录。CC BY 4.0 与 CC0 1.0 则用于明确采用这些许可
的文件。保留许可证文本不表示公开源码树中包含对应二进制。

## 开发者还原的托管依赖

以下生产依赖固定在 `Directory.Packages.props` 中；生产项目的 `packages.lock.json` 记录其
直接和传递依赖的准确解析结果。这些依赖由开发者或二进制构建者从 NuGet 还原，而不是提交到
本仓库。

| 组件 | 版本 | 许可证 | 用途 |
| --- | --- | --- | --- |
| Avalonia、Avalonia.Desktop、Avalonia.Skia、Avalonia.Themes.Fluent | 12.1.0 | MIT | 跨平台桌面宿主、UI 框架、主题和 Skia 集成。 |
| Avalonia.AvaloniaEdit | 12.0.0 | MIT | 公式编辑器的文本编辑行为。 |
| SkiaSharp | 3.119.4 | MIT | 纹理解码和模型渲染。 |
| Microsoft.Extensions.Logging、Microsoft.Extensions.Logging.Abstractions | 10.0.10 | MIT | 日志抽象和应用日志组合。 |
| Serilog.Extensions.Logging、Serilog.Sinks.File、Serilog | 10.0.0、7.0.0、4.2.0 | Apache-2.0 | 结构化、本地滚动诊断日志。 |
| SharpCompress | 0.50.1 | MIT | 本地 RAR 和 7Z 模型压缩包导入。 |
| BouncyCastle.Cryptography | 2.6.2 | MIT | 协作用 Ed25519 签名和验证。 |
| System.Security.Cryptography.ProtectedData | 10.0.10 | MIT | 使用 Windows DPAPI 保护本地协作身份。 |

上表许可证表达式取自已解析 NuGet 包的元数据。二进制发行版必须保留每个适用的直接和传递
依赖的声明与许可证，包括 Avalonia、SkiaSharp 及其依赖解析出的原生资产。当前源码检出有意
不主张这些已还原包载荷属于 GitHub 源码分发内容。

## 条件性原生与运行时依赖

`src/Motara.App/Motara.App.csproj` 仅在本地路径存在时引入可选内容。下列二进制、模型文件、
SDK 或运行时载荷均未被本仓库跟踪，干净检出也不会包含它们。

| 组件 | 与源码树的关系 | 公开源码分发 | 发行要求 |
| --- | --- | --- | --- |
| [PurismCore](https://github.com/MotaraSoft/PurismCore) | 由 Motara 运行时适配器加载、独立维护的 MOC3 解析器。当前复核的来源为 `97dd2a5cf4a0c37947319b77671c578bfa99ad96`，默认 v6 ABI。 | 未复制 PurismCore 源码或原生二进制。 | 记录准确制品/版本/来源并保留 MIT 声明。见[合规记录](docs/legal/PurismCore-Compliance.zh-CN.md)。 |
| FFmpeg 共享运行时 | 可选视频解码/探测可执行文件与 DLL。 | 不包含。 | 在分发前确认准确构建和配置，保留许可证与声明，并履行所选 LGPL/GPL 与专利义务。 |
| Spout2 SDK/运行时 | 可选 Windows 桥接源码要求单独提供 Spout2 SDK，并可静态链接其库。 | 不包含 SDK、静态库、桥接二进制或许可证文件。 | 确认准确 SDK/源码版本与许可证，并满足桥接及所有链接代码的声明和再分发条件。 |
| NDI Runtime / SDK | 可选 Windows 桥接会动态加载用户已安装的 NDI 运行时。 | 不包含 NDI 运行时、SDK、头文件或桥接二进制。 | 遵守 NDI SDK/运行时许可证与再分发条件；不得暗示 Motara 源码许可证覆盖 NDI。 |
| MediaPipe Face Landmarker | 可选本地相机桥接和 `.task` 模型。 | 不包含桥接二进制、MediaPipe 运行时、模型或支持资产。 | 记录准确 MediaPipe 构建、模型来源、许可证、声明和再分发条件。 |
| OpenSeeFace 和 ESCAPI | 可选本地 OpenSeeFace 进程和相机库。 | 不包含可执行文件、模型、相机库或许可证文件。 | 记录准确发行版/来源，并保留所有适用许可证和声明。 |

`native/` 内的桥接源码除文件另有说明外由 Motara 编写。仅通过加载外部运行时或引用其 ABI，
并不会把外部运行时纳入源码；但这也不会免除发行者对其链接、捆绑或调用的二进制所负的独立
义务。

## 排除材料

用户模型、模型压缩包、录制内容、截图、日志、私有本地测试材料、Live2D 示例模型、构建输出
和打包制品均不属于本仓库的源码分发范围，且除其自身文件另有说明外，不受 Motara 源码许可证
覆盖。
