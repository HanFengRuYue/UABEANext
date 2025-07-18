# UABEANext

UABEANext 是一款基于 .NET 的跨平台 Unity 资源编辑与预览工具，支持多种资源类型的导入、导出、批量处理和可视化预览。项目采用插件化架构，便于扩展和自定义，适用于游戏开发、资源提取、二次开发等多种场景。

## 主要特性

- **多资源类型支持**：内置音频、字体、网格（模型）、文本、贴图等多种资源插件，支持常见 Unity 资源的读取、导出与导入。
- **插件化架构**：每种资源类型均为独立插件，便于扩展和维护。开发者可根据需求自定义或新增插件。
- **跨平台**：基于 Avalonia UI 框架，支持 Windows 和 Linux 系统。
- **可视化预览**：内置资源预览器，支持模型、贴图、音频等资源的实时预览与交互操作。
- **批量处理**：支持批量导入、导出和转换资源，提升工作效率。
- **第三方库集成**：集成 AssetsTools.NET 及多种本地库，支持复杂资源格式的解析与处理。

## 目录结构

- `UABEANext4/`：主程序，包含核心逻辑、插件接口、服务、UI 视图与视图模型等。
- `UABEANext4.Desktop/`：桌面端启动器与配置。
- `AudioPlugin/`、`FontPlugin/`、`MeshPlugin/`、`TextAssetPlugin/`、`TexturePlugin/`：各类资源插件。
- `PluginPreviewer/`、`PluginPreviewer.Desktop/`：资源预览器相关模块。
- `Libs/`：第三方库（如 AssetsTools.NET）。
- `NativeLibs/`：平台相关本地库（如 cuttlefish、PVRTexLib）。
- `ReleaseFiles/`：发布相关文件。
- `readme.md`：自述文件。
- `UABEANext4.sln`：解决方案文件。