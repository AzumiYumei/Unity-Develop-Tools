# 反射探针 Cubemap 表面绘制

在 Scene View 的模型表面操作笔刷，根据命中位置、世界法线和视角计算反射方向，将颜色写入该方向对应的 Cubemap 像素。

## 入口与输入

- 菜单：`Tools/Azumi/纹理生成与修改/反射探针 Cubemap 表面绘制`。
- 脚本：[ReflectionProbeCubemapPainterWindow.cs](Editor/ReflectionProbeCubemapPainterWindow.cs)。
- 输入：绘制模型、可编辑 Cubemap、可选 Reflection Probe 和指定视角相机。
- 绘制模型的 Mesh 必须开启 Read/Write，否则会跳过并无法命中。

## 使用步骤

1. 指定绘制模型与 Reflection Probe，也可“使用当前选择对象”。
2. “从 Probe 读取 Cubemap”，然后“从当前 Probe/Cubemap 复制为可编辑资产”；也可设置尺寸、资源名和输出目录后新建可编辑 Cubemap。
3. 需要预览探针效果时点击“把当前 Cubemap 绑定到 Probe”，会将探针设为 Custom 并绑定资源。
4. 设置笔刷颜色、大小、不透明度、硬度，启用“Scene View 绘制”，在模型表面左键拖拽。
5. 未开启“每笔实时 Apply”时，点击“Apply 未提交像素”刷新；完成后“保存 Assets”。

## 输出与限制

- 输出或修改的是 Cubemap `.asset`，不是模型的 UV 贴图。改变视角会改变反射方向与受影响像素。
- “擦除模式”使用擦除颜色覆盖，不是恢复原始探针数据；笔刷操作记录整对象 Undo。
- Cubemap 需要可读写；导入的烘焙图不适合直接写入时先复制为可编辑资产。
- 模型几何变化后“重建模型缓存”。绑定探针和绘制都会修改对象或资源，检查共享 Cubemap 的使用范围。
- SkinnedMeshRenderer 使用 sharedMesh 建立缓存，不按骨骼动画实时 BakeMesh；变形后的表面命中不保证准确。

[返回项目首页](../../../../README.md)
