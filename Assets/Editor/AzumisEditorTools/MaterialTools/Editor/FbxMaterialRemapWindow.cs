using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AzumisEditorTools
{
    public class FbxMaterialRemapWindow : EditorWindow
    {
        private enum MaterialSearchMode
        {
            Global,
            Folder
        }

        private sealed class MaterialCandidate
        {
            public Material m_Material;
            public string m_Name = string.Empty;
            public string m_NormalizedName = string.Empty;
            public string m_Path = string.Empty;
            public string[] m_Tokens = Array.Empty<string>();
        }

        private sealed class FbxMaterialEntry
        {
            public AssetImporter.SourceAssetIdentifier m_SourceIdentifier;
            public Material m_SourceMaterial;
            public string m_SourceName = string.Empty;
            public Material m_TargetMaterial;
            public int m_CandidateCount;
            public string m_CandidatePreviewPath = string.Empty;
            public bool m_IsExpanded = true;
        }

        private sealed class PrefabMaterialEntry
        {
            public Material m_SourceMaterial;
            public Material m_TargetMaterial;
            public int m_UsageCount;
            public bool m_IsExpanded;
            public readonly List<PrefabRendererUsageEntry> m_RendererUsages = new List<PrefabRendererUsageEntry>();
        }

        private sealed class PrefabRendererUsageEntry
        {
            public string m_DisplayLabel = string.Empty;
            public string m_RelativeRendererPath = string.Empty;
        }

        [Serializable]
        private sealed class MaterialTargetState
        {
            public string m_SourceKey = string.Empty;
            public string m_TargetPath = string.Empty;
            public string m_TargetName = string.Empty;
        }

        [Serializable]
        private sealed class WindowState
        {
            public string m_FbxAssetPath = string.Empty;
            public int m_MaterialSearchMode;
            public string m_SearchFolderPath = string.Empty;
            public string m_PrefabAssetPath = string.Empty;
            public string m_PrefabSearchFolderPath = string.Empty;
            public bool m_IsFbxResultsExpanded;
            public bool m_IsPrefabResultsExpanded;
            public Vector2 m_ScrollPosition;
            public List<MaterialTargetState> m_FbxTargets = new List<MaterialTargetState>();
            public List<MaterialTargetState> m_PrefabTargets = new List<MaterialTargetState>();
        }

        private const string m_WindowStateEditorPrefsKey = "AzumisEditorTools.FbxMaterialRemapWindow.State";
        private const string m_WindowTitle = "FBX 材质映射";

        private GameObject m_FbxAsset;
        private string m_LockedFbxAssetPath = string.Empty;
        private MaterialSearchMode m_MaterialSearchMode = MaterialSearchMode.Global;
        private DefaultAsset m_SearchFolder;
        private readonly List<FbxMaterialEntry> m_FbxMaterialEntries = new List<FbxMaterialEntry>();

        private GameObject m_PrefabAsset;
        private string m_PrefabAssetPath = string.Empty;
        private readonly List<PrefabMaterialEntry> m_PrefabMaterialEntries = new List<PrefabMaterialEntry>();
        private DefaultAsset m_PrefabSearchFolder;
        private GameObject m_PrefabSceneInstanceRoot;

        private Vector2 m_ScrollPosition;
        private bool m_IsFbxResultsExpanded = false;
        private bool m_IsPrefabResultsExpanded = false;
        private WindowState m_LoadedWindowState;
        private bool m_IsRestoringWindowState;

        [MenuItem("Tools/Azumi/模型材质/FBX Remapped Materials 工具")]
        public static void ShowWindow()
        {
            FbxMaterialRemapWindow window = GetWindow<FbxMaterialRemapWindow>(m_WindowTitle);
            window.minSize = new Vector2(620f, 720f);
            window.Show();
        }

        private void OnEnable()
        {
            LoadWindowState();
        }

        private void OnDisable()
        {
            SaveWindowState();
        }

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);
            DrawFbxRemapSection();
            EditorGUILayout.Space(12f);
            DrawPrefabReplaceSection();
            EditorGUILayout.EndScrollView();
            if (EditorGUI.EndChangeCheck())
            {
                SaveWindowState();
            }
        }

        private void DrawFbxRemapSection()
        {
            EditorGUILayout.LabelField("FBX Remapped Materials", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            EditorGUI.BeginChangeCheck();
            m_FbxAsset = (GameObject)EditorGUILayout.ObjectField("目标 FBX", m_FbxAsset, typeof(GameObject), false);
            if (EditorGUI.EndChangeCheck())
            {
                LockFbxAsset(m_FbxAsset);
            }

            using (new EditorGUI.DisabledScope(m_FbxAsset == null))
            {
                if (GUILayout.Button("重新锁定并读取材质"))
                {
                    LockFbxAsset(m_FbxAsset);
                }
            }

            if (string.IsNullOrEmpty(m_LockedFbxAssetPath))
            {
                EditorGUILayout.HelpBox("请选择一个 FBX 资源。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.LabelField("已锁定路径", m_LockedFbxAssetPath);
            m_MaterialSearchMode = (MaterialSearchMode)EditorGUILayout.EnumPopup("查找模式", m_MaterialSearchMode);
            if (m_MaterialSearchMode == MaterialSearchMode.Folder)
            {
                m_SearchFolder = (DefaultAsset)EditorGUILayout.ObjectField("材质查找目录", m_SearchFolder, typeof(DefaultAsset), false);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("按名称查找材质"))
            {
                AutoMatchFbxMaterials();
                SaveWindowState();
            }

            using (new EditorGUI.DisabledScope(m_FbxMaterialEntries.Count == 0))
            {
                if (GUILayout.Button("应用 Remapped Materials"))
                {
                    ApplyFbxMaterialRemaps();
                    SaveWindowState();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6f);
            if (m_FbxMaterialEntries.Count == 0)
            {
                EditorGUILayout.HelpBox("当前 FBX 未读取到可映射材质。", MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            m_IsFbxResultsExpanded = EditorGUILayout.Foldout(m_IsFbxResultsExpanded, "材质结果列表 (" + m_FbxMaterialEntries.Count + ")", true);
            if (!m_IsFbxResultsExpanded)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < m_FbxMaterialEntries.Count; i++)
            {
                DrawFbxMaterialEntry(m_FbxMaterialEntries[i], i);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawFbxMaterialEntry(FbxMaterialEntry entry, int index)
        {
            EditorGUILayout.BeginVertical("box");
            entry.m_IsExpanded = EditorGUILayout.Foldout(entry.m_IsExpanded, "材质 " + (index + 1), true);
            if (!entry.m_IsExpanded)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                if (entry.m_SourceMaterial != null)
                {
                    EditorGUILayout.ObjectField("源材质 " + (index + 1), entry.m_SourceMaterial, typeof(Material), false);
                }
                else
                {
                    EditorGUILayout.TextField("源材质 " + (index + 1), entry.m_SourceName);
                }
            }

            entry.m_TargetMaterial = (Material)EditorGUILayout.ObjectField("目标材质", entry.m_TargetMaterial, typeof(Material), false);
            EditorGUILayout.LabelField("匹配结果", entry.m_CandidateCount > 0 ? "找到 " + entry.m_CandidateCount + " 个候选" : "未找到候选");
            if (!string.IsNullOrEmpty(entry.m_CandidatePreviewPath))
            {
                EditorGUILayout.SelectableLabel(entry.m_CandidatePreviewPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawPrefabReplaceSection()
        {
            EditorGUILayout.LabelField("预制件材质替换", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            EditorGUI.BeginChangeCheck();
            m_PrefabAsset = (GameObject)EditorGUILayout.ObjectField("目标预制件", m_PrefabAsset, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
            {
                ScanPrefabMaterials(m_PrefabAsset);
            }

            using (new EditorGUI.DisabledScope(m_PrefabAsset == null))
            {
                if (GUILayout.Button("重新扫描预制件材质"))
                {
                    ScanPrefabMaterials(m_PrefabAsset);
                }
            }

            if (m_PrefabAsset == null)
            {
                EditorGUILayout.HelpBox("请选择一个预制件资源。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            if (!string.IsNullOrEmpty(m_PrefabAssetPath))
            {
                EditorGUILayout.LabelField("预制件路径", m_PrefabAssetPath);
            }
            else
            {
                EditorGUILayout.LabelField("目标类型", "场景对象");
            }

            m_PrefabSearchFolder = (DefaultAsset)EditorGUILayout.ObjectField("材质查找目录", m_PrefabSearchFolder, typeof(DefaultAsset), false);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(m_PrefabMaterialEntries.Count == 0))
            {
                if (GUILayout.Button("按名称填充替换材质"))
                {
                    AutoMatchPrefabMaterialsByName();
                    SaveWindowState();
                }
            }

            using (new EditorGUI.DisabledScope(m_PrefabMaterialEntries.Count == 0))
            {
                if (GUILayout.Button("同名材质一键全部替换"))
                {
                    ReplacePrefabMaterialsBySameNameInFolder();
                    SaveWindowState();
                }
            }
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(m_PrefabMaterialEntries.Count == 0))
            {
                if (GUILayout.Button("应用预制件材质替换"))
                {
                    ApplyPrefabMaterialReplacement();
                    SaveWindowState();
                }
            }

            EditorGUILayout.Space(6f);
            if (m_PrefabMaterialEntries.Count == 0)
            {
                EditorGUILayout.HelpBox("当前预制件没有读取到可替换材质。", MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            m_IsPrefabResultsExpanded = EditorGUILayout.Foldout(m_IsPrefabResultsExpanded, "材质结果列表 (" + m_PrefabMaterialEntries.Count + ")", true);
            if (!m_IsPrefabResultsExpanded)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < m_PrefabMaterialEntries.Count; i++)
            {
                DrawPrefabMaterialEntry(m_PrefabMaterialEntries[i], i);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawPrefabMaterialEntry(PrefabMaterialEntry entry, int index)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            entry.m_IsExpanded = EditorGUILayout.Foldout(entry.m_IsExpanded, "材质 " + (index + 1), true);
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("使用次数: " + entry.m_UsageCount, GUILayout.Width(120f));
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("源材质", entry.m_SourceMaterial, typeof(Material), false);
            }
            entry.m_TargetMaterial = (Material)EditorGUILayout.ObjectField("替换材质", entry.m_TargetMaterial, typeof(Material), false);

            if (entry.m_IsExpanded)
            {
                for (int i = 0; i < entry.m_RendererUsages.Count; i++)
                {
                    PrefabRendererUsageEntry usageEntry = entry.m_RendererUsages[i];
                    if (GUILayout.Button(usageEntry.m_DisplayLabel, EditorStyles.linkLabel))
                    {
                        SelectRendererUsageInScene(usageEntry);
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void LockFbxAsset(GameObject fbxAsset)
        {
            m_FbxMaterialEntries.Clear();
            m_LockedFbxAssetPath = string.Empty;

            if (fbxAsset == null)
            {
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(fbxAsset);
            if (!IsFbxAssetPath(assetPath))
            {
                EditorUtility.DisplayDialog("提示", "请选择 .fbx 模型资源。", "确定");
                m_FbxAsset = null;
                return;
            }

            ModelImporter importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
            {
                EditorUtility.DisplayDialog("提示", "当前资源不是可处理的模型导入资源。", "确定");
                m_FbxAsset = null;
                return;
            }

            m_LockedFbxAssetPath = assetPath;
            RefreshFbxMaterialEntries(importer);
        }

        private void RefreshFbxMaterialEntries(ModelImporter importer)
        {
            m_FbxMaterialEntries.Clear();

            Dictionary<AssetImporter.SourceAssetIdentifier, UnityEngine.Object> externalObjectMap = importer.GetExternalObjectMap();
            Dictionary<string, Material> sourceMaterialByName = AssetDatabase.LoadAllAssetRepresentationsAtPath(m_LockedFbxAssetPath)
                .OfType<Material>()
                .GroupBy(material => material.name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            List<AssetImporter.SourceAssetIdentifier> sourceIdentifiers = GetSourceMaterialIdentifiersByReflection(importer);
            if (sourceIdentifiers.Count == 0)
            {
                sourceIdentifiers = GetSourceMaterialIdentifiersBySerializedImporter(importer);
            }

            if (sourceIdentifiers.Count == 0)
            {
                sourceIdentifiers = GetSourceMaterialIdentifiersByRendererNames();
            }

            for (int i = 0; i < sourceIdentifiers.Count; i++)
            {
                AssetImporter.SourceAssetIdentifier sourceIdentifier = sourceIdentifiers[i];
                UnityEngine.Object remappedObject;
                FbxMaterialEntry entry = new FbxMaterialEntry();
                entry.m_SourceIdentifier = sourceIdentifier;
                entry.m_SourceName = GetIdentifierName(sourceIdentifier, "材质_" + (i + 1));
                sourceMaterialByName.TryGetValue(entry.m_SourceName, out entry.m_SourceMaterial);
                if (externalObjectMap.TryGetValue(sourceIdentifier, out remappedObject))
                {
                    entry.m_TargetMaterial = remappedObject as Material;
                    if (entry.m_TargetMaterial != null)
                    {
                        entry.m_CandidateCount = 1;
                        entry.m_CandidatePreviewPath = AssetDatabase.GetAssetPath(entry.m_TargetMaterial);
                    }
                }

                m_FbxMaterialEntries.Add(entry);
            }

            ApplyLoadedFbxTargets();
        }

        private void AutoMatchFbxMaterials()
        {
            if (string.IsNullOrEmpty(m_LockedFbxAssetPath))
            {
                return;
            }

            string[] searchFolders = GetSearchFolders();
            if (m_MaterialSearchMode == MaterialSearchMode.Folder && (searchFolders == null || searchFolders.Length == 0))
            {
                EditorUtility.DisplayDialog("提示", "请指定一个有效的材质查找目录。", "确定");
                return;
            }

            BuildMaterialCandidateIndex(searchFolders, out List<MaterialCandidate> candidates, out Dictionary<string, List<MaterialCandidate>> tokenIndex);
            for (int i = 0; i < m_FbxMaterialEntries.Count; i++)
            {
                FbxMaterialEntry entry = m_FbxMaterialEntries[i];
                entry.m_CandidateCount = 0;
                entry.m_CandidatePreviewPath = string.Empty;

                FindBestMatchByRegexAndOverlap(entry.m_SourceName, candidates, tokenIndex, out Material bestMaterial, out int candidateCount, out string previewPath);
                entry.m_CandidateCount = candidateCount;
                entry.m_TargetMaterial = bestMaterial;
                entry.m_CandidatePreviewPath = previewPath;
            }
        }

        private Dictionary<string, List<Material>> CollectMaterialsByName(string[] searchFolders)
        {
            string[] guids = searchFolders != null && searchFolders.Length > 0
                ? AssetDatabase.FindAssets("t:Material", searchFolders)
                : AssetDatabase.FindAssets("t:Material");

            Dictionary<string, List<Material>> materialsByName = new Dictionary<string, List<Material>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < guids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (ShouldSkipMaterialAssetPath(assetPath))
                {
                    continue;
                }

                Material material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                if (material == null)
                {
                    continue;
                }

                List<Material> materialList;
                if (!materialsByName.TryGetValue(material.name, out materialList))
                {
                    materialList = new List<Material>();
                    materialsByName.Add(material.name, materialList);
                }

                materialList.Add(material);
            }

            return materialsByName;
        }

        private bool ShouldSkipMaterialAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return true;
            }

            if (string.Equals(assetPath, m_LockedFbxAssetPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private void ApplyFbxMaterialRemaps()
        {
            ModelImporter importer = AssetImporter.GetAtPath(m_LockedFbxAssetPath) as ModelImporter;
            if (importer == null)
            {
                EditorUtility.DisplayDialog("提示", "未找到目标 FBX 的 ModelImporter。", "确定");
                return;
            }

            SerializedObject serializedImporter = new SerializedObject(importer);
            SerializedProperty remapWhenMaterialImportIsNoneProperty = serializedImporter.FindProperty("remapMaterialsIfMaterialImportModeIsNone");
            if (remapWhenMaterialImportIsNoneProperty != null && !remapWhenMaterialImportIsNoneProperty.boolValue)
            {
                remapWhenMaterialImportIsNoneProperty.boolValue = true;
                serializedImporter.ApplyModifiedPropertiesWithoutUndo();
            }

            for (int i = 0; i < m_FbxMaterialEntries.Count; i++)
            {
                FbxMaterialEntry entry = m_FbxMaterialEntries[i];
                if (entry.m_TargetMaterial == null)
                {
                    importer.RemoveRemap(entry.m_SourceIdentifier);
                }
                else
                {
                    importer.AddRemap(entry.m_SourceIdentifier, entry.m_TargetMaterial);
                }
            }

            SaveWindowState();
            importer.SaveAndReimport();
            RefreshFbxMaterialEntries(importer);
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("完成", "FBX 的 Remapped Materials 已更新。", "确定");
        }

        private void ScanPrefabMaterials(GameObject prefabAsset)
        {
            m_PrefabMaterialEntries.Clear();
            m_PrefabAssetPath = string.Empty;
            m_PrefabSceneInstanceRoot = null;

            if (prefabAsset == null)
            {
                return;
            }

            string assetPath = AssetDatabase.GetAssetPath(prefabAsset);
            if (IsPrefabAssetPath(assetPath))
            {
                m_PrefabAssetPath = assetPath;
                GameObject prefabRoot = PrefabUtility.LoadPrefabContents(assetPath);
                try
                {
                    CollectPrefabMaterialEntries(prefabRoot, prefabRoot.transform);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                }

                SortPrefabMaterialEntries();
                ApplyLoadedPrefabTargets();
                return;
            }

            m_PrefabSceneInstanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(prefabAsset) ?? prefabAsset;
            m_PrefabAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(m_PrefabSceneInstanceRoot);
            CollectPrefabMaterialEntries(m_PrefabSceneInstanceRoot, m_PrefabSceneInstanceRoot.transform);
            SortPrefabMaterialEntries();
            ApplyLoadedPrefabTargets();
        }

        private void CollectPrefabMaterialEntries(GameObject prefabRoot, Transform rootTransform)
        {
            Dictionary<Material, PrefabMaterialEntry> materialEntryMap = new Dictionary<Material, PrefabMaterialEntry>();
            Renderer[] renderers = prefabRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!IsSupportedRenderer(renderer))
                {
                    continue;
                }

                Material[] materials = renderer.sharedMaterials;
                for (int j = 0; j < materials.Length; j++)
                {
                    Material sourceMaterial = GetMaterialOrSourceFallback(renderer, materials, j);
                    if (sourceMaterial == null)
                    {
                        continue;
                    }

                    PrefabMaterialEntry entry;
                    if (!materialEntryMap.TryGetValue(sourceMaterial, out entry))
                    {
                        entry = new PrefabMaterialEntry();
                        entry.m_SourceMaterial = sourceMaterial;
                        materialEntryMap.Add(sourceMaterial, entry);
                        m_PrefabMaterialEntries.Add(entry);
                    }

                    entry.m_UsageCount++;
                    entry.m_RendererUsages.Add(CreateRendererUsageEntry(renderer, rootTransform, j));
                }
            }
        }

        private void ApplyPrefabMaterialReplacement()
        {
            if (m_PrefabAsset == null)
            {
                return;
            }

            Dictionary<Material, Material> replacementMap = new Dictionary<Material, Material>();
            for (int i = 0; i < m_PrefabMaterialEntries.Count; i++)
            {
                PrefabMaterialEntry entry = m_PrefabMaterialEntries[i];
                if (entry.m_SourceMaterial == null || entry.m_TargetMaterial == null || entry.m_SourceMaterial == entry.m_TargetMaterial)
                {
                    continue;
                }

                replacementMap[entry.m_SourceMaterial] = entry.m_TargetMaterial;
            }

            if (replacementMap.Count == 0)
            {
                EditorUtility.DisplayDialog("提示", "请至少指定一个需要替换的目标材质。", "确定");
                return;
            }

            SaveWindowState();
            string prefabAssetPath = AssetDatabase.GetAssetPath(m_PrefabAsset);
            if (IsPrefabAssetPath(prefabAssetPath))
            {
                m_PrefabAssetPath = prefabAssetPath;
                GameObject prefabRoot = PrefabUtility.LoadPrefabContents(m_PrefabAssetPath);
                bool isChanged = false;
                try
                {
                    Renderer[] renderers = prefabRoot.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        Renderer renderer = renderers[i];
                        if (!IsSupportedRenderer(renderer))
                        {
                            continue;
                        }

                        Material[] materials = renderer.sharedMaterials;
                        if (!ReplaceMaterialSlotsInOrder(renderer, materials, replacementMap))
                        {
                            continue;
                        }

                        renderer.sharedMaterials = materials;
                        EditorUtility.SetDirty(renderer);
                        isChanged = true;
                    }

                    if (isChanged)
                    {
                        PrefabUtility.SaveAsPrefabAsset(prefabRoot, m_PrefabAssetPath);
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                }

                if (!isChanged)
                {
                    EditorUtility.DisplayDialog("提示", "没有需要写回的材质替换。", "确定");
                    return;
                }

                AssetDatabase.ImportAsset(m_PrefabAssetPath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                ScanPrefabMaterials(m_PrefabAsset);
                EditorUtility.DisplayDialog("完成", "预制件材质替换已应用。", "确定");
                return;
            }

            GameObject instanceRoot = m_PrefabSceneInstanceRoot != null ? m_PrefabSceneInstanceRoot : m_PrefabAsset;
            bool isInstanceChanged = false;
            Renderer[] instanceRenderers = instanceRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < instanceRenderers.Length; i++)
            {
                Renderer renderer = instanceRenderers[i];
                if (!IsSupportedRenderer(renderer))
                {
                    continue;
                }

                Material[] materials = renderer.sharedMaterials;
                if (!ReplaceMaterialSlotsInOrder(renderer, materials, replacementMap))
                {
                    continue;
                }

                Undo.RecordObject(renderer, "Replace Prefab Materials");
                renderer.sharedMaterials = materials;
                EditorUtility.SetDirty(renderer);
                if (PrefabUtility.IsPartOfPrefabInstance(renderer))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                }

                isInstanceChanged = true;
            }

            if (!isInstanceChanged)
            {
                EditorUtility.DisplayDialog("提示", "没有需要写回的材质替换。", "确定");
                return;
            }

            EditorSceneManager.MarkSceneDirty(instanceRoot.scene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            ScanPrefabMaterials(m_PrefabAsset);
            EditorUtility.DisplayDialog("完成", "场景预制件实例材质替换已应用。", "确定");
        }

        private static bool ReplaceMaterialSlotsInOrder(Renderer renderer, Material[] materials, Dictionary<Material, Material> replacementMap)
        {
            if (renderer == null || materials == null || replacementMap == null || replacementMap.Count == 0)
            {
                return false;
            }

            bool isChanged = false;
            for (int i = 0; i < materials.Length; i++)
            {
                Material sourceMaterial = GetMaterialOrSourceFallback(renderer, materials, i);
                if (sourceMaterial == null)
                {
                    continue;
                }

                Material replacementMaterial;
                if (!replacementMap.TryGetValue(sourceMaterial, out replacementMaterial))
                {
                    continue;
                }

                materials[i] = replacementMaterial;
                isChanged = true;
            }

            return isChanged;
        }

        private static Material GetMaterialOrSourceFallback(Renderer renderer, Material[] materials, int materialSlotIndex)
        {
            if (materials != null && materialSlotIndex >= 0 && materialSlotIndex < materials.Length && materials[materialSlotIndex] != null)
            {
                return materials[materialSlotIndex];
            }

            Renderer sourceRenderer = renderer;
            while (sourceRenderer != null)
            {
                sourceRenderer = PrefabUtility.GetCorrespondingObjectFromSource(sourceRenderer);
                if (sourceRenderer == null)
                {
                    break;
                }

                Material[] sourceMaterials = sourceRenderer.sharedMaterials;
                if (sourceMaterials == null || materialSlotIndex < 0 || materialSlotIndex >= sourceMaterials.Length)
                {
                    continue;
                }

                Material sourceMaterial = sourceMaterials[materialSlotIndex];
                if (sourceMaterial != null)
                {
                    return sourceMaterial;
                }
            }

            return null;
        }

        private void LoadWindowState()
        {
            string json = EditorPrefs.GetString(m_WindowStateEditorPrefsKey, string.Empty);
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            WindowState state;
            try
            {
                state = JsonUtility.FromJson<WindowState>(json);
            }
            catch (ArgumentException)
            {
                return;
            }

            if (state == null)
            {
                return;
            }

            m_IsRestoringWindowState = true;
            m_LoadedWindowState = state;
            try
            {
                m_MaterialSearchMode = Enum.IsDefined(typeof(MaterialSearchMode), state.m_MaterialSearchMode)
                    ? (MaterialSearchMode)state.m_MaterialSearchMode
                    : MaterialSearchMode.Global;
                m_SearchFolder = LoadAssetAtPath<DefaultAsset>(state.m_SearchFolderPath);
                m_PrefabSearchFolder = LoadAssetAtPath<DefaultAsset>(state.m_PrefabSearchFolderPath);
                m_IsFbxResultsExpanded = state.m_IsFbxResultsExpanded;
                m_IsPrefabResultsExpanded = state.m_IsPrefabResultsExpanded;
                m_ScrollPosition = state.m_ScrollPosition;

                m_FbxAsset = LoadAssetAtPath<GameObject>(state.m_FbxAssetPath);
                if (m_FbxAsset != null)
                {
                    LockFbxAsset(m_FbxAsset);
                }

                m_PrefabAsset = LoadAssetAtPath<GameObject>(state.m_PrefabAssetPath);
                if (m_PrefabAsset != null)
                {
                    ScanPrefabMaterials(m_PrefabAsset);
                }
            }
            finally
            {
                m_IsRestoringWindowState = false;
            }
        }

        private void SaveWindowState()
        {
            if (m_IsRestoringWindowState)
            {
                return;
            }

            WindowState state = new WindowState();
            state.m_FbxAssetPath = !string.IsNullOrEmpty(m_LockedFbxAssetPath) ? m_LockedFbxAssetPath : AssetDatabase.GetAssetPath(m_FbxAsset);
            state.m_MaterialSearchMode = (int)m_MaterialSearchMode;
            state.m_SearchFolderPath = AssetDatabase.GetAssetPath(m_SearchFolder);
            state.m_PrefabAssetPath = GetPrefabStatePath();
            state.m_PrefabSearchFolderPath = AssetDatabase.GetAssetPath(m_PrefabSearchFolder);
            state.m_IsFbxResultsExpanded = m_IsFbxResultsExpanded;
            state.m_IsPrefabResultsExpanded = m_IsPrefabResultsExpanded;
            state.m_ScrollPosition = m_ScrollPosition;
            SaveFbxTargets(state.m_FbxTargets);
            SavePrefabTargets(state.m_PrefabTargets);

            EditorPrefs.SetString(m_WindowStateEditorPrefsKey, JsonUtility.ToJson(state));
            m_LoadedWindowState = state;
        }

        private string GetPrefabStatePath()
        {
            string prefabAssetPath = AssetDatabase.GetAssetPath(m_PrefabAsset);
            if (!string.IsNullOrEmpty(prefabAssetPath))
            {
                return prefabAssetPath;
            }

            return m_PrefabAssetPath;
        }

        private void SaveFbxTargets(List<MaterialTargetState> targetStates)
        {
            if (targetStates == null)
            {
                return;
            }

            for (int i = 0; i < m_FbxMaterialEntries.Count; i++)
            {
                FbxMaterialEntry entry = m_FbxMaterialEntries[i];
                if (entry == null || entry.m_TargetMaterial == null)
                {
                    continue;
                }

                targetStates.Add(CreateMaterialTargetState(entry.m_SourceName, entry.m_TargetMaterial));
            }
        }

        private void SavePrefabTargets(List<MaterialTargetState> targetStates)
        {
            if (targetStates == null)
            {
                return;
            }

            for (int i = 0; i < m_PrefabMaterialEntries.Count; i++)
            {
                PrefabMaterialEntry entry = m_PrefabMaterialEntries[i];
                if (entry == null || entry.m_SourceMaterial == null || entry.m_TargetMaterial == null)
                {
                    continue;
                }

                targetStates.Add(CreateMaterialTargetState(GetMaterialStateKey(entry.m_SourceMaterial), entry.m_TargetMaterial));
            }
        }

        private static MaterialTargetState CreateMaterialTargetState(string sourceKey, Material targetMaterial)
        {
            MaterialTargetState state = new MaterialTargetState();
            state.m_SourceKey = sourceKey ?? string.Empty;
            state.m_TargetPath = AssetDatabase.GetAssetPath(targetMaterial);
            state.m_TargetName = targetMaterial != null ? targetMaterial.name : string.Empty;
            return state;
        }

        private void ApplyLoadedFbxTargets()
        {
            if (m_LoadedWindowState == null || m_LoadedWindowState.m_FbxTargets == null)
            {
                return;
            }

            Dictionary<string, MaterialTargetState> targetStateMap = BuildTargetStateMap(m_LoadedWindowState.m_FbxTargets);
            for (int i = 0; i < m_FbxMaterialEntries.Count; i++)
            {
                FbxMaterialEntry entry = m_FbxMaterialEntries[i];
                if (entry == null || string.IsNullOrEmpty(entry.m_SourceName))
                {
                    continue;
                }

                MaterialTargetState targetState;
                if (!targetStateMap.TryGetValue(entry.m_SourceName, out targetState))
                {
                    continue;
                }

                Material targetMaterial = LoadMaterialTarget(targetState);
                if (targetMaterial != null)
                {
                    entry.m_TargetMaterial = targetMaterial;
                    entry.m_CandidatePreviewPath = AssetDatabase.GetAssetPath(targetMaterial);
                    entry.m_CandidateCount = Mathf.Max(entry.m_CandidateCount, 1);
                }
            }
        }

        private void ApplyLoadedPrefabTargets()
        {
            if (m_LoadedWindowState == null || m_LoadedWindowState.m_PrefabTargets == null)
            {
                return;
            }

            Dictionary<string, MaterialTargetState> targetStateMap = BuildTargetStateMap(m_LoadedWindowState.m_PrefabTargets);
            for (int i = 0; i < m_PrefabMaterialEntries.Count; i++)
            {
                PrefabMaterialEntry entry = m_PrefabMaterialEntries[i];
                if (entry == null || entry.m_SourceMaterial == null)
                {
                    continue;
                }

                MaterialTargetState targetState;
                if (!targetStateMap.TryGetValue(GetMaterialStateKey(entry.m_SourceMaterial), out targetState))
                {
                    continue;
                }

                Material targetMaterial = LoadMaterialTarget(targetState);
                if (targetMaterial != null)
                {
                    entry.m_TargetMaterial = targetMaterial;
                }
            }
        }

        private static Dictionary<string, MaterialTargetState> BuildTargetStateMap(List<MaterialTargetState> targetStates)
        {
            Dictionary<string, MaterialTargetState> targetStateMap = new Dictionary<string, MaterialTargetState>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targetStates.Count; i++)
            {
                MaterialTargetState targetState = targetStates[i];
                if (targetState == null || string.IsNullOrEmpty(targetState.m_SourceKey))
                {
                    continue;
                }

                targetStateMap[targetState.m_SourceKey] = targetState;
            }

            return targetStateMap;
        }

        private static Material LoadMaterialTarget(MaterialTargetState targetState)
        {
            if (targetState == null || string.IsNullOrEmpty(targetState.m_TargetPath))
            {
                return null;
            }

            Material material = AssetDatabase.LoadAssetAtPath<Material>(targetState.m_TargetPath);
            if (material != null && (string.IsNullOrEmpty(targetState.m_TargetName) || string.Equals(material.name, targetState.m_TargetName, StringComparison.OrdinalIgnoreCase)))
            {
                return material;
            }

            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(targetState.m_TargetPath);
            for (int i = 0; i < assets.Length; i++)
            {
                Material subMaterial = assets[i] as Material;
                if (subMaterial == null)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(targetState.m_TargetName) || string.Equals(subMaterial.name, targetState.m_TargetName, StringComparison.OrdinalIgnoreCase))
                {
                    return subMaterial;
                }
            }

            return material;
        }

        private static T LoadAssetAtPath<T>(string assetPath) where T : UnityEngine.Object
        {
            return string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.LoadAssetAtPath<T>(assetPath);
        }

        private static string GetMaterialStateKey(Material material)
        {
            if (material == null)
            {
                return string.Empty;
            }

            return AssetDatabase.GetAssetPath(material) + "|" + material.name;
        }

        private void SortPrefabMaterialEntries()
        {
            m_PrefabMaterialEntries.Sort((left, right) =>
            {
                string leftName = left != null && left.m_SourceMaterial != null ? left.m_SourceMaterial.name : string.Empty;
                string rightName = right != null && right.m_SourceMaterial != null ? right.m_SourceMaterial.name : string.Empty;
                return string.Compare(leftName, rightName, StringComparison.OrdinalIgnoreCase);
            });
        }

        private string[] GetSearchFolders()
        {
            if (m_MaterialSearchMode != MaterialSearchMode.Folder)
            {
                return null;
            }

            string folderPath = AssetDatabase.GetAssetPath(m_SearchFolder);
            if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
            {
                return null;
            }

            return new[] { folderPath };
        }

        private static List<AssetImporter.SourceAssetIdentifier> GetSourceMaterialIdentifiersByReflection(ModelImporter importer)
        {
            List<AssetImporter.SourceAssetIdentifier> identifiers = new List<AssetImporter.SourceAssetIdentifier>();
            PropertyInfo sourceMaterialsProperty = importer.GetType().GetProperty("sourceMaterials", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (sourceMaterialsProperty == null)
            {
                return identifiers;
            }

            Array values = sourceMaterialsProperty.GetValue(importer, null) as Array;
            if (values == null)
            {
                return identifiers;
            }

            for (int i = 0; i < values.Length; i++)
            {
                object value = values.GetValue(i);
                if (value is AssetImporter.SourceAssetIdentifier identifier)
                {
                    identifiers.Add(identifier);
                }
            }

            return identifiers;
        }

        private List<AssetImporter.SourceAssetIdentifier> GetSourceMaterialIdentifiersByRendererNames()
        {
            List<AssetImporter.SourceAssetIdentifier> identifiers = new List<AssetImporter.SourceAssetIdentifier>();
            if (string.IsNullOrEmpty(m_LockedFbxAssetPath))
            {
                return identifiers;
            }

            GameObject modelRoot = AssetDatabase.LoadAssetAtPath<GameObject>(m_LockedFbxAssetPath);
            if (modelRoot == null)
            {
                return identifiers;
            }

            Renderer[] renderers = modelRoot.GetComponentsInChildren<Renderer>(true);
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < renderers.Length; i++)
            {
                Material[] materials = renderers[i].sharedMaterials;
                for (int j = 0; j < materials.Length; j++)
                {
                    Material material = materials[j];
                    if (material == null || string.IsNullOrEmpty(material.name) || !names.Add(material.name))
                    {
                        continue;
                    }

                    identifiers.Add(new AssetImporter.SourceAssetIdentifier(typeof(Material), material.name));
                }
            }

            return identifiers;
        }

        private static List<AssetImporter.SourceAssetIdentifier> GetSourceMaterialIdentifiersBySerializedImporter(ModelImporter importer)
        {
            List<AssetImporter.SourceAssetIdentifier> identifiers = new List<AssetImporter.SourceAssetIdentifier>();
            SerializedObject serializedObject = new SerializedObject(importer);
            SerializedProperty materialsProperty = serializedObject.FindProperty("m_Materials");
            if (materialsProperty == null || !materialsProperty.isArray)
            {
                return identifiers;
            }

            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < materialsProperty.arraySize; i++)
            {
                SerializedProperty element = materialsProperty.GetArrayElementAtIndex(i);
                if (element == null)
                {
                    continue;
                }

                SerializedProperty nameProperty = element.FindPropertyRelative("name");
                string materialName = nameProperty != null ? nameProperty.stringValue : string.Empty;
                if (string.IsNullOrEmpty(materialName) || !names.Add(materialName))
                {
                    continue;
                }

                identifiers.Add(new AssetImporter.SourceAssetIdentifier(typeof(Material), materialName));
            }

            return identifiers;
        }

        private static string GetIdentifierName(AssetImporter.SourceAssetIdentifier sourceIdentifier, string fallbackName)
        {
            Type identifierType = sourceIdentifier.GetType();
            PropertyInfo nameProperty = identifierType.GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
            if (nameProperty != null)
            {
                object value = nameProperty.GetValue(sourceIdentifier, null);
                string name = value as string;
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }

            FieldInfo nameField = identifierType.GetField("name", BindingFlags.Public | BindingFlags.Instance);
            if (nameField != null)
            {
                object value = nameField.GetValue(sourceIdentifier);
                string name = value as string;
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }

            return fallbackName;
        }

        private static bool IsFbxAssetPath(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPrefabAssetPath(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSupportedRenderer(Renderer renderer)
        {
            return renderer is MeshRenderer || renderer is SkinnedMeshRenderer;
        }

        private static string GetRendererPath(Transform current, Transform root)
        {
            if (current == null || root == null)
            {
                return string.Empty;
            }

            if (current == root)
            {
                return root.name;
            }

            Stack<string> pathParts = new Stack<string>();
            Transform walk = current;
            while (walk != null && walk != root)
            {
                pathParts.Push(walk.name);
                walk = walk.parent;
            }

            pathParts.Push(root.name);
            return string.Join("/", pathParts.ToArray());
        }

        private void SelectRendererUsageInScene(PrefabRendererUsageEntry usageEntry)
        {
            if (usageEntry == null || string.IsNullOrEmpty(usageEntry.m_RelativeRendererPath))
            {
                return;
            }

            if (m_PrefabSceneInstanceRoot != null)
            {
                GameObject sceneObject = FindChildByRelativePath(m_PrefabSceneInstanceRoot.transform, usageEntry.m_RelativeRendererPath);
                if (sceneObject != null)
                {
                    Selection.activeGameObject = sceneObject;
                    EditorGUIUtility.PingObject(sceneObject);
                    return;
                }
            }

            GameObject targetObject = FindUsageObjectInPrefabStage(usageEntry.m_RelativeRendererPath);
            if (targetObject == null)
            {
                targetObject = FindUsageObjectInScenePrefabInstance(usageEntry.m_RelativeRendererPath);
            }

            if (targetObject == null)
            {
                EditorUtility.DisplayDialog("提示", "当前打开场景中未找到该预制件实例或对应节点。", "确定");
                return;
            }

            Selection.activeGameObject = targetObject;
            EditorGUIUtility.PingObject(targetObject);
        }

        private void AutoMatchPrefabMaterialsByName()
        {
            string folderPath = AssetDatabase.GetAssetPath(m_PrefabSearchFolder);
            if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
            {
                EditorUtility.DisplayDialog("提示", "请指定一个有效的材质查找目录。", "确定");
                return;
            }

            BuildMaterialCandidateIndex(new[] { folderPath }, out List<MaterialCandidate> candidates, out Dictionary<string, List<MaterialCandidate>> tokenIndex);
            for (int i = 0; i < m_PrefabMaterialEntries.Count; i++)
            {
                PrefabMaterialEntry entry = m_PrefabMaterialEntries[i];
                if (entry == null || entry.m_SourceMaterial == null)
                {
                    continue;
                }

                FindBestMatchByRegexAndOverlap(entry.m_SourceMaterial.name, candidates, tokenIndex, out Material bestMaterial, out _, out _);
                if (bestMaterial != null)
                {
                    entry.m_TargetMaterial = bestMaterial;
                }
            }
        }

        private void ReplacePrefabMaterialsBySameNameInFolder()
        {
            AutoMatchPrefabMaterialsByName();
            ApplyPrefabMaterialReplacement();
        }

        private void BuildMaterialCandidateIndex(string[] searchFolders, out List<MaterialCandidate> allCandidates, out Dictionary<string, List<MaterialCandidate>> tokenIndex)
        {
            string[] guids = searchFolders != null && searchFolders.Length > 0
                ? AssetDatabase.FindAssets("t:Material", searchFolders)
                : AssetDatabase.FindAssets("t:Material");

            allCandidates = new List<MaterialCandidate>(guids.Length);
            tokenIndex = new Dictionary<string, List<MaterialCandidate>>(StringComparer.OrdinalIgnoreCase);
            HashSet<int> uniqueMaterials = new HashSet<int>();

            for (int i = 0; i < guids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (ShouldSkipMaterialAssetPath(assetPath))
                {
                    continue;
                }

                Material material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
                if (material == null)
                {
                    continue;
                }

                int id = material.GetInstanceID();
                if (!uniqueMaterials.Add(id))
                {
                    continue;
                }

                string materialName = material.name ?? string.Empty;
                string normalizedName = NormalizeName(materialName);
                string[] tokens = TokenizeName(normalizedName);

                MaterialCandidate candidate = new MaterialCandidate
                {
                    m_Material = material,
                    m_Name = materialName,
                    m_NormalizedName = normalizedName,
                    m_Path = assetPath,
                    m_Tokens = tokens
                };

                allCandidates.Add(candidate);

                for (int t = 0; t < tokens.Length; t++)
                {
                    string token = tokens[t];
                    if (string.IsNullOrEmpty(token))
                    {
                        continue;
                    }

                    if (!tokenIndex.TryGetValue(token, out List<MaterialCandidate> list))
                    {
                        list = new List<MaterialCandidate>();
                        tokenIndex.Add(token, list);
                    }

                    list.Add(candidate);
                }
            }
        }

        private void FindBestMatchByRegexAndOverlap(string sourceName, List<MaterialCandidate> allCandidates, Dictionary<string, List<MaterialCandidate>> tokenIndex, out Material bestMaterial, out int candidateCount, out string previewPath)
        {
            bestMaterial = null;
            candidateCount = 0;
            previewPath = string.Empty;

            if (string.IsNullOrEmpty(sourceName) || allCandidates == null || allCandidates.Count == 0)
            {
                return;
            }

            string normalizedSource = NormalizeName(sourceName);
            string[] sourceTokens = TokenizeName(normalizedSource);
            Regex sourceRegex = BuildLooseRegexFromTokens(sourceTokens);

            HashSet<MaterialCandidate> candidateSet = new HashSet<MaterialCandidate>();
            for (int i = 0; i < sourceTokens.Length; i++)
            {
                if (!tokenIndex.TryGetValue(sourceTokens[i], out List<MaterialCandidate> list))
                {
                    continue;
                }

                for (int j = 0; j < list.Count; j++)
                {
                    candidateSet.Add(list[j]);
                }
            }

            if (candidateSet.Count == 0)
            {
                for (int i = 0; i < allCandidates.Count; i++)
                {
                    candidateSet.Add(allCandidates[i]);
                }
            }

            MaterialCandidate bestCandidate = null;
            int bestScore = int.MinValue;

            foreach (MaterialCandidate candidate in candidateSet)
            {
                if (candidate == null)
                {
                    continue;
                }

                int score = ComputeNameOverlapScore(normalizedSource, sourceTokens, sourceRegex, candidate);
                if (score <= 0)
                {
                    continue;
                }

                candidateCount++;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestCandidate = candidate;
                }
            }

            if (bestCandidate == null)
            {
                return;
            }

            bestMaterial = bestCandidate.m_Material;
            previewPath = bestCandidate.m_Path;
        }

        private string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            string lower = name.Trim().ToLowerInvariant();
            lower = Regex.Replace(lower, @"\s+", "_");
            lower = Regex.Replace(lower, @"[^a-z0-9_]+", "_");
            lower = Regex.Replace(lower, @"_+", "_").Trim('_');
            return lower;
        }

        private string[] TokenizeName(string normalizedName)
        {
            if (string.IsNullOrEmpty(normalizedName))
            {
                return Array.Empty<string>();
            }

            string[] raw = normalizedName.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> tokens = new List<string>(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                string token = raw[i];
                if (token.Length < 2)
                {
                    continue;
                }

                tokens.Add(token);
            }

            if (tokens.Count == 0)
            {
                tokens.Add(normalizedName);
            }

            return tokens.ToArray();
        }

        private Regex BuildLooseRegexFromTokens(string[] tokens)
        {
            if (tokens == null || tokens.Length == 0)
            {
                return null;
            }

            string pattern = string.Join(".*", tokens.Select(Regex.Escape));
            if (string.IsNullOrEmpty(pattern))
            {
                return null;
            }

            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private int ComputeNameOverlapScore(string normalizedSource, string[] sourceTokens, Regex sourceRegex, MaterialCandidate candidate)
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.m_NormalizedName))
            {
                return 0;
            }

            string candidateName = candidate.m_NormalizedName;
            if (string.Equals(normalizedSource, candidateName, StringComparison.OrdinalIgnoreCase))
            {
                return 100000;
            }

            int tokenOverlapLength = 0;
            for (int i = 0; i < sourceTokens.Length; i++)
            {
                string token = sourceTokens[i];
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }

                if (candidateName.Contains(token))
                {
                    tokenOverlapLength += token.Length;
                }
            }

            int lcs = LongestCommonSubstringLength(normalizedSource, candidateName);
            int lengthPenalty = Mathf.Abs(normalizedSource.Length - candidateName.Length);
            int regexBonus = sourceRegex != null && sourceRegex.IsMatch(candidateName) ? 20 : 0;

            if (tokenOverlapLength == 0)
            {
                if (normalizedSource.Length <= 2)
                {
                    return 0;
                }

                if (lcs < 3 && regexBonus == 0)
                {
                    return 0;
                }
            }

            int score = tokenOverlapLength * 10 + lcs * 2 + regexBonus - lengthPenalty;
            return score;
        }

        private int LongestCommonSubstringLength(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return 0;
            }

            int aLen = a.Length;
            int bLen = b.Length;
            int[] prev = new int[bLen + 1];
            int[] curr = new int[bLen + 1];
            int best = 0;

            for (int i = 1; i <= aLen; i++)
            {
                for (int j = 1; j <= bLen; j++)
                {
                    if (a[i - 1] == b[j - 1])
                    {
                        curr[j] = prev[j - 1] + 1;
                        if (curr[j] > best)
                        {
                            best = curr[j];
                        }
                    }
                    else
                    {
                        curr[j] = 0;
                    }
                }

                int[] temp = prev;
                prev = curr;
                curr = temp;
            }

            return best;
        }

        private GameObject FindUsageObjectInPrefabStage(string relativeRendererPath)
        {
            if (string.IsNullOrEmpty(m_PrefabAssetPath))
            {
                return null;
            }

            PrefabStage prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage == null || !string.Equals(prefabStage.assetPath, m_PrefabAssetPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return FindChildByRelativePath(prefabStage.prefabContentsRoot.transform, relativeRendererPath);
        }

        private GameObject FindUsageObjectInScenePrefabInstance(string relativeRendererPath)
        {
            if (string.IsNullOrEmpty(m_PrefabAssetPath))
            {
                return null;
            }

            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(m_PrefabAssetPath);
            if (prefabAsset == null)
            {
                return null;
            }

            Transform[] sceneTransforms = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; i < sceneTransforms.Length; i++)
            {
                Transform transform = sceneTransforms[i];
                if (transform == null || transform.parent != null || EditorUtility.IsPersistent(transform.gameObject))
                {
                    continue;
                }

                if (!PrefabUtility.IsPartOfPrefabInstance(transform.gameObject))
                {
                    continue;
                }

                GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(transform.gameObject);
                if (source != prefabAsset)
                {
                    continue;
                }

                GameObject targetObject = FindChildByRelativePath(transform, relativeRendererPath);
                if (targetObject != null)
                {
                    return targetObject;
                }
            }

            return null;
        }

        private static GameObject FindChildByRelativePath(Transform root, string relativePath)
        {
            if (root == null || string.IsNullOrEmpty(relativePath))
            {
                return null;
            }

            if (relativePath == ".")
            {
                return root.gameObject;
            }

            Transform child = root.Find(relativePath);
            return child != null ? child.gameObject : null;
        }

        private static PrefabRendererUsageEntry CreateRendererUsageEntry(Renderer renderer, Transform root, int materialSlotIndex)
        {
            string rendererPath = GetRendererPath(renderer.transform, root);
            string relativeRendererPath = GetRendererRelativePath(renderer.transform, root);
            string meshName = string.Empty;

            SkinnedMeshRenderer skinnedMeshRenderer = renderer as SkinnedMeshRenderer;
            if (skinnedMeshRenderer != null && skinnedMeshRenderer.sharedMesh != null)
            {
                meshName = skinnedMeshRenderer.sharedMesh.name;
            }
            else
            {
                MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    meshName = meshFilter.sharedMesh.name;
                }
            }

            PrefabRendererUsageEntry usageEntry = new PrefabRendererUsageEntry();
            usageEntry.m_RelativeRendererPath = relativeRendererPath;
            if (string.IsNullOrEmpty(meshName))
            {
                usageEntry.m_DisplayLabel = rendererPath + " [材质槽 " + materialSlotIndex + "]";
                return usageEntry;
            }

            usageEntry.m_DisplayLabel = rendererPath + " [Mesh: " + meshName + "] [材质槽 " + materialSlotIndex + "]";
            return usageEntry;
        }

        private static string GetRendererRelativePath(Transform current, Transform root)
        {
            if (current == null || root == null)
            {
                return string.Empty;
            }

            if (current == root)
            {
                return ".";
            }

            Stack<string> pathParts = new Stack<string>();
            Transform walk = current;
            while (walk != null && walk != root)
            {
                pathParts.Push(walk.name);
                walk = walk.parent;
            }

            return string.Join("/", pathParts.ToArray());
        }
    }
}
