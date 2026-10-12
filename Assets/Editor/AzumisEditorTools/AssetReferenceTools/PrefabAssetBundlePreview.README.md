# Prefab AB 打包预览

按项目分包规则推导一组 Prefab 的包内资源、其他包资源与外部依赖包，并比较两组 Prefab 独立打包的内容重叠。

## 入口与依赖

- 菜单：`Tools/Azumi/资源工具/Prefab AB打包预览`。
- 脚本：[PrefabAssetBundlePreviewWindow.cs](Editor/PrefabAssetBundlePreviewWindow.cs)。
- 直接依赖 `BuildHelper.PrefabExt`、`AssetExt`、`TextureExt`、`AssetBundleExt`。当前仓库未找到 `BuildHelper` 类定义，需由接入项目提供或适配后使用。

## 使用步骤

1. 添加一个或多个目标 Prefab，按需设置预览包名和 `Assets` 过滤。
2. 点击“分析打包结果”，检查当前包、包内资源、其他包资源和外部依赖包。
3. 展开详情查看规则指定包、归属原因及未命中规则的提示。
4. 在“Prefab 组内容重叠对比”分别填写 A、B 两组，点击“对比两组 Prefab”，查看交集和各自独有资源。

## 规则与限制

- `BuildProjectAssetBundleMap` 等方法写有 `ResourcesEx/Prefabs`、`ResourcesEx/Configs`、`ResourcesEx/Textures` 逐资源分包及 `Raw/N60AS` 特定车模共享包规则。
- 联合预览时，根资源和相关规则包依赖可能并入当前包，已分到其他包的依赖单独展示。
- 包名为空时单目标优先使用规则包名，未命中时采用预览包名；多目标使用联合预览包名。
- 只推导结果，不触发构建或修改配置；不是自动读取任意项目构建规则的通用分析器。规则变化需同步代码，最终以真实构建验证。

[返回目录](README.md) · [返回项目首页](../../../../README.md)
