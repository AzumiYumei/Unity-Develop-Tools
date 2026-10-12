# Unity Develop Tools

Unity 编辑器开发工具集，覆盖资源引用、材质处理、纹理生成、反射探针、Prefab 分析、截图录屏和运行时调试。源码位于 `Assets/Editor/AzumisEditorTools`，共提供 20 个工具窗口。

## 环境与安装

- 本仓库项目版本：Unity **2022.3.62f3**，URP **14.0.12**。
- 材质 Shader Variant 收集器直接依赖 URP 类型；纹理通道工具与 SDF 生成器需要支持 Compute Shader 的图形环境。
- 两个录屏工具需要自行安装 ffmpeg 并配置路径；仓库不包含 ffmpeg。
- Prefab AB 打包预览依赖项目自定义 `BuildHelper`。当前仓库源码中未找到该类，使用前需补齐依赖或适配预览规则。该依赖缺失可能导致编辑器脚本编译失败，影响其他窗口加载。

使用完整项目：通过 Unity Hub 打开本仓库目录，等待导入与脚本编译完成。

移植到已有项目：复制所需工具目录及对应 `.meta` 文件，保留 `Editor` 层级。Compute Shader、转换 Shader、相机轨迹脚本等配套文件需一起复制。个别菜单位于 `Tools/Recording` 或 `Tools/纹理工具`，请按下表查找。

## 工具索引

### 资源与 Prefab

| 工具 | 菜单入口 | 说明 |
| --- | --- | --- |
| 资源引用查找与替换 | `Tools/Azumi/资源工具/资源引用查找与替换` | [扫描、替换与清空引用](Assets/Editor/AzumisEditorTools/AssetReferenceTools/AssetReferenceReplace.README.md) |
| 资源依赖分析与对比 | `Tools/Azumi/资源工具/资源依赖分析与对比` | [递归依赖、交集与差异](Assets/Editor/AzumisEditorTools/AssetReferenceTools/RecursiveAssetDependencyCompare.README.md) |
| Prefab AB 打包预览 | `Tools/Azumi/资源工具/Prefab AB打包预览` | [包归属与内容重叠](Assets/Editor/AzumisEditorTools/AssetReferenceTools/PrefabAssetBundlePreview.README.md) |
| 未使用资源查找 | `Tools/Azumi/资源工具/未使用资源查找` | [查找无资源依赖引用的候选项](Assets/Editor/AzumisEditorTools/UnusedAssetTools/README.md) |
| Prefab MeshRenderer 批次分析 | `Tools/Azumi/Prefab工具/Prefab MeshRenderer 批次分析` | [材质槽位统计与材质分组](Assets/Editor/AzumisEditorTools/PrefabTools/README.md) |

### 材质与 Shader

| 工具 | 菜单入口 | 说明 |
| --- | --- | --- |
| FBX 材质映射与预制件材质替换 | `Tools/Azumi/模型材质/FBX Remapped Materials 工具` | [按名称匹配与替换](Assets/Editor/AzumisEditorTools/MaterialTools/FbxMaterialRemap.README.md) |
| 批量材质属性赋值 | `Tools/Azumi/模型材质/批量材质属性赋值工具` | [统一设置材质属性](Assets/Editor/AzumisEditorTools/MaterialTools/MaterialPropertyBatchSetter.README.md) |
| 材质烘焙成纹理 | `Tools/Azumi/纹理生成与修改/材质烘焙成纹理` | [全屏渲染与 PNG 导出](Assets/Editor/AzumisEditorTools/MaterialTools/MaterialToTextureBaker.README.md) |
| 材质无效属性清理 | `Tools/Azumi/材质工具/材质无效属性清理` | [清理旧属性残留](Assets/Editor/AzumisEditorTools/MaterialTools/MaterialUnusedPropertyCleaner.README.md) |
| 材质 Shader Variant 收集器 | `Tools/Azumi/Shader工具/材质 Shader Variant 收集器` | [生成 ShaderVariantCollection](Assets/Editor/AzumisEditorTools/MaterialShaderVariantCollector/README.md) |

### 纹理与反射探针

| 工具 | 菜单入口 | 说明 |
| --- | --- | --- |
| Ramp Tex 生成器 | `Tools/Azumi/纹理生成与修改/Ramp Tex 生成器` | [渐变、参考图与材质绑定](Assets/Editor/AzumisEditorTools/RampTexMaker/README.md) |
| 纹理通道分离与合并 | `Tools/Azumi/纹理生成与修改/纹理通道分离与合并` | [提取、反相与 RGBA 打包](Assets/Editor/AzumisEditorTools/TextureChannelPacker/README.md) |
| SDF 生成器 | `Tools/Azumi/纹理生成与修改/SDF 生成器` | [高倍率距离场与 EXR/PNG](Assets/Editor/AzumisEditorTools/SdfGenerator/README.md) |
| Cubemap 转圆柱展开图 | `Tools/纹理工具/Cubemap 转圆柱展开图` | [HDR 解码与高精度导出](Assets/Editor/AzumisEditorTools/CubemapConverter/README.md) |
| Cubemap 转经纬图 HDRI | `Tools/Azumi/纹理生成与修改/Cubemap 转经纬图 HDRI` | [Cubemap 或探针转 EXR](Assets/Editor/AzumisEditorTools/ReflectionProbeTextureMapper/README.md) |
| 反射探针横排图转经纬 EXR | `Tools/Azumi/纹理生成与修改/反射探针横排图转经纬 EXR` | [六面横排图转经纬图](Assets/Editor/AzumisEditorTools/ReflectionProbeLatLongExporter/README.md) |
| 反射探针 Cubemap 表面绘制 | `Tools/Azumi/纹理生成与修改/反射探针 Cubemap 表面绘制` | [在模型上绘制反射图](Assets/Editor/AzumisEditorTools/ReflectionProbeCubemapPainter/README.md) |

### 截图、录屏与调试

| 工具 | 菜单入口 | 说明 |
| --- | --- | --- |
| 截图与录屏工具 | `Tools/Azumi/截图工具` | [截图、轨迹回放与机位运镜](Assets/Editor/AzumisEditorTools/ScreenshotTool/README.md) |
| Pure Screen Recorder | `Tools/Recording/Pure Screen Recorder` | [固定帧数相机录制](Assets/Editor/AzumisEditorTools/PureScreenRecorder/README.md) |
| 运行时方法调用工具 | `Tools/Azumi/运行时方法调用工具` | [填写参数调用 Public 方法](Assets/Editor/AzumisEditorTools/RuntimeFuncTest/README.md) |

## 常用流程

1. **整理材质**：先修正 FBX 材质映射，再批量赋值；切换 Shader 后检查旧属性和纹理引用。
2. **排查资源**：用引用工具找来源，用依赖对比检查共有资源；需要了解分包时使用项目规则适配后的 AB 预览。
3. **生成着色数据**：Ramp 生成渐变，通道工具组合遮罩，SDF 生成轮廓距离场。数据纹理通常应使用线性导入设置。
4. **导出环境图**：Cubemap 使用经纬 HDRI 或圆柱展开工具，六面横排图使用横排转换器。保留 HDR 时选择 EXR。
5. **录制视频**：保存和回放运镜使用截图工具；自定义帧率、固定帧数与游戏时间步使用 Pure Screen Recorder。

## 使用边界与常见问题

- 所有窗口位于 `Editor` 目录，仅供编辑器使用。`CameraTrajectoryPreset` 也定义在该目录，不应直接作为 Player 运行时组件的依赖。
- 未使用资源结果只表示扫描范围内未发现资源依赖引用，不能覆盖字符串加载或远程配置的全部用途。
- Prefab 分析的 `Batches` 按材质槽位估算，不代表 Profiler 或 Frame Debugger 的实际 Draw Call。
- Shader Variant 收集结果基于材质和当前全局/URP 状态，不保证覆盖运行时动态切换的全部组合。
- 引用替换、材质修改、Ramp 自动覆盖和资源删除会写入资源。执行前可保存版本记录；各工具的撤销能力详见对应文档。
- 菜单未出现先看 Console 编译错误；ffmpeg 启动失败检查路径与编码器；Compute Shader 加载失败检查配套文件是否完整。

## 目录与维护

`AssetReferenceTools` 和 `MaterialTools` 的目录 README 提供子工具索引，其余工具目录可直接阅读 README。`Azumi_N60_Testings` 是测试资源目录，不是工具窗口；`Screenshot_Trajectory_*.asset` 是相机轨迹资源。

新增工具时同步更新首页索引和工具说明，代码注释使用中文。本文依据仓库当前实现编写，未额外声明其他 Unity 版本或渲染管线的兼容性。
