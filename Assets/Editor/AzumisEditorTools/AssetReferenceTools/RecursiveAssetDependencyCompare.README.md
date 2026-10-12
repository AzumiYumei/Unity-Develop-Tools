# 资源依赖分析与对比

递归收集资源依赖，按类型过滤，并比较两个目标的共同和独有依赖，适合分析 Prefab、场景、材质。

## 入口与输入

- 菜单：`Tools/Azumi/资源工具/资源依赖分析与对比`。
- 脚本：[RecursiveAssetDependencyCompareWindow.cs](Editor/RecursiveAssetDependencyCompareWindow.cs)。
- 输入：主资源、可选对比资源及过滤规则。

## 使用步骤

1. 指定“主资源”，比较时再填写“对比资源”。
2. 选择是否仅统计 `Assets`、是否排除脚本与程序集。
3. 按需排除 Texture、Material、Shader、Model、Prefab、Scene、Animation、Audio。自定义扩展名可用逗号或分号分隔，例如 `.tga,.psd,.bytes`。
4. 点击“分析依赖”，查看汇总、依赖列表和引用证据。
5. 填写了对比资源时，查看交集和各自独有资源。

## 结果与限制

通过 `AssetDatabase.GetDependencies` 收集递归依赖，不修改资源，不生成 AB 包。过滤会改变最终集合；资源依赖不等于实际运行时加载集合，字符串加载资源可能不在结果中。分包归属可继续用 [AB 预览](PrefabAssetBundlePreview.README.md) 检查。

[返回目录](README.md) · [返回项目首页](../../../../README.md)
