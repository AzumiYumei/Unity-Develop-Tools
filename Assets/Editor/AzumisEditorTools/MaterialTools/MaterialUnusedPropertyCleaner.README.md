# 材质无效属性清理

查找当前 Shader 不再声明、但材质仍保留非空纹理引用或非零数值的旧属性，并单项或批量清理。

## 入口

- 菜单：`Tools/Azumi/材质工具/材质无效属性清理`。
- 脚本：[MaterialUnusedPropertyCleanerWindow.cs](Editor/MaterialUnusedPropertyCleanerWindow.cs)。

## 使用步骤

1. 范围优先级：目标材质 → 目标目录 → Project 选中的材质和文件夹 → 无有效选择时整个 `Assets`。
2. 勾选 Texture、Int、Float、Color 检查类型。
3. 点击“扫描无效属性”，查看材质、Shader、属性名、残留值与纹理路径。
4. 用“仅清理该材质”处理单项，或“清理无效属性”处理本次结果。
5. 重新扫描并检查依赖变化。

## 结果与限制

修改并保存材质序列化属性，保留当前 Shader 的有效属性。只关注存在残留内容的项，不列出所有空值旧属性。不会删除纹理资源文件；日后切回旧 Shader 时旧值不会自动恢复，执行前保留版本记录。

[返回目录](README.md) · [返回项目首页](../../../../README.md)
