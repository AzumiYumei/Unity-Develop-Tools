using UnityEngine;
using UnityEditor;
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;

// ================================================================
// Runtime Method Invoker
// 
// 文件名称: RuntimeMethodInvokerWindow.cs
// 版本: 1.1
// 
// 描述: 运行时方法调用工具窗口，用于在 Editor 中调用运行时的 GameObject 方法。
// 
// 修改历史:
// 2025-12-17 - 修复切换场景时因对象销毁导致的 GUI 报错问题。
// ================================================================

public class RuntimeMethodInvokerWindow : EditorWindow
{
    // 使用 List 管理多个 GameObject
    private List<GameObject> m_TargetGameObjects = new List<GameObject>();
    private Vector2 m_ScrollPosition;
    
    // 用于存储方法参数的缓存，Key格式: InstanceID_MethodName_ParamIndex_ParamName
    private Dictionary<string, object> m_ParameterCache = new Dictionary<string, object>();
    
    // 临时变量，用于接收拖拽输入
    private GameObject m_TempDragObject;

    [MenuItem("Tools/Azumi/运行时方法调用工具")]
    public static void ShowWindow()
    {
        GetWindow<RuntimeMethodInvokerWindow>("Method Invoker");
    }

    private void OnGUI()
    {
        // 修复：在绘制前清理已销毁的对象，防止 Layout/Repaint 不一致导致的报错
        m_TargetGameObjects.RemoveAll(go => go == null);

        EditorGUILayout.Space();
        DrawHeader();
        EditorGUILayout.Space();

        if (m_TargetGameObjects.Count == 0)
        {
            EditorGUILayout.HelpBox("请在上方拖入 GameObject 以开始。", MessageType.Info);
        }

        m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);

        // 遍历并绘制所有已添加的 GameObject
        for (int i = 0; i < m_TargetGameObjects.Count; i++)
        {
            GameObject go = m_TargetGameObjects[i];
            DrawGameObjectSection(go, i);
        }

        EditorGUILayout.EndScrollView();
    }

    /// <summary>
    /// 绘制头部：添加 GameObject 的区域
    /// </summary>
    private void DrawHeader()
    {
        EditorGUILayout.LabelField("添加目标对象", EditorStyles.boldLabel);
        
        // 绘制一个空的 ObjectField 用于接收拖拽
        GameObject newGo = (GameObject)EditorGUILayout.ObjectField("Add GameObject", null, typeof(GameObject), true);
        
        if (newGo != null)
        {
            if (!m_TargetGameObjects.Contains(newGo))
            {
                m_TargetGameObjects.Add(newGo);
            }
            else
            {
                Debug.LogWarning($"GameObject '{newGo.name}' is already in the list.");
            }
        }
    }

    /// <summary>
    /// 绘制单个 GameObject 的所有内容
    /// </summary>
    private void DrawGameObjectSection(GameObject go, int index)
    {
        EditorGUILayout.BeginVertical("box");
        
        // GameObject 标题栏
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField($"> {go.name}", EditorStyles.boldLabel);
        
        GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
        if (GUILayout.Button("Remove", GUILayout.Width(60)))
        {
            m_TargetGameObjects.RemoveAt(index);
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            // 退出当前 GUI 循环，强制重绘，防止 Layout/Repaint 不一致
            GUIUtility.ExitGUI();
        }
        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();

        // 绘制该 GameObject 下的所有 Mono 脚本
        DrawComponentList(go);

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space();
    }

    /// <summary>
    /// 绘制组件列表
    /// </summary>
    private void DrawComponentList(GameObject go)
    {
        MonoBehaviour[] monos = go.GetComponents<MonoBehaviour>();

        foreach (var mono in monos)
        {
            if (mono == null) continue; // 可能是丢失的脚本引用

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(mono.GetType().Name, EditorStyles.boldLabel);
            
            DrawMethods(mono);

            EditorGUILayout.EndVertical();
        }
    }

    /// <summary>
    /// 绘制单个组件的所有Public方法
    /// </summary>
    private void DrawMethods(MonoBehaviour mono)
    {
        // 获取所有Public且声明在当前类中的实例方法（排除属性访问器等）
        MethodInfo[] methods = mono.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName) // 排除 getter/setter
            .ToArray();

        if (methods.Length == 0)
        {
            EditorGUILayout.LabelField("  无 Public 方法", EditorStyles.miniLabel);
        }

        foreach (var method in methods)
        {
            DrawSingleMethod(mono, method);
        }
    }

    /// <summary>
    /// 绘制单个方法及其参数输入
    /// </summary>
    private void DrawSingleMethod(MonoBehaviour mono, MethodInfo method)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField(method.Name); // 方法名不加粗，区别于类名

        ParameterInfo[] parameters = method.GetParameters();
        object[] invokeArgs = new object[parameters.Length];

        // 绘制参数输入
        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo param = parameters[i];
            string key = GetParameterKey(mono, method, param, i);
            
            // 确保缓存中有默认值
            if (!m_ParameterCache.ContainsKey(key))
            {
                m_ParameterCache[key] = GetDefaultValue(param.ParameterType);
            }

            // 绘制参数输入并更新缓存
            m_ParameterCache[key] = DrawParameterField(param.Name, m_ParameterCache[key], param.ParameterType);
            invokeArgs[i] = m_ParameterCache[key];
        }

        // 调用按钮
        if (GUILayout.Button("Invoke"))
        {
            try
            {
                object result = method.Invoke(mono, invokeArgs);
                if (method.ReturnType != typeof(void))
                {
                    Debug.Log($"<color=cyan>[{mono.GetType().Name}.{method.Name}]</color> Result: {result}");
                }
                else
                {
                    Debug.Log($"<color=green>[{mono.GetType().Name}.{method.Name}]</color> Invoked successfully.");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error invoking {method.Name}: {e.Message}\n{e.StackTrace}");
            }
        }

        EditorGUILayout.EndVertical();
    }

    /// <summary>
    /// 根据类型绘制不同的输入框
    /// </summary>
    private object DrawParameterField(string label, object value, Type type)
    {
        if (type == typeof(int)) return EditorGUILayout.IntField(label, (int)value);
        if (type == typeof(float)) return EditorGUILayout.FloatField(label, (float)value);
        if (type == typeof(string)) return EditorGUILayout.TextField(label, (string)value);
        if (type == typeof(bool)) return EditorGUILayout.Toggle(label, (bool)value);
        if (type == typeof(Vector3)) return EditorGUILayout.Vector3Field(label, (Vector3)value);
        if (type == typeof(Vector2)) return EditorGUILayout.Vector2Field(label, (Vector2)value);
        if (type == typeof(Color)) return EditorGUILayout.ColorField(label, (Color)value);
        if (type.IsEnum)
        {
            // 安全处理：如果 value 是 int（可能来自缓存或默认值异常），转为枚举实例
            if (value is int iVal)
            {
                value = Enum.ToObject(type, iVal);
            }

            // 检查是否带有 [Flags] 属性
            bool hasFlags = type.GetCustomAttributes(typeof(FlagsAttribute), false).Length > 0;
            if (hasFlags)
            {
                return EditorGUILayout.EnumFlagsField(label, (Enum)value);
            }
            return EditorGUILayout.EnumPopup(label, (Enum)value);
        }
        if (typeof(UnityEngine.Object).IsAssignableFrom(type))
        {
            return EditorGUILayout.ObjectField(label, (UnityEngine.Object)value, type, true);
        }

        EditorGUILayout.LabelField(label, $"Unsupported Type: {type.Name}");
        return value;
    }

    private string GetParameterKey(MonoBehaviour mono, MethodInfo method, ParameterInfo param, int index)
    {
        // 使用 InstanceID 确保不同组件实例有独立参数存储
        return $"{mono.GetInstanceID()}_{method.Name}_{index}_{param.Name}";
    }

    private object GetDefaultValue(Type t)
    {
        if (t == typeof(string)) return "";
        if (t.IsValueType) return Activator.CreateInstance(t);
        return null;
    }
}
