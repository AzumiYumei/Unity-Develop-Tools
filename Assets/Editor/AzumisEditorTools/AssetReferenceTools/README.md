# 资源引用与依赖工具

本目录包含三个独立窗口，入口均位于 `Tools/Azumi/资源工具`。

| 工具 | 适用问题 | 文档 |
| --- | --- | --- |
| 资源引用查找与替换 | 谁引用了目标？如何替换或清空引用？ | [使用说明](AssetReferenceReplace.README.md) |
| 资源依赖分析与对比 | 目标依赖什么？两个目标有哪些共同依赖？ | [使用说明](RecursiveAssetDependencyCompare.README.md) |
| Prefab AB 打包预览 | 按项目规则，依赖会进入哪些包？ | [使用说明](PrefabAssetBundlePreview.README.md) |

AB 预览额外依赖项目自定义 `BuildHelper`；当前仓库未包含该类定义，使用或移植前需适配。

[返回项目首页](../../../../README.md)
