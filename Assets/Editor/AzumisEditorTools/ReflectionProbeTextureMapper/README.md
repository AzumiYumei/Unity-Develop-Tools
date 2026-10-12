# Cubemap 转经纬图 HDRI

将 Cubemap 或反射探针的 Cubemap 投影为 2:1 的经纬展开线性 HDR EXR，提供水平旋转和水平/垂直翻转。

## 入口与依赖

- 菜单：`Tools/Azumi/纹理生成与修改/Cubemap 转经纬图 HDRI`。
- 脚本：[ReflectionProbeTextureMapperWindow.cs](Editor/ReflectionProbeTextureMapperWindow.cs)。
- 配套 Shader：[CubemapToLatLongHDRI.shader](Editor/CubemapToLatLongHDRI.shader)。

## 使用步骤

1. 指定 Cubemap 或 Reflection Probe，也可点击“使用当前选择”；窗口显示当前实际来源。
2. 选择输出宽度，高度按宽度一半生成。
3. 设置水平旋转角度、水平翻转与垂直翻转。
4. 填写资源名和项目内输出目录。
5. 点击“转换并导出 EXR”，检查输出方向、接缝和 HDR 亮度。

## 输出与限制

- 输出 ZIP 压缩 EXR，保持经纬展开 2:1 比例，作为线性 HDR 纹理使用。
- 输入必须能够解析为 Cubemap；未烘焙探针或非 Cubemap 类型会显示来源错误。
- GPU 转换需要配套 Shader。迁移时保留全部文件，输出方向可通过旋转和翻转调整。
- 此工具接收 Cubemap，不接收六面横排 Texture2D；已有横排图使用 [横排图转换器](../ReflectionProbeLatLongExporter/README.md)。

[返回项目首页](../../../../README.md)
