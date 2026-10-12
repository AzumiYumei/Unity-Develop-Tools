# FBX 材质映射与预制件材质替换

修改 FBX Importer 的 Remapped Materials，或替换 Prefab / 场景对象的 Renderer 材质。支持以 FBX 原始材质名匹配已发生重映射的模型。

## 入口

- 菜单：`Tools/Azumi/模型材质/FBX Remapped Materials 工具`。
- 脚本：[FbxMaterialRemapWindow.cs](Editor/FbxMaterialRemapWindow.cs)。

## FBX 映射

1. 指定“目标 FBX”，读取材质；刷新时点击“重新锁定并读取材质”。
2. 选择全局或文件夹查找，文件夹模式指定材质目录。
3. 按需启用“使用 FBX 本体材质名称作为查找 Key”。
4. 点击“按名称查找材质”，展开结果核对候选数和路径，可手动更改目标材质。
5. 点击“应用 Remapped Materials”，写入 Importer 并重新导入。

## Prefab / 场景对象替换

1. 指定目标预制件资源或场景对象及材质查找目录。
2. 按需启用“使用 Mesh 对应原 FBX 材质名作为查找 Key”。
3. 点击“按名称填充替换材质”，核对后“应用预制件材质替换”。
4. “同名材质一键全部替换”直接根据指定目录执行替换，使用前确认目录与结果范围。

## 结果与限制

FBX 操作修改导入器，Prefab 操作保存资源，场景操作修改 Renderer 并处理实例覆盖。同名多候选及原始名称冲突需手动核对。FBX 重新导入会刷新依赖对象，Importer 写入不是完整 Undo 流程，应保留版本记录。

[返回目录](README.md) · [返回项目首页](../../../../README.md)
