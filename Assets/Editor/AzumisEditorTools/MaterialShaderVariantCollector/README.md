# 材质 Shader Variant 收集器

递归扫描多个目录中的材质，按 Shader 与关键字组合聚合，结合 PassType 生成 `ShaderVariantCollection` 资源。

## 入口与依赖

- 菜单：`Tools/Azumi/Shader工具/材质 Shader Variant 收集器`。
- 脚本：[MaterialShaderVariantCollectorWindow.cs](Editor/MaterialShaderVariantCollectorWindow.cs)。
- 直接使用 `UnityEngine.Rendering.Universal`，需要 URP；本仓库配置 URP 14.0.12。

## 使用步骤

1. 添加目录，可使用当前选中的文件夹、空目录项或 `Assets` 根目录。
2. 选择是否合并当前启用的全局关键字，是否依据当前 URP Asset 与构建平台补充管线派生关键字。
3. 点击“扫描材质变体”，检查 Shader、关键字组合、可生成变体和无效关键字材质数量。
4. 使用搜索、展开/折叠、显示 Pass 和使用材质选项核对来源。
5. 点击“一键生成 ShaderVariantCollection”，选择项目内保存位置。

## 输出与限制

- 扫描包括子目录及模型中的 Material 子资源，重叠目录会去重。
- 输出为可供项目引用的 ShaderVariantCollection；生成资源不会自动完成运行时预热或项目集成。
- 材质关键字、当前全局状态与 URP 推导只覆盖可确定组合，动态灯光、雾、Lightmap 和运行时关键字切换不保证完整覆盖。
- 无效关键字、Pass 识别警告和生成失败信息应逐项检查。发布前仍需通过项目实际场景验证变体与剔除配置。

[返回项目首页](../../../../README.md)
