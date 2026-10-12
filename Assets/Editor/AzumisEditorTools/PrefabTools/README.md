# Prefab MeshRenderer 批次分析

扫描 Prefab 中的 MeshRenderer，统计材质槽位，并按材质聚合引用，用于发现重复材质、空槽和过多材质槽位。

## 入口与输入

- 菜单：`Tools/Azumi/Prefab工具/Prefab MeshRenderer 批次分析`。
- 脚本：[PrefabMeshRendererBatchAnalyzerWindow.cs](Editor/PrefabMeshRendererBatchAnalyzerWindow.cs)。
- 输入：Project 中的 Prefab 资源。

## 使用步骤

1. 指定“目标 Prefab”，点击“扫描 Prefab”。
2. 查看 MeshRenderer 总数、Batches 总和及材质分组数。
3. 展开 Renderer 列表，检查名称、层级路径、材质槽位数。
4. 展开材质分组，检查同一材质被哪些 Renderer 使用及槽位引用次数。

## 结果与限制

- 包含未激活子对象中的 MeshRenderer；不统计 SkinnedMeshRenderer。
- 只分析，不修改或保存 Prefab。
- 当前每个 Renderer 的 `Batches` 等于材质槽位数，汇总是槽位估算，空槽也需检查。
- 不计算 Shader Pass、阴影、灯光、静态/动态合批、GPU Instancing 或 SRP Batcher 的实际渲染结果。真实开销需结合 Profiler、Frame Debugger 验证。

[返回项目首页](../../../../README.md)
