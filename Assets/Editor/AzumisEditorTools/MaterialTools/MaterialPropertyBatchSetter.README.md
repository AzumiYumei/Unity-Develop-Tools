# 批量材质属性赋值

为多个材质统一应用多条 Shader Property 规则，支持 Float、Color、Vector、Texture。

## 入口

- 菜单：`Tools/Azumi/模型材质/批量材质属性赋值工具`。
- 脚本：[MaterialPropertyBatchSetterWindow.cs](Editor/MaterialPropertyBatchSetterWindow.cs)。

## 使用步骤

1. 选中材质并“添加当前选中材质”，或指定文件夹并“扫描文件夹材质”，递归收集并去重。
2. 核对列表，移除不需要处理的材质；可“清理空引用”。
3. “新增属性规则”，填写内部属性名、类型和值。例如 `_BaseColor` 配合 Color，名称取决于目标 Shader。
4. 勾选要执行的规则，点击“执行批量赋值”。
5. 在 Inspector 核对结果和执行反馈。

## 结果与限制

直接修改和保存材质，并记录材质 Undo。不存在的属性跳过，规则类型应匹配 Shader 定义。Texture 可为空以清除贴图引用。工具不切换 Shader，不自动处理关键字或 ShaderGUI 联动，需另外检查。

[返回目录](README.md) · [返回项目首页](../../../../README.md)
