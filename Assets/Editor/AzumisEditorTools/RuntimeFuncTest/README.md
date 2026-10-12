# 运行时方法调用工具

在窗口中列出 GameObject 上 MonoBehaviour 的 Public 实例方法，填写参数后通过反射调用，适合手动触发调试逻辑。

## 入口

- 菜单：`Tools/Azumi/运行时方法调用工具`。
- 脚本：[RuntimeMethodInvokerWindow.cs](Editor/RuntimeMethodInvokerWindow.cs)。

## 使用步骤

1. 在 `Add GameObject` 拖入目标，可添加多个对象。
2. 查看每个对象自身的 MonoBehaviour 和方法列表。
3. 填写参数，点击 `Invoke`。
4. 在 Console 查看调用成功、返回值或异常；用 `Remove` 移除目标对象。

## 支持范围

- 列出直接声明在当前组件类型中的 Public 实例方法，排除属性访问器；不列出继承方法、静态或私有方法，也不自动遍历子对象组件。
- 参数输入支持 int、float、string、bool、Vector2、Vector3、Color、枚举（含 Flags）和 UnityEngine.Object 派生类型。
- 未支持类型显示 `Unsupported Type`，不提供有效编辑控件。复杂参数、泛型或特殊签名需由项目自行提供适合调试的包装方法。

## 结果与限制

调用真实执行组件方法，可能修改场景或触发游戏逻辑，不会自动记录 Undo。窗口未强制要求 Play Mode；依赖运行时状态的方法应进入 Play Mode 后调用。Coroutine 或异步方法的返回对象不会自动作为调度任务启动。场景切换后销毁的对象会从列表清理。

[返回项目首页](../../../../README.md)
