# 反射探针横排图转经纬 EXR

将连续六个方形 Cubemap 面构成的横排 Texture2D 转换为经纬展开线性 HDR EXR，支持有额外填充块的横排图。

## 入口与输入

- 菜单：`Tools/Azumi/纹理生成与修改/反射探针横排图转经纬 EXR`。
- 脚本：[ReflectionProbeStripToLatLongExrWindow.cs](Editor/ReflectionProbeStripToLatLongExrWindow.cs)。
- 输入高度 N，宽度为 N 的整数倍且至少含六块；面顺序为 `+X、-X、+Y、-Y、+Z、-Z`。

## 使用步骤

1. 指定“6 面横排图”，检查横向块数。例如标准输入为 6N × N，有填充时可以是 8N × N。
2. 使用“6 面起始块索引”选择连续六面，索引从 0 开始。
3. 指定资源名和项目内输出目录，查看预计尺寸。
4. 点击“转换并导出 EXR”，检查方向和面接缝。

## 输出与限制

- 输出为 2N × N 的经纬展开 EXR，采用 ZIP 压缩。
- 面顺序或单面朝向与约定不同会导致错位；工具不接收十字布局、纵排图或普通经纬图。
- 输入分辨率取决于 Unity 实际导入尺寸，检查最大尺寸等导入设置，避免源图已被缩小。
- EXR 输出不能恢复源 PNG 或低精度图中已丢失的 HDR 信息。Cubemap 输入请使用 [Cubemap 经纬转换器](../ReflectionProbeTextureMapper/README.md)。

[返回项目首页](../../../../README.md)
