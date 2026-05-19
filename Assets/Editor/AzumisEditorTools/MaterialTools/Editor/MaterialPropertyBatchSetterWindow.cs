using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// ================================================================
// 批量材质属性赋值工具
//
// 文件名称: MaterialPropertyBatchSetterWindow.cs
// 版本: 1.1
//
// 描述: 提供一个Editor窗口，用于对多个材质资源的多个 Shader Property
//       进行统一赋值。支持 Float/Color/Vector/Texture 四种常见属性类型。
//
// 修改历史:
// 2026-05-08 - 初始版本
// 2026-05-08 - 新增文件夹扫描材质功能
// ================================================================

namespace AzumisEditorTools
{
    public class MaterialPropertyBatchSetterWindow : EditorWindow
    {
        private enum ShaderPropertyType
        {
            Float,
            Color,
            Vector,
            Texture
        }

        [Serializable]
        private sealed class ShaderPropertyRule
        {
            public bool m_Enable = true;
            public string m_PropertyName = "_BaseColor";
            public ShaderPropertyType m_PropertyType = ShaderPropertyType.Color;
            public float m_FloatValue = 0f;
            public Color m_ColorValue = Color.white;
            public Vector4 m_VectorValue = Vector4.zero;
            public Texture m_TextureValue;
        }

        private readonly List<Material> m_TargetMaterials = new List<Material>();
        private readonly List<ShaderPropertyRule> m_PropertyRules = new List<ShaderPropertyRule>();
        private DefaultAsset m_MaterialFolder;
        private Vector2 m_MaterialsScrollPosition;
        private Vector2 m_RulesScrollPosition;

        [MenuItem("Tools/Azumi/模型材质/批量材质属性赋值工具")]
        public static void ShowWindow()
        {
            MaterialPropertyBatchSetterWindow window = GetWindow<MaterialPropertyBatchSetterWindow>("批量材质属性赋值");
            window.minSize = new Vector2(620f, 520f);
            window.Show();
        }

        private void OnEnable()
        {
            if (m_PropertyRules.Count == 0)
            {
                m_PropertyRules.Add(new ShaderPropertyRule());
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("批量材质属性赋值", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("用于对多个材质资源统一设置多个 Shader Property 值。仅对“材质中存在该属性名”的项生效。", MessageType.Info);
            EditorGUILayout.Space(6f);

            DrawMaterialSection();
            EditorGUILayout.Space(8f);
            DrawPropertyRuleSection();
            EditorGUILayout.Space(10f);
            DrawApplyButtons();
        }

        private void DrawMaterialSection()
        {
            EditorGUILayout.LabelField("目标材质列表", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("添加当前选中材质", GUILayout.Height(24f)))
            {
                AddSelectedMaterials();
            }

            if (GUILayout.Button("扫描文件夹材质", GUILayout.Height(24f)))
            {
                AddMaterialsFromFolder();
            }

            if (GUILayout.Button("清理空引用", GUILayout.Height(24f)))
            {
                RemoveNullMaterials();
            }

            if (GUILayout.Button("清空材质列表", GUILayout.Height(24f)))
            {
                m_TargetMaterials.Clear();
            }
            EditorGUILayout.EndHorizontal();

            m_MaterialFolder = (DefaultAsset)EditorGUILayout.ObjectField("材质文件夹", m_MaterialFolder, typeof(DefaultAsset), false);
            EditorGUILayout.Space(4f);
            m_MaterialsScrollPosition = EditorGUILayout.BeginScrollView(m_MaterialsScrollPosition, GUILayout.Height(170f));
            for (int i = 0; i < m_TargetMaterials.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                m_TargetMaterials[i] = (Material)EditorGUILayout.ObjectField("材质 " + (i + 1), m_TargetMaterials[i], typeof(Material), false);
                if (GUILayout.Button("移除", GUILayout.Width(60f)))
                {
                    m_TargetMaterials.RemoveAt(i);
                    i--;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private void DrawPropertyRuleSection()
        {
            EditorGUILayout.LabelField("属性规则列表", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("新增属性规则", GUILayout.Height(24f)))
            {
                m_PropertyRules.Add(new ShaderPropertyRule());
            }

            if (GUILayout.Button("清空规则", GUILayout.Height(24f)))
            {
                m_PropertyRules.Clear();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4f);
            m_RulesScrollPosition = EditorGUILayout.BeginScrollView(m_RulesScrollPosition, GUILayout.Height(220f));
            for (int i = 0; i < m_PropertyRules.Count; i++)
            {
                ShaderPropertyRule rule = m_PropertyRules[i];
                if (rule == null)
                {
                    continue;
                }

                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();
                rule.m_Enable = EditorGUILayout.ToggleLeft("启用规则", rule.m_Enable, GUILayout.Width(80f));
                rule.m_PropertyName = EditorGUILayout.TextField("Property Name", rule.m_PropertyName);
                if (GUILayout.Button("删除规则", GUILayout.Width(70f)))
                {
                    m_PropertyRules.RemoveAt(i);
                    i--;
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                    continue;
                }
                EditorGUILayout.EndHorizontal();

                rule.m_PropertyType = (ShaderPropertyType)EditorGUILayout.EnumPopup("属性类型", rule.m_PropertyType);
                DrawRuleValueField(rule);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.EndVertical();
        }

        private void DrawRuleValueField(ShaderPropertyRule rule)
        {
            switch (rule.m_PropertyType)
            {
                case ShaderPropertyType.Float:
                    rule.m_FloatValue = EditorGUILayout.FloatField("值", rule.m_FloatValue);
                    break;
                case ShaderPropertyType.Color:
                    rule.m_ColorValue = EditorGUILayout.ColorField("值", rule.m_ColorValue);
                    break;
                case ShaderPropertyType.Vector:
                    rule.m_VectorValue = EditorGUILayout.Vector4Field("值", rule.m_VectorValue);
                    break;
                case ShaderPropertyType.Texture:
                    rule.m_TextureValue = (Texture)EditorGUILayout.ObjectField("值", rule.m_TextureValue, typeof(Texture), false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void DrawApplyButtons()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("执行批量赋值", GUILayout.Height(32f)))
            {
                ApplyRulesToMaterials();
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 将当前 Selection 中的材质资源追加到目标列表，并自动去重。
        /// </summary>
        private void AddSelectedMaterials()
        {
            UnityEngine.Object[] selectedObjects = Selection.objects;
            for (int i = 0; i < selectedObjects.Length; i++)
            {
                Material selectedMaterial = selectedObjects[i] as Material;
                if (selectedMaterial == null || m_TargetMaterials.Contains(selectedMaterial))
                {
                    continue;
                }

                m_TargetMaterials.Add(selectedMaterial);
            }
        }

        /// <summary>
        /// 递归扫描指定文件夹下的所有材质资源，并自动追加到目标列表中。
        /// </summary>
        private void AddMaterialsFromFolder()
        {
            string folderPath = AssetDatabase.GetAssetPath(m_MaterialFolder);
            if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
            {
                EditorUtility.DisplayDialog("提示", "请先指定一个有效的材质文件夹。", "确定");
                return;
            }

            string[] materialGuids = AssetDatabase.FindAssets("t:Material", new[] { folderPath });
            int addedCount = 0;
            for (int i = 0; i < materialGuids.Length; i++)
            {
                string materialPath = AssetDatabase.GUIDToAssetPath(materialGuids[i]);
                Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null || m_TargetMaterials.Contains(material))
                {
                    continue;
                }

                m_TargetMaterials.Add(material);
                addedCount++;
            }

            EditorUtility.DisplayDialog("扫描完成", "新增材质数量: " + addedCount, "确定");
        }

        /// <summary>
        /// 删除目标材质列表中的空引用，避免执行时出现无效对象。
        /// </summary>
        private void RemoveNullMaterials()
        {
            for (int i = m_TargetMaterials.Count - 1; i >= 0; i--)
            {
                if (m_TargetMaterials[i] == null)
                {
                    m_TargetMaterials.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 执行批量赋值：
        /// 1) 遍历目标材质；
        /// 2) 逐条应用启用状态的属性规则；
        /// 3) 仅当材质含有对应属性名时才设置值。
        /// </summary>
        private void ApplyRulesToMaterials()
        {
            RemoveNullMaterials();

            if (m_TargetMaterials.Count == 0)
            {
                EditorUtility.DisplayDialog("提示", "请先添加至少一个目标材质。", "确定");
                return;
            }

            if (m_PropertyRules.Count == 0)
            {
                EditorUtility.DisplayDialog("提示", "请先添加至少一条属性规则。", "确定");
                return;
            }

            int changedMaterialCount = 0;
            int appliedPropertyCount = 0;
            int skippedMissingPropertyCount = 0;

            for (int i = 0; i < m_TargetMaterials.Count; i++)
            {
                Material material = m_TargetMaterials[i];
                if (material == null)
                {
                    continue;
                }

                bool materialChanged = false;
                Undo.RecordObject(material, "Batch Set Material Shader Properties");

                for (int j = 0; j < m_PropertyRules.Count; j++)
                {
                    ShaderPropertyRule rule = m_PropertyRules[j];
                    if (rule == null || !rule.m_Enable || string.IsNullOrWhiteSpace(rule.m_PropertyName))
                    {
                        continue;
                    }

                    string propertyName = rule.m_PropertyName.Trim();
                    if (!material.HasProperty(propertyName))
                    {
                        skippedMissingPropertyCount++;
                        continue;
                    }

                    ApplySingleRule(material, propertyName, rule);
                    appliedPropertyCount++;
                    materialChanged = true;
                }

                if (!materialChanged)
                {
                    continue;
                }

                EditorUtility.SetDirty(material);
                changedMaterialCount++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "执行完成",
                "已修改材质数量: " + changedMaterialCount +
                "\n成功设置属性次数: " + appliedPropertyCount +
                "\n因属性不存在而跳过次数: " + skippedMissingPropertyCount,
                "确定");
        }

        /// <summary>
        /// 将单条规则写入指定材质。
        /// </summary>
        private void ApplySingleRule(Material material, string propertyName, ShaderPropertyRule rule)
        {
            switch (rule.m_PropertyType)
            {
                case ShaderPropertyType.Float:
                    material.SetFloat(propertyName, rule.m_FloatValue);
                    break;
                case ShaderPropertyType.Color:
                    material.SetColor(propertyName, rule.m_ColorValue);
                    break;
                case ShaderPropertyType.Vector:
                    material.SetVector(propertyName, rule.m_VectorValue);
                    break;
                case ShaderPropertyType.Texture:
                    material.SetTexture(propertyName, rule.m_TextureValue);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
