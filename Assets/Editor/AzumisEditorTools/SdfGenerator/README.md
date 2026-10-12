# SDF 生成器

根据输入纹理阈值生成有符号距离场，通过 GPU Jump Flood 传播轮廓种子。支持亚像素边界估计、高倍率输出和高精度 EXR。

## 入口与依赖

- 菜单：`Tools/Azumi/纹理生成与修改/SDF 生成器`。
- 脚本：[SdfTextureGeneratorWindow.cs](Editor/SdfTextureGeneratorWindow.cs)。
- GPU 程序：[SdfTextureGeneratorCS.compute](Editor/SdfTextureGeneratorCS.compute)，需要 Compute Shader 支持。

## 使用步骤

1. 指定源纹理和输出目录。
2. 设置“白色阈值”（0～255），选择使用 G 通道判定；关闭后使用 RGB 平均值，不是 Alpha。
3. 设置扩散像素和输出倍率（1～8）。输出宽高按倍率放大，扩散距离同步按倍率缩放。
4. 选择“高精度EXR导出”，需要 PNG 时关闭；按需开启反转输出。
5. 点击“预览生成 SDF”检查效果；确认后点击“生成并导出 SDF”，再用 Shader 验证边缘。

## 输出与参数

- 未反转时，轮廓值约为 0.5，内部高于 0.5，外部低于 0.5；距离经扩散范围归一化并截断到 0～1。
- 高精度输出使用 EXR，普通输出使用 PNG；预览为较低精度显示，不应将预览精度等同于 EXR。
- 导出设置使用线性、不压缩、Clamp、关闭 MipMap 等适合距离场的参数，作为数据纹理采样。

## 使用限制

输入应同时有内外区域，阈值会决定轮廓。倍率越高显存和计算成本越大，宽高都放大意味着像素数量按倍率平方增长。GPU Jump Flood 是近似距离传播，不用于需要严格几何距离精度的计算。

[返回项目首页](../../../../README.md)
