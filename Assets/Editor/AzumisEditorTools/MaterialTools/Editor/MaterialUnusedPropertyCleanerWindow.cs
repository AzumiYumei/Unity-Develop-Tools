// ================================================================
// 材质无效属性清理工具
//
// 文件名称: MaterialUnusedPropertyCleanerWindow.cs
// 版本: 1.3
//
// 描述:
// 该工具用于扫描并清理 Material 资源中已经不再被当前 Shader 使用的旧属性。
// 与普通“列出全部失效属性”不同，本工具只关注“仍然保留残留值或资源引用”的失效属性，
// 从而帮助定位真正可能继续占用资源、影响依赖关系或增加包体的材质问题。
//
// 使用方式:
// 1. 打开菜单 Tools/Azumi/材质工具/材质无效属性清理。
// 2. 可选指定一个目标材质；若指定，则只处理该材质。
// 3. 若未指定目标材质，可选指定一个目标目录；若不指定，则优先处理当前 Project 视图选中的材质和文件夹。
// 4. 若既未指定材质、也未指定目录、且当前没有选中资源，则默认扫描整个 Assets 目录。
// 5. 可按类型限制检查范围，例如仅检查 Texture 残留。
// 6. 点击“扫描无效属性”查看结果。
// 7. 可对单个材质执行单独清理，也可执行批量清理。
//
// 注意事项:
// 1. 清理会直接修改 .mat 资源，建议先提交版本控制后再批量执行。
// 2. 工具只清理“当前 Shader 已不存在，且仍保留残留值或资源引用”的属性。
// 3. 针对纹理属性，窗口会额外显示可点击对象字段和残留资源路径，便于快速定位引用来源。
// 4. 若未来重新把旧属性加回 Shader，被删除的旧属性值不会自动恢复。
//
// 修改历史:
// 2026-05-29 - 初始版本
// 2026-05-29 - 调整为仅显示存在残留值或资源引用的失效属性，支持单材质处理，并显示纹理资源路径
// 2026-05-29 - 为纹理残留项增加可点击对象字段，点击后直接定位到对应资源
// 2026-05-29 - 增加属性类型筛选，可限制只检查指定类型的残留属性
// ================================================================

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AzumisEditorTools
{
    public class MaterialUnusedPropertyCleanerWindow : EditorWindow
    {
        private enum InvalidPropertyValueKind
        {
            Texture,
            Int,
            Float,
            Color
        }

        private sealed class InvalidMaterialPropertyInfo
        {
            public string m_PropertyName = string.Empty;
            public string m_PropertyGroup = string.Empty;
            public string m_ValueSummary = string.Empty;
            public string m_ReferencedAssetPath = string.Empty;
            public Texture m_ReferencedTexture;
            public InvalidPropertyValueKind m_ValueKind;
        }

        private sealed class MaterialScanResult
        {
            public Material m_Material;
            public string m_AssetPath = string.Empty;
            public string m_ShaderName = string.Empty;
            public bool m_IsExpanded = true;
            public readonly List<InvalidMaterialPropertyInfo> m_InvalidProperties = new List<InvalidMaterialPropertyInfo>();
        }

        private const string m_WindowTitle = "材质无效属性清理";
        private const float m_FloatEpsilon = 0.0001f;

        private Material m_TargetMaterial;
        private DefaultAsset m_TargetFolder;
        private Vector2 m_ScrollPosition;
        private readonly List<MaterialScanResult> m_ScanResults = new List<MaterialScanResult>();
        private bool m_IsCheckTexture = true;
        private bool m_IsCheckInt = true;
        private bool m_IsCheckFloat = true;
        private bool m_IsCheckColor = true;
        private int m_LastScannedMaterialCount;
        private int m_LastInvalidMaterialCount;
        private int m_LastInvalidPropertyCount;

        [MenuItem("Tools/Azumi/材质工具/材质无效属性清理")]
        public static void ShowWindow()
        {
            MaterialUnusedPropertyCleanerWindow window = GetWindow<MaterialUnusedPropertyCleanerWindow>(m_WindowTitle);
            window.minSize = new Vector2(720f, 520f);
            window.Show();
        }

        private void OnGUI()
        {
            DrawToolbar();
            EditorGUILayout.Space(8f);
            DrawSummary();
            EditorGUILayout.Space(8f);
            DrawResultList();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.LabelField("扫描范围", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            m_TargetMaterial = (Material)EditorGUILayout.ObjectField("目标材质", m_TargetMaterial, typeof(Material), false);
            m_TargetFolder = (DefaultAsset)EditorGUILayout.ObjectField("目标目录", m_TargetFolder, typeof(DefaultAsset), false);
            EditorGUILayout.HelpBox(
                "指定目标材质时仅处理该材质。未指定材质时，若指定目录则仅扫描该目录；否则优先使用当前 Project 视图选中的材质和文件夹；若当前没有选中资源，则扫描整个 Assets 目录。",
                MessageType.Info);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("检查类型", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            m_IsCheckTexture = EditorGUILayout.ToggleLeft("Texture", m_IsCheckTexture, GUILayout.Width(90f));
            m_IsCheckInt = EditorGUILayout.ToggleLeft("Int", m_IsCheckInt, GUILayout.Width(60f));
            m_IsCheckFloat = EditorGUILayout.ToggleLeft("Float", m_IsCheckFloat, GUILayout.Width(70f));
            m_IsCheckColor = EditorGUILayout.ToggleLeft("Color", m_IsCheckColor, GUILayout.Width(70f));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("扫描无效属性", GUILayout.Height(28f)))
            {
                ScanMaterials();
            }

            using (new EditorGUI.DisabledScope(m_ScanResults.Count == 0))
            {
                if (GUILayout.Button("清理无效属性", GUILayout.Height(28f)))
                {
                    CleanupMaterials();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawSummary()
        {
            EditorGUILayout.LabelField("扫描结果", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("扫描材质数", m_LastScannedMaterialCount.ToString());
            EditorGUILayout.LabelField("存在无效属性的材质数", m_LastInvalidMaterialCount.ToString());
            EditorGUILayout.LabelField("无效属性总数", m_LastInvalidPropertyCount.ToString());
            EditorGUILayout.EndVertical();
        }

        private void DrawResultList()
        {
            EditorGUILayout.LabelField("详情列表", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (m_ScanResults.Count == 0)
            {
                EditorGUILayout.HelpBox("当前没有可显示的扫描结果。请先执行一次扫描。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);
            for (int i = 0; i < m_ScanResults.Count; i++)
            {
                DrawScanResult(m_ScanResults[i], i);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawScanResult(MaterialScanResult result, int index)
        {
            EditorGUILayout.BeginVertical("box");
            result.m_IsExpanded = EditorGUILayout.Foldout(
                result.m_IsExpanded,
                "材质 " + (index + 1) + " - " + result.m_Material.name + " (" + result.m_InvalidProperties.Count + " 个无效属性)",
                true);

            if (!result.m_IsExpanded)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("材质", result.m_Material, typeof(Material), false);
            }

            EditorGUILayout.TextField("路径", result.m_AssetPath);
            EditorGUILayout.TextField("Shader", result.m_ShaderName);

            for (int i = 0; i < result.m_InvalidProperties.Count; i++)
            {
                InvalidMaterialPropertyInfo propertyInfo = result.m_InvalidProperties[i];
                EditorGUILayout.LabelField(propertyInfo.m_PropertyGroup, propertyInfo.m_PropertyName);
                if (!string.IsNullOrEmpty(propertyInfo.m_ValueSummary))
                {
                    EditorGUILayout.TextField("残留值", propertyInfo.m_ValueSummary);
                }

                if (propertyInfo.m_ValueKind == InvalidPropertyValueKind.Texture && propertyInfo.m_ReferencedTexture != null)
                {
                    DrawPingableTextureField(propertyInfo.m_ReferencedTexture);
                }

                if (propertyInfo.m_ValueKind == InvalidPropertyValueKind.Texture && !string.IsNullOrEmpty(propertyInfo.m_ReferencedAssetPath))
                {
                    EditorGUILayout.TextField("资源路径", propertyInfo.m_ReferencedAssetPath);
                }
            }

            EditorGUILayout.Space(4f);
            if (GUILayout.Button("仅清理该材质", GUILayout.Height(24f)))
            {
                CleanupSingleResult(result);
            }

            EditorGUILayout.EndVertical();
        }

        private void ScanMaterials()
        {
            m_ScanResults.Clear();
            m_LastScannedMaterialCount = 0;
            m_LastInvalidMaterialCount = 0;
            m_LastInvalidPropertyCount = 0;

            List<Material> materials = CollectTargetMaterials();
            m_LastScannedMaterialCount = materials.Count;

            for (int i = 0; i < materials.Count; i++)
            {
                Material material = materials[i];
                MaterialScanResult result = CreateScanResult(material);
                if (result == null)
                {
                    continue;
                }

                m_ScanResults.Add(result);
                m_LastInvalidMaterialCount++;
                m_LastInvalidPropertyCount += result.m_InvalidProperties.Count;
            }

            Debug.LogFormat(
                "[MaterialUnusedPropertyCleaner] 扫描完成。扫描材质数: {0}，存在无效属性的材质数: {1}，无效属性总数: {2}",
                m_LastScannedMaterialCount,
                m_LastInvalidMaterialCount,
                m_LastInvalidPropertyCount);
        }

        private void CleanupMaterials()
        {
            if (m_ScanResults.Count == 0)
            {
                EditorUtility.DisplayDialog("提示", "当前没有可清理的扫描结果，请先执行扫描。", "确定");
                return;
            }

            bool isConfirmed = EditorUtility.DisplayDialog(
                "确认清理",
                "即将清理当前扫描结果中的无效材质属性，此操作会直接修改 .mat 资源，是否继续？",
                "继续",
                "取消");
            if (!isConfirmed)
            {
                return;
            }

            int cleanedMaterialCount = 0;
            int removedPropertyCount = 0;

            try
            {
                AssetDatabase.StartAssetEditing();

                for (int i = 0; i < m_ScanResults.Count; i++)
                {
                    Material material = m_ScanResults[i].m_Material;
                    int currentRemovedCount = CleanupMaterial(material);
                    if (currentRemovedCount <= 0)
                    {
                        continue;
                    }

                    EditorUtility.SetDirty(material);
                    cleanedMaterialCount++;
                    removedPropertyCount += currentRemovedCount;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.LogFormat(
                "[MaterialUnusedPropertyCleaner] 清理完成。已清理材质数: {0}，移除无效属性总数: {1}",
                cleanedMaterialCount,
                removedPropertyCount);

            ScanMaterials();
            EditorUtility.DisplayDialog(
                "完成",
                "材质无效属性清理完成。\n已清理材质数: " + cleanedMaterialCount + "\n移除无效属性总数: " + removedPropertyCount,
                "确定");
        }

        private void CleanupSingleResult(MaterialScanResult result)
        {
            if (result == null || result.m_Material == null)
            {
                return;
            }

            bool isConfirmed = EditorUtility.DisplayDialog(
                "确认清理",
                "即将仅清理当前材质中的无效残留属性，是否继续？",
                "继续",
                "取消");
            if (!isConfirmed)
            {
                return;
            }

            int removedPropertyCount = CleanupMaterial(result.m_Material);
            if (removedPropertyCount > 0)
            {
                EditorUtility.SetDirty(result.m_Material);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            Debug.LogFormat(
                "[MaterialUnusedPropertyCleaner] 单材质清理完成。材质: {0}，移除无效属性数: {1}",
                AssetDatabase.GetAssetPath(result.m_Material),
                removedPropertyCount);

            ScanMaterials();
            EditorUtility.DisplayDialog(
                "完成",
                "单材质清理完成。\n移除无效属性数: " + removedPropertyCount,
                "确定");
        }

        private List<Material> CollectTargetMaterials()
        {
            HashSet<Material> materialSet = new HashSet<Material>();
            if (m_TargetMaterial != null)
            {
                materialSet.Add(m_TargetMaterial);
                return new List<Material>(materialSet);
            }

            string targetFolderPath = AssetDatabase.GetAssetPath(m_TargetFolder);

            if (!string.IsNullOrEmpty(targetFolderPath) && AssetDatabase.IsValidFolder(targetFolderPath))
            {
                CollectMaterialsFromFolders(new[] { targetFolderPath }, materialSet);
                return new List<Material>(materialSet);
            }

            string[] selectedAssetGuids = Selection.assetGUIDs;
            if (selectedAssetGuids != null && selectedAssetGuids.Length > 0)
            {
                List<string> selectedFolders = new List<string>();
                for (int i = 0; i < selectedAssetGuids.Length; i++)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(selectedAssetGuids[i]);
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        continue;
                    }

                    if (AssetDatabase.IsValidFolder(assetPath))
                    {
                        selectedFolders.Add(assetPath);
                        continue;
                    }

                    Material material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                    if (material != null)
                    {
                        materialSet.Add(material);
                    }
                }

                if (selectedFolders.Count > 0)
                {
                    CollectMaterialsFromFolders(selectedFolders.ToArray(), materialSet);
                }

                if (materialSet.Count > 0)
                {
                    return new List<Material>(materialSet);
                }
            }

            CollectMaterialsFromFolders(new[] { "Assets" }, materialSet);
            return new List<Material>(materialSet);
        }

        private void CollectMaterialsFromFolders(string[] folderPaths, HashSet<Material> materialSet)
        {
            if (folderPaths == null || materialSet == null)
            {
                return;
            }

            string[] materialGuids = AssetDatabase.FindAssets("t:Material", folderPaths);
            for (int i = 0; i < materialGuids.Length; i++)
            {
                string materialPath = AssetDatabase.GUIDToAssetPath(materialGuids[i]);
                Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material != null)
                {
                    materialSet.Add(material);
                }
            }
        }

        private MaterialScanResult CreateScanResult(Material material)
        {
            if (material == null || material.shader == null)
            {
                return null;
            }

            SerializedObject serializedObject = new SerializedObject(material);
            SerializedProperty savedProperties = serializedObject.FindProperty("m_SavedProperties");
            if (savedProperties == null)
            {
                return null;
            }

            MaterialScanResult result = new MaterialScanResult();
            result.m_Material = material;
            result.m_AssetPath = AssetDatabase.GetAssetPath(material);
            result.m_ShaderName = material.shader.name;

            if (m_IsCheckTexture)
            {
                CollectInvalidTextureProperties(material, savedProperties.FindPropertyRelative("m_TexEnvs"), result.m_InvalidProperties);
            }

            if (m_IsCheckInt)
            {
                CollectInvalidIntProperties(material, savedProperties.FindPropertyRelative("m_Ints"), result.m_InvalidProperties);
            }

            if (m_IsCheckFloat)
            {
                CollectInvalidFloatProperties(material, savedProperties.FindPropertyRelative("m_Floats"), result.m_InvalidProperties);
            }

            if (m_IsCheckColor)
            {
                CollectInvalidColorProperties(material, savedProperties.FindPropertyRelative("m_Colors"), result.m_InvalidProperties);
            }

            if (result.m_InvalidProperties.Count == 0)
            {
                return null;
            }

            return result;
        }

        private void CollectInvalidTextureProperties(
            Material material,
            SerializedProperty propertyArray,
            List<InvalidMaterialPropertyInfo> invalidProperties)
        {
            if (material == null || propertyArray == null || !propertyArray.isArray || invalidProperties == null)
            {
                return;
            }

            for (int i = 0; i < propertyArray.arraySize; i++)
            {
                SerializedProperty element = propertyArray.GetArrayElementAtIndex(i);
                if (element == null)
                {
                    continue;
                }

                SerializedProperty nameProperty = element.FindPropertyRelative("first");
                SerializedProperty textureProperty = element.FindPropertyRelative("second.m_Texture");
                if (nameProperty == null || textureProperty == null)
                {
                    continue;
                }

                string propertyName = nameProperty.stringValue;
                if (material.HasProperty(propertyName))
                {
                    continue;
                }

                Texture referencedTexture = textureProperty.objectReferenceValue as Texture;
                if (referencedTexture == null)
                {
                    continue;
                }

                InvalidMaterialPropertyInfo propertyInfo = new InvalidMaterialPropertyInfo();
                propertyInfo.m_PropertyName = propertyName;
                propertyInfo.m_PropertyGroup = "Texture";
                propertyInfo.m_ValueKind = InvalidPropertyValueKind.Texture;
                propertyInfo.m_ValueSummary = referencedTexture.name;
                propertyInfo.m_ReferencedTexture = referencedTexture;
                propertyInfo.m_ReferencedAssetPath = AssetDatabase.GetAssetPath(referencedTexture);
                invalidProperties.Add(propertyInfo);
            }
        }

        private void DrawPingableTextureField(Texture referencedTexture)
        {
            if (referencedTexture == null)
            {
                return;
            }

            Rect fieldRect = EditorGUILayout.GetControlRect();
            EditorGUI.BeginChangeCheck();
            Texture displayedTexture = (Texture)EditorGUI.ObjectField(
                fieldRect,
                "纹理资源",
                referencedTexture,
                typeof(Texture),
                false);

            if (EditorGUI.EndChangeCheck())
            {
                if (displayedTexture != null)
                {
                    Selection.activeObject = displayedTexture;
                    EditorGUIUtility.PingObject(displayedTexture);
                }
                else
                {
                    Selection.activeObject = referencedTexture;
                    EditorGUIUtility.PingObject(referencedTexture);
                }
            }

            Event currentEvent = Event.current;
            if (currentEvent == null)
            {
                return;
            }

            if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0 && fieldRect.Contains(currentEvent.mousePosition))
            {
                Selection.activeObject = referencedTexture;
                EditorGUIUtility.PingObject(referencedTexture);
            }
        }

        private void CollectInvalidIntProperties(
            Material material,
            SerializedProperty propertyArray,
            List<InvalidMaterialPropertyInfo> invalidProperties)
        {
            if (material == null || propertyArray == null || !propertyArray.isArray || invalidProperties == null)
            {
                return;
            }

            for (int i = 0; i < propertyArray.arraySize; i++)
            {
                SerializedProperty element = propertyArray.GetArrayElementAtIndex(i);
                if (element == null)
                {
                    continue;
                }

                SerializedProperty nameProperty = element.FindPropertyRelative("first");
                SerializedProperty valueProperty = element.FindPropertyRelative("second");
                if (nameProperty == null || valueProperty == null)
                {
                    continue;
                }

                string propertyName = nameProperty.stringValue;
                if (material.HasProperty(propertyName))
                {
                    continue;
                }

                if (valueProperty.intValue == 0)
                {
                    continue;
                }

                InvalidMaterialPropertyInfo propertyInfo = new InvalidMaterialPropertyInfo();
                propertyInfo.m_PropertyName = propertyName;
                propertyInfo.m_PropertyGroup = "Int";
                propertyInfo.m_ValueKind = InvalidPropertyValueKind.Int;
                propertyInfo.m_ValueSummary = valueProperty.intValue.ToString();
                invalidProperties.Add(propertyInfo);
            }
        }

        private void CollectInvalidFloatProperties(
            Material material,
            SerializedProperty propertyArray,
            List<InvalidMaterialPropertyInfo> invalidProperties)
        {
            if (material == null || propertyArray == null || !propertyArray.isArray || invalidProperties == null)
            {
                return;
            }

            for (int i = 0; i < propertyArray.arraySize; i++)
            {
                SerializedProperty element = propertyArray.GetArrayElementAtIndex(i);
                if (element == null)
                {
                    continue;
                }

                SerializedProperty nameProperty = element.FindPropertyRelative("first");
                SerializedProperty valueProperty = element.FindPropertyRelative("second");
                if (nameProperty == null || valueProperty == null)
                {
                    continue;
                }

                string propertyName = nameProperty.stringValue;
                if (material.HasProperty(propertyName))
                {
                    continue;
                }

                if (Mathf.Abs(valueProperty.floatValue) <= m_FloatEpsilon)
                {
                    continue;
                }

                InvalidMaterialPropertyInfo propertyInfo = new InvalidMaterialPropertyInfo();
                propertyInfo.m_PropertyName = propertyName;
                propertyInfo.m_PropertyGroup = "Float";
                propertyInfo.m_ValueKind = InvalidPropertyValueKind.Float;
                propertyInfo.m_ValueSummary = valueProperty.floatValue.ToString("0.####");
                invalidProperties.Add(propertyInfo);
            }
        }

        private void CollectInvalidColorProperties(
            Material material,
            SerializedProperty propertyArray,
            List<InvalidMaterialPropertyInfo> invalidProperties)
        {
            if (material == null || propertyArray == null || !propertyArray.isArray || invalidProperties == null)
            {
                return;
            }

            for (int i = 0; i < propertyArray.arraySize; i++)
            {
                SerializedProperty element = propertyArray.GetArrayElementAtIndex(i);
                if (element == null)
                {
                    continue;
                }

                SerializedProperty nameProperty = element.FindPropertyRelative("first");
                SerializedProperty valueProperty = element.FindPropertyRelative("second");
                if (nameProperty == null || valueProperty == null)
                {
                    continue;
                }

                string propertyName = nameProperty.stringValue;
                if (material.HasProperty(propertyName))
                {
                    continue;
                }

                Color colorValue = valueProperty.colorValue;
                if (IsApproximatelyZeroColor(colorValue))
                {
                    continue;
                }

                InvalidMaterialPropertyInfo propertyInfo = new InvalidMaterialPropertyInfo();
                propertyInfo.m_PropertyName = propertyName;
                propertyInfo.m_PropertyGroup = "Color";
                propertyInfo.m_ValueKind = InvalidPropertyValueKind.Color;
                propertyInfo.m_ValueSummary = string.Format(
                    "RGBA({0:0.###}, {1:0.###}, {2:0.###}, {3:0.###})",
                    colorValue.r,
                    colorValue.g,
                    colorValue.b,
                    colorValue.a);
                invalidProperties.Add(propertyInfo);
            }
        }

        private int CleanupMaterial(Material material)
        {
            if (material == null || material.shader == null)
            {
                return 0;
            }

            SerializedObject serializedObject = new SerializedObject(material);
            SerializedProperty savedProperties = serializedObject.FindProperty("m_SavedProperties");
            if (savedProperties == null)
            {
                return 0;
            }

            int removedCount = 0;
            if (m_IsCheckTexture)
            {
                removedCount += RemoveInvalidTexturePropertiesWithReference(material, savedProperties.FindPropertyRelative("m_TexEnvs"));
            }

            if (m_IsCheckInt)
            {
                removedCount += RemoveInvalidIntPropertiesWithValue(material, savedProperties.FindPropertyRelative("m_Ints"));
            }

            if (m_IsCheckFloat)
            {
                removedCount += RemoveInvalidFloatPropertiesWithValue(material, savedProperties.FindPropertyRelative("m_Floats"));
            }

            if (m_IsCheckColor)
            {
                removedCount += RemoveInvalidColorPropertiesWithValue(material, savedProperties.FindPropertyRelative("m_Colors"));
            }

            if (removedCount > 0)
            {
                serializedObject.ApplyModifiedProperties();
            }

            return removedCount;
        }

        private int RemoveInvalidTexturePropertiesWithReference(Material material, SerializedProperty propertyArray)
        {
            if (material == null || propertyArray == null || !propertyArray.isArray)
            {
                return 0;
            }

            int removedCount = 0;
            for (int i = propertyArray.arraySize - 1; i >= 0; i--)
            {
                SerializedProperty element = propertyArray.GetArrayElementAtIndex(i);
                if (element == null)
                {
                    continue;
                }

                SerializedProperty nameProperty = element.FindPropertyRelative("first");
                SerializedProperty textureProperty = element.FindPropertyRelative("second.m_Texture");
                if (nameProperty == null || textureProperty == null)
                {
                    continue;
                }

                string propertyName = nameProperty.stringValue;
                if (material.HasProperty(propertyName) || textureProperty.objectReferenceValue == null)
                {
                    continue;
                }

                propertyArray.DeleteArrayElementAtIndex(i);
                removedCount++;
            }

            return removedCount;
        }

        private int RemoveInvalidIntPropertiesWithValue(Material material, SerializedProperty propertyArray)
        {
            if (material == null || propertyArray == null || !propertyArray.isArray)
            {
                return 0;
            }

            int removedCount = 0;
            for (int i = propertyArray.arraySize - 1; i >= 0; i--)
            {
                SerializedProperty element = propertyArray.GetArrayElementAtIndex(i);
                if (element == null)
                {
                    continue;
                }

                SerializedProperty nameProperty = element.FindPropertyRelative("first");
                SerializedProperty valueProperty = element.FindPropertyRelative("second");
                if (nameProperty == null || valueProperty == null)
                {
                    continue;
                }

                string propertyName = nameProperty.stringValue;
                if (material.HasProperty(propertyName) || valueProperty.intValue == 0)
                {
                    continue;
                }

                propertyArray.DeleteArrayElementAtIndex(i);
                removedCount++;
            }

            return removedCount;
        }

        private int RemoveInvalidFloatPropertiesWithValue(Material material, SerializedProperty propertyArray)
        {
            if (material == null || propertyArray == null || !propertyArray.isArray)
            {
                return 0;
            }

            int removedCount = 0;
            for (int i = propertyArray.arraySize - 1; i >= 0; i--)
            {
                SerializedProperty element = propertyArray.GetArrayElementAtIndex(i);
                if (element == null)
                {
                    continue;
                }

                SerializedProperty nameProperty = element.FindPropertyRelative("first");
                SerializedProperty valueProperty = element.FindPropertyRelative("second");
                if (nameProperty == null || valueProperty == null)
                {
                    continue;
                }

                string propertyName = nameProperty.stringValue;
                if (material.HasProperty(propertyName) || Mathf.Abs(valueProperty.floatValue) <= m_FloatEpsilon)
                {
                    continue;
                }

                propertyArray.DeleteArrayElementAtIndex(i);
                removedCount++;
            }

            return removedCount;
        }

        private int RemoveInvalidColorPropertiesWithValue(Material material, SerializedProperty propertyArray)
        {
            if (material == null || propertyArray == null || !propertyArray.isArray)
            {
                return 0;
            }

            int removedCount = 0;
            for (int i = propertyArray.arraySize - 1; i >= 0; i--)
            {
                SerializedProperty element = propertyArray.GetArrayElementAtIndex(i);
                if (element == null)
                {
                    continue;
                }

                SerializedProperty nameProperty = element.FindPropertyRelative("first");
                SerializedProperty valueProperty = element.FindPropertyRelative("second");
                if (nameProperty == null || valueProperty == null)
                {
                    continue;
                }

                string propertyName = nameProperty.stringValue;
                if (material.HasProperty(propertyName) || IsApproximatelyZeroColor(valueProperty.colorValue))
                {
                    continue;
                }

                propertyArray.DeleteArrayElementAtIndex(i);
                removedCount++;
            }

            return removedCount;
        }

        private static bool IsApproximatelyZeroColor(Color colorValue)
        {
            return Mathf.Abs(colorValue.r) <= m_FloatEpsilon &&
                   Mathf.Abs(colorValue.g) <= m_FloatEpsilon &&
                   Mathf.Abs(colorValue.b) <= m_FloatEpsilon &&
                   Mathf.Abs(colorValue.a) <= m_FloatEpsilon;
        }
    }
}
