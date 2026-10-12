# 材质烘焙成纹理

通过 `Graphics.Blit` 在全屏 Quad 上执行材质并导出 PNG，适合 UV 程序纹理和全屏效果。

## 入口与输入

- 菜单：`Tools/Azumi/纹理生成与修改/材质烘焙成纹理`。
- 脚本：[MaterialToTextureBakerWindow.cs](Editor/MaterialToTextureBakerWindow.cs)。
- 输入：材质、宽高、PassIndex 和导入参数。

## 使用步骤

1. 指定材质和纹理尺寸，PassIndex 从 `0` 开始检查，确保索引有效。
2. 选择 sRGB、MipMap、Alpha Is Transparency 参数。
3. 点击“生成预览”核对效果与 Alpha。
4. 点击“导出 PNG 纹理”，选择项目内路径并检查导入结果。

## 输出与限制

输出 PNG，自动应用选定导入参数。UV 为 0～1，不提供真实模型的法线、切线、世界位置或场景光照；依赖这些数据的 Shader 可能与场景不同。不是模型 UV 展开或高低模投射工具。PNG 不保留 HDR 超范围值；错误 Pass 或不适合 Blit 的 Shader 可能输出黑图。

[返回目录](README.md) · [返回项目首页](../../../../README.md)
