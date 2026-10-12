# 纹理通道分离与合并

通过 Compute Shader 将单通道提取为灰度图，或将多张纹理的通道组合为一张 RGBA PNG。支持 R/G/B/A 和反相通道 `1-R`、`1-G`、`1-B`、`1-A`。

## 入口与依赖

- 菜单：`Tools/Azumi/纹理生成与修改/纹理通道分离与合并`。
- 脚本：[TextureChannelPackerWindow.cs](Editor/TextureChannelPackerWindow.cs)。
- GPU 程序：[TextureChannelPackerCS.compute](Editor/TextureChannelPackerCS.compute)，移植时需一并保留，需要 Compute Shader 支持。

## 使用步骤

1. 选择模式：提取单通道或打包多通道。
2. 提取时指定源纹理及通道，结果 RGB 为所选通道灰度，Alpha 为 1。
3. 打包时分别设置目标 R/G/B/A 的输入纹理和来源通道；固定值可选择 Black（0）或 White（1）。
4. 点击“处理并导出”，选择 PNG 路径，导入后核对各通道。

## 尺寸与色彩空间

- 提取尺寸与源纹理一致；打包根据输入确定最大宽高，较小纹理通过线性 Clamp 采样缩放。
- 提取 Alpha 使用原始像素读取，打包通道使用纹理采样。反相在所读取的通道值上执行。
- 提取导出参考源纹理的 sRGB 设置，打包导出参考 RGB 输入槽的 sRGB 状态，按需执行线性到 Gamma 转换。
- 遮罩、粗糙度、金属度等数据纹理应检查源图及导出图的 sRGB 设置；混合颜色纹理和线性数据时需核对数值。

## 常见问题

Compute Shader 未找到时检查文件是否完整并使用“重试加载 Compute Shader”。打包至少指定一张纹理以确定尺寸；空输入由黑色占位图代替，其 Alpha 不等同于 0，需要固定通道时明确选择 Black 或 White。PNG 不保留 HDR 数值。

[返回项目首页](../../../../README.md)
