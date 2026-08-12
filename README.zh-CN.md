# Motara

[English](README.md) | [简体中文](README.zh-CN.md)

Motara 是一款开源桌面应用，用于面部捕捉数据处理、虚拟形象控制与场景合成。

> **项目状态：** 正在积极开发。本仓库尚未提供官方二进制发行版；其 API、文件格式、
> 网络行为和受支持平台范围均不构成稳定的兼容性承诺。

## 本源码树已实现

- 基于 Avalonia 的桌面外壳，具备英文与简体中文本地化、本地 UI/窗口设置、结构化本地
  诊断日志、菜单和快捷键配置。
- 场景工作区：本地场景持久化、模型分配、图像与视频背景、Spout2/NDI 信号附件、效果和
  截图。
- 本地模型库：可导入 Cubism 模型描述文件目录，以及 ZIP、RAR、7Z 压缩包，并进行有界
  校验；模型参数映射和捕捉源映射方案也存储在本地。
- 通过独立维护、MIT 许可的
  [PurismCore](https://github.com/MotaraSoft/PurismCore) 原生组件实现的 MOC3 解析与
  Skia 渲染代码，包括纹理解码、遮罩、混合模式、GPU 回退和截图。
- iFacialMocap UDP、本地相机 MediaPipe、本地 OpenSeeFace 进程三种捕捉适配器，以及
  映射、公式、校准/滤波管线、参数优先级和有界帧处理。
- 具备可配置参数映射的 Live2D Cubism Editor 外部 API 输出适配器。
- 协作功能的早期组件：安装实例身份存储、好友与会话邀请格式、加密点对点帧、本地好友
  记录和模型包传输原语。

其中一部分能力依赖可选原生组件或外部应用。这些能力的源码已经存在，但在干净检出中，
需按下文提供依赖后才能使用。

## 尚未实现或尚不能发行

- 尚未实现 VTube Studio 输出。
- 尚未实现公开插件 SDK、插件宿主和稳定的第三方扩展契约。
- 仓库不含 PurismCore、FFmpeg、Spout2、NDI、MediaPipe、OpenSeeFace、相机支持组件及其
  模型文件的官方可再分发包。干净检出会有意省略这些二进制与数据文件。
- MediaPipe 和 OpenSeeFace 捕捉、Spout2/NDI 信号、视频解码和 MOC3 渲染均需要各自的
  本地运行时组件。源码会明确处理组件缺失的情况，但本仓库自身不保证这些能力可用。
- 当前协作身份使用 Windows DPAPI 保护；尚未提供等价的 macOS Keychain 与 Linux Secret
  Service 实现。
- 协作网络、presence 服务、原生运行时、跨平台支持、安全审查、安装/更新流程及端到端
  互操作性尚未达到可发行状态。

## 构建并运行源码

安装 [global.json](global.json) 指定的 .NET SDK（`10.0.302`），然后执行：

```powershell
dotnet restore --locked-mode
dotnet build Motara.slnx --no-restore
dotnet run --project src/Motara.App/Motara.App.csproj --no-build
```

如需在本地进行可选的 Release 验证，可执行：

```powershell
dotnet build Motara.slnx --configuration Release --no-restore
```

公开源码仓库有意不包含测试项目、构建输出、打包材料、私有模型和可选原生/运行时制品。
即使缺少这些内容，构建也应成功；相应的运行时依赖功能会保持不可用，直到在本地提供所需
组件。

## 运行时与分发边界

`src/Motara.App/Motara.App.csproj` 只有在对应本地路径存在时才会复制可选运行时。这些路径
不受源码控制，也不授予再分发权。特别是：

- PurismCore 是独立维护的 MIT 组件；其当前来源和合规记录见下方链接。
- FFmpeg、Spout2、NDI、MediaPipe、OpenSeeFace、ESCAPI 及相关模型不属于本仓库的源码
  分发内容。
- 未来的二进制发行版必须列明每个纳入制品的准确版本和来源，保留许可证/声明文件，满足
  其再分发条件，并在发布前更新第三方清单。

依赖清单和发行要求详见[第三方声明](THIRD-PARTY-NOTICES.zh-CN.md)。

## 法律与声明

- [许可证](LICENSE)：除文件另有说明外，Motara 自行编写的源码采用 MPL-2.0。
- [声明](NOTICE.md)：项目许可证的适用范围，以及随附声明和公共法律材料的处理方式。
- [第三方声明](THIRD-PARTY-NOTICES.zh-CN.md)：依赖、引入的图标几何、可选运行时和分发
  要求。
- [商标政策](TRADEMARKS.zh-CN.md)：名称和视觉标识不随源码许可证一并授权。
- [PurismCore 合规记录](docs/legal/PurismCore-Compliance.zh-CN.md)与
  [第 48 号指导性案例记录](docs/legal/Guiding-Case-48.zh-CN.md)：保留的法律材料，
  不构成法律意见，也不预先决定任何争议。

Motara 是独立项目，与 Apple、Live2D Inc.、DenchiSoft 及 iFacialMocap 开发者不存在隶属
或背书关系。ARKit、Live2D、Cubism、VTube Studio、iFacialMocap、NDI、FFmpeg、MediaPipe、
OpenSeeFace、Spout 等第三方名称或标识归各自权利人所有。用户应自行确认其模型、插件、
录制内容和其他素材已获得充分使用权。

## 相关仓库

- [Motara.PluginSdk](https://github.com/MotaraSoft/Motara.PluginSdk)：为未来的公共扩展
  契约与插件开发工具预留。
- [motarasoft.github.io](https://github.com/MotaraSoft/motarasoft.github.io)：Motara 网站
  的源码。

## 参与贡献

欢迎通过 GitHub Issues 提交问题和范围明确的建议。提交实现改动前，请先讨论任何涉及公开
API、文件格式、协议、许可证或第三方运行时的影响，因为这些领域目前尚未稳定。

一般事务或需要私下沟通的组织事务，请联系
[hello@motara.org](mailto:hello@motara.org)。安全问题请按照组织的
[安全政策](https://github.com/MotaraSoft/.github/security/policy)进行报告。
