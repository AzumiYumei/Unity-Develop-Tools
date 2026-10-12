# Cubemap 转圆柱展开图

将 Cubemap 导出为二维展开图片，支持水平旋转、Mip 层级、反射探针 HDR 解码以及全精度 EXR。

## 入口与依赖

- 菜单：`Tools/纹理工具/Cubemap 转圆柱展开图`。
- 脚本：[CubemapToCylindricalWindow.cs](Editor/CubemapToCylindricalWindow.cs)。
- 配套 Shader：[HiddenCubemapToCylindrical.shader](Editor/HiddenCubemapToCylindrical.shader)。
- 输入：Cubemap，或从 Reflection Probe 读取的 Cubemap。

## 使用步骤

1. 指定源 Cubemap，或选择反射探针并读取其贴图。
2. 设置输出尺寸，可“按源尺寸设置”；配置水平旋转和 Mip 层级。
3. 选择 EXR 或 PNG。烘焙探针通常保留“应用反射探针 HDR 解码”。
4. EXR 模式可启用“32-bit Float 无压缩 EXR”。“临时无压缩重导入源图”和“按源完整分辨率输出”仅在该全精度选项也开启时生效。
5. 点击“转换并保存”，选择输出文件并检查方向与高亮。

## 输出与限制

- 按源完整分辨率时，面宽 N 对应目标尺寸 4N × 2N；宽高分别限制为最大 16384，N 大于 4096 时可能不再保持 2:1。手动尺寸由窗口控制。
- PNG 截断 HDR 高亮到普通 LDR 范围，保留动态范围时使用 EXR。
- 全精度流程使用 Float RenderTexture / Texture2D，无压缩 EXR 文件较大；4K × 2K RGBAFloat 像素数据约 128 MiB。
- 临时重导入会改变源 Importer 后恢复设置；检查转换反馈，尤其是源资源不可编辑时。
- 需要配套转换 Shader，不能只复制窗口脚本。探针纹理必须能作为 Cubemap 使用。

[返回项目首页](../../../../README.md)
