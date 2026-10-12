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
            public string m_SourceDisplayName = string.Empty;
            public string m_FbxMaterialLookupKey = string.Empty;
            public bool m_HasFbxMaterialLookupKeyConflict;
            public int m_UsageCount;
            public bool m_IsExpanded;
            public readonly List<PrefabRendererUsageEntry> m_RendererUsages = new List<PrefabRendererUsageEntry>();
        }

        private sealed class PrefabRendererUsageEntry
        {
            public string m_DisplayLabel = string.Empty;
            public string m_RelativeRendererPath = string.Empty;
        }

        private sealed class OriginalRendererMaterialState
        {
            public Material[] m_OriginalMaterials = Array.Empty<Material>();
            public string[] m_OriginalMaterialNames = Array.Empty<string>();
        }

        private sealed class OriginalFbxRendererStateMap
        {
            public readonly Dictionary<string, OriginalRendererMaterialState> m_RendererStateByPath = new Dictionary<string, OriginalRendererMaterialState>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class OriginalFbxMaterialNameLookup
        {
            public readonly Dictionary<int, string> m_NameByMaterialId = new Dictionary<int, string>();
            public readonly Dictionary<string, string> m_NameByMaterialName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private const string m_WindowTitle = "FBX 材质映射";

        private GameObject m_FbxAsset;
        private string m_LockedFbxAssetPath = string.Empty;
        private MaterialSearchMode m_MaterialSearchMode = MaterialSearchMode.Global;
        private bool m_IsUseFbxMaterialNameAsSearchKey = false;
        private DefaultAsset m_SearchFolder;
        private readonly List<FbxMaterialEntry> m_FbxMaterialEntries = new List<FbxMaterialEntry>();

        private GameObject m_PrefabAsset;
        private string m_PrefabAssetPath = string.Empty;
        private readonly List<PrefabMaterialEntry> m_PrefabMaterialEntries = new List<PrefabMaterialEntry>();
        private DefaultAsset m_PrefabSearchFolder;
        private GameObject m_PrefabSceneInstanceRoot;
        private bool m_IsUsePrefabMeshFbxMaterialNameAsSearchKey = false;

        private Vector2 m_ScrollPosition;
        private bool m_IsFbxResultsExpanded = false;
        private bool m_IsPrefabResultsExpanded = false;

        [MenuItem("Tools/Azumi/模型材质/FBX Remapped Materials 工具")]
        public static void ShowWindow()
        {
            FbxMaterialRemapWindow window = GetWindow<FbxMaterialRemapWindow>(m_WindowTitle);
            window.minSize = new Vector2(620f, 720f);
            window.Show();
        }

        private void OnGUI()
        {
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);
            DrawFbxRemapSection();
            EditorGUILayout.Space(12f);
            DrawPrefabReplaceSection();
            EditorGUILayout.EndScrollView();
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
            m_IsUseFbxMaterialNameAsSearchKey = EditorGUILayout.ToggleLeft("使用 FBX 本体材质名称作为查找 Key", m_IsUseFbxMaterialNameAsSearchKey);
            if (m_MaterialSearchMode == MaterialSearchMode.Folder)
            {
                m_SearchFolder = (DefaultAsset)EditorGUILayout.ObjectField("材质查找目录", m_SearchFolder, typeof(DefaultAsset), false);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("按名称查找材质"))
            {
                AutoMatchFbxMaterials();
            }

            using (new EditorGUI.DisabledScope(m_FbxMaterialEntries.Count == 0))
            {
                if (GUILayout.Button("应用 Remapped Materials"))
                {
                    ApplyFbxMaterialRemaps();
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
            EditorGUI.BeginChangeCheck();
            m_IsUsePrefabMeshFbxMaterialNameAsSearchKey = EditorGUILayout.ToggleLeft("使用 Mesh 对应原 FBX 材质名作为查找 Key", m_IsUsePrefabMeshFbxMaterialNameAsSearchKey);
            if (EditorGUI.EndChangeCheck())
            {
                ScanPrefabMaterials(m_PrefabAsset);
            }

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(m_PrefabMaterialEntries.Count == 0))
            {
                if (GUILayout.Button("按名称填充替换材质"))
                {
                    AutoMatchPrefabMaterialsByName();
                }
            }

            using (new EditorGUI.DisabledScope(m_PrefabMaterialEntries.Count == 0))
            {
                if (GUILayout.Button("同名材质一键全部替换"))
                {
                    ReplacePrefabMaterialsBySameNameInFolder();
                }
            }
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(m_PrefabMaterialEntries.Count == 0))
            {
                if (GUILayout.Button("应用预制件材质替换"))
                {
                    ApplyPrefabMaterialReplacement();
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
                if (entry.m_SourceMaterial != null)
                {
                    EditorGUILayout.ObjectField("源材质", entry.m_SourceMaterial, typeof(Material), false);
                }
                else
                {
                    EditorGUILayout.TextField("源材质", entry.m_SourceDisplayName);
                }
            }

            string lookupKey = GetPrefabMaterialLookupKey(entry);
            if (!string.IsNullOrEmpty(lookupKey))
            {
                EditorGUILayout.TextField("查找 Key", lookupKey);
            }

            if (m_IsUsePrefabMeshFbxMaterialNameAsSearchKey && entry.m_HasFbxMaterialLookupKeyConflict)
            {
                EditorGUILayout.HelpBox("该材质在不同 Mesh 槽位上对应多个原 FBX 材质名，已回退为当前材质名进行查找。", MessageType.Warning);
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

                string lookupKey = GetFbxMaterialLookupKey(entry);
                FindBestMatchByRegexAndOverlap(lookupKey, candidates, tokenIndex, out Material bestMaterial, out int candidateCount, out string previewPath);
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

                m_PrefabMaterialEntries.Sort(ComparePrefabMaterialEntries);
                return;
            }

            m_PrefabSceneInstanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(prefabAsset) ?? prefabAsset;
            m_PrefabAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(m_PrefabSceneInstanceRoot);
            CollectPrefabMaterialEntries(m_PrefabSceneInstanceRoot, m_PrefabSceneInstanceRoot.transform);
            m_PrefabMaterialEntries.Sort(ComparePrefabMaterialEntries);
        }

        private void CollectPrefabMaterialEntries(GameObject prefabRoot, Transform rootTransform)
        {
            if (m_IsUsePrefabMeshFbxMaterialNameAsSearchKey)
            {
                CollectPrefabOriginalFbxMaterialEntries(prefabRoot, rootTransform);
                return;
            }

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
                    Material sourceMaterial = materials[j];
                    string fbxMaterialLookupKey = ResolveRendererSlotFbxMaterialName(renderer, j);
                    if (sourceMaterial == null)
                    {
                        continue;
                    }

                    PrefabMaterialEntry entry;
                    if (!materialEntryMap.TryGetValue(sourceMaterial, out entry))
                    {
                        entry = new PrefabMaterialEntry();
                        entry.m_SourceMaterial = sourceMaterial;
                        entry.m_SourceDisplayName = sourceMaterial.name;
                        materialEntryMap.Add(sourceMaterial, entry);
                        m_PrefabMaterialEntries.Add(entry);
                    }

                    UpdatePrefabMaterialLookupKey(entry, fbxMaterialLookupKey);
                    entry.m_UsageCount++;
                    entry.m_RendererUsages.Add(CreateRendererUsageEntry(renderer, rootTransform, j));
                }
            }
        }

        // 在“原始 FBX 材质名”模式下，不再依赖 prefab 当前 sharedMaterials，
        // 而是直接按 FBX 源 renderer 的原始材质槽状态收集可替换条目。
        private void CollectPrefabOriginalFbxMaterialEntries(GameObject prefabRoot, Transform rootTransform)
        {
            Dictionary<string, PrefabMaterialEntry> materialEntryMap = new Dictionary<string, PrefabMaterialEntry>(StringComparer.OrdinalIgnoreCase);
            Renderer[] renderers = prefabRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!IsSupportedRenderer(renderer))
                {
                    continue;
                }

                if (!TryGetOriginalRendererMaterialState(renderer, out OriginalRendererMaterialState originalState))
                {
                    continue;
                }

                for (int j = 0; j < originalState.m_OriginalMaterials.Length; j++)
                {
                    Material sourceMaterial = originalState.m_OriginalMaterials[j];
                    string lookupKey = originalState.m_OriginalMaterialNames[j];
                    if (string.IsNullOrEmpty(lookupKey))
                    {
                        continue;
                    }

                    if (!materialEntryMap.TryGetValue(lookupKey, out PrefabMaterialEntry entry))
                    {
                        entry = new PrefabMaterialEntry();
                        entry.m_SourceMaterial = sourceMaterial;
                        entry.m_SourceDisplayName = sourceMaterial != null ? sourceMaterial.name : lookupKey;
                        entry.m_FbxMaterialLookupKey = lookupKey;
                        materialEntryMap.Add(lookupKey, entry);
                        m_PrefabMaterialEntries.Add(entry);
                    }
                    else if (entry.m_SourceMaterial == null && sourceMaterial != null)
                    {
                        entry.m_SourceMaterial = sourceMaterial;
                        entry.m_SourceDisplayName = sourceMaterial.name;
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

            if (m_IsUsePrefabMeshFbxMaterialNameAsSearchKey)
            {
                ApplyPrefabMaterialReplacementByOriginalFbxState();
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
                        bool isRendererChanged = false;
                        for (int j = 0; j < materials.Length; j++)
                        {
                            Material replacementMaterial;
                            if (!replacementMap.TryGetValue(materials[j], out replacementMaterial))
                            {
                                continue;
                            }

                            materials[j] = replacementMaterial;
                            isRendererChanged = true;
                        }

                        if (!isRendererChanged)
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
                bool isRendererChanged = false;
                for (int j = 0; j < materials.Length; j++)
                {
                    Material replacementMaterial;
                    if (!replacementMap.TryGetValue(materials[j], out replacementMaterial))
                    {
                        continue;
                    }

                    materials[j] = replacementMaterial;
                    isRendererChanged = true;
                }

                if (!isRendererChanged)
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

        // 该分支会先恢复每个 renderer 在 FBX 中的原始材质槽数组，
        // 再按 FBX 原材质名把命中的槽位替换为目标材质，完全忽略 prefab 当前材质槽状态。
        private void ApplyPrefabMaterialReplacementByOriginalFbxState()
        {
            Dictionary<string, Material> replacementMap = BuildPrefabOriginalMaterialReplacementMap();
            if (replacementMap.Count == 0)
            {
                EditorUtility.DisplayDialog("提示", "请至少指定一个需要替换的目标材质。", "确定");
                return;
            }

            string prefabAssetPath = AssetDatabase.GetAssetPath(m_PrefabAsset);
            if (IsPrefabAssetPath(prefabAssetPath))
            {
                m_PrefabAssetPath = prefabAssetPath;
                GameObject prefabRoot = PrefabUtility.LoadPrefabContents(m_PrefabAssetPath);
                bool isChanged = false;
                try
                {
                    isChanged = ApplyOriginalFbxStateToRenderers(prefabRoot, false);
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
                EditorUtility.DisplayDialog("完成", "预制件材质替换已按 FBX 原始材质槽状态应用。", "确定");
                return;
            }

            GameObject instanceRoot = m_PrefabSceneInstanceRoot != null ? m_PrefabSceneInstanceRoot : m_PrefabAsset;
            bool isInstanceChanged = ApplyOriginalFbxStateToRenderers(instanceRoot, true);
            if (!isInstanceChanged)
            {
                EditorUtility.DisplayDialog("提示", "没有需要写回的材质替换。", "确定");
                return;
            }

            EditorSceneManager.MarkSceneDirty(instanceRoot.scene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            ScanPrefabMaterials(m_PrefabAsset);
            EditorUtility.DisplayDialog("完成", "场景预制件实例材质替换已按 FBX 原始材质槽状态应用。", "确定");
        }

        private Dictionary<string, Material> BuildPrefabOriginalMaterialReplacementMap()
        {
            Dictionary<string, Material> replacementMap = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < m_PrefabMaterialEntries.Count; i++)
            {
                PrefabMaterialEntry entry = m_PrefabMaterialEntries[i];
                string lookupKey = GetPrefabMaterialLookupKey(entry);
                if (string.IsNullOrEmpty(lookupKey) || entry.m_TargetMaterial == null)
                {
                    continue;
                }

                replacementMap[lookupKey] = entry.m_TargetMaterial;
            }

            return replacementMap;
        }

        private bool ApplyOriginalFbxStateToRenderers(GameObject rootObject, bool isRecordUndo)
        {
            if (rootObject == null)
            {
                return false;
            }

            Dictionary<string, Material> replacementMap = BuildPrefabOriginalMaterialReplacementMap();
            Renderer[] renderers = rootObject.GetComponentsInChildren<Renderer>(true);
            bool isChanged = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!IsSupportedRenderer(renderer))
                {
                    continue;
                }

                if (!TryGetOriginalRendererMaterialState(renderer, out OriginalRendererMaterialState originalState))
                {
                    continue;
                }

                Material[] currentMaterials = renderer.sharedMaterials;
                Material[] targetMaterials = new Material[originalState.m_OriginalMaterials.Length];
                bool isRendererChanged = currentMaterials.Length != targetMaterials.Length;
                for (int j = 0; j < originalState.m_OriginalMaterials.Length; j++)
                {
                    Material targetMaterial = originalState.m_OriginalMaterials[j];
                    string lookupKey = originalState.m_OriginalMaterialNames[j];
                    if (!string.IsNullOrEmpty(lookupKey) && replacementMap.TryGetValue(lookupKey, out Material replacementMaterial))
                    {
                        targetMaterial = replacementMaterial;
                    }

                    targetMaterials[j] = targetMaterial;
                    if (!isRendererChanged && currentMaterials[j] != targetMaterial)
                    {
                        isRendererChanged = true;
                    }
                }

                if (!isRendererChanged)
                {
                    continue;
                }

                if (isRecordUndo)
                {
                    Undo.RecordObject(renderer, "Replace Prefab Materials");
                }

                renderer.sharedMaterials = targetMaterials;
                EditorUtility.SetDirty(renderer);
                if (isRecordUndo && PrefabUtility.IsPartOfPrefabInstance(renderer))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                }

                isChanged = true;
            }

            return isChanged;
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
            return GetSourceMaterialIdentifiersByRendererNames(m_LockedFbxAssetPath);
        }

        private static List<AssetImporter.SourceAssetIdentifier> GetSourceMaterialIdentifiersByRendererNames(string modelAssetPath)
        {
            List<AssetImporter.SourceAssetIdentifier> identifiers = new List<AssetImporter.SourceAssetIdentifier>();
            if (string.IsNullOrEmpty(modelAssetPath))
            {
                return identifiers;
            }

            GameObject modelRoot = AssetDatabase.LoadAssetAtPath<GameObject>(modelAssetPath);
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

        private string GetFbxMaterialLookupKey(FbxMaterialEntry entry)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            if (m_IsUseFbxMaterialNameAsSearchKey && entry.m_SourceMaterial != null && !string.IsNullOrEmpty(entry.m_SourceMaterial.name))
            {
                return entry.m_SourceMaterial.name;
            }

            return entry.m_SourceName;
        }

        // 预制件自动匹配时，优先使用 mesh 槽位所属 FBX 的原材质名；解析失败或冲突时回退到当前材质名。
        private string GetPrefabMaterialLookupKey(PrefabMaterialEntry entry)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            if (m_IsUsePrefabMeshFbxMaterialNameAsSearchKey &&
                !entry.m_HasFbxMaterialLookupKeyConflict &&
                !string.IsNullOrEmpty(entry.m_FbxMaterialLookupKey))
            {
                return entry.m_FbxMaterialLookupKey;
            }

            if (entry.m_SourceMaterial == null)
            {
                return entry.m_SourceDisplayName;
            }

            return entry.m_SourceMaterial.name;
        }

        // 同一个材质如果对应多个不同的 FBX 原材质名，则停止使用 FBX 名称，避免错误自动映射。
        private static void UpdatePrefabMaterialLookupKey(PrefabMaterialEntry entry, string lookupKey)
        {
            if (entry == null || string.IsNullOrEmpty(lookupKey) || entry.m_HasFbxMaterialLookupKeyConflict)
            {
                return;
            }

            if (string.IsNullOrEmpty(entry.m_FbxMaterialLookupKey))
            {
                entry.m_FbxMaterialLookupKey = lookupKey;
                return;
            }

            if (!string.Equals(entry.m_FbxMaterialLookupKey, lookupKey, StringComparison.OrdinalIgnoreCase))
            {
                entry.m_FbxMaterialLookupKey = string.Empty;
                entry.m_HasFbxMaterialLookupKeyConflict = true;
            }
        }

        // 根据 renderer 当前材质槽绑定的 mesh，回查来源 FBX 中同槽位的原始材质名。
        private string ResolveRendererSlotFbxMaterialName(Renderer renderer, int materialSlotIndex)
        {
            Mesh sharedMesh = GetRendererSharedMesh(renderer);
            if (sharedMesh == null)
            {
                return string.Empty;
            }

            string modelAssetPath = AssetDatabase.GetAssetPath(sharedMesh);
            if (!IsFbxAssetPath(modelAssetPath))
            {
                return string.Empty;
            }

            ModelImporter importer = AssetImporter.GetAtPath(modelAssetPath) as ModelImporter;
            if (importer == null)
            {
                return string.Empty;
            }

            List<AssetImporter.SourceAssetIdentifier> sourceIdentifiers = GetSourceMaterialIdentifiersByReflection(importer);
            if (sourceIdentifiers.Count == 0)
            {
                sourceIdentifiers = GetSourceMaterialIdentifiersBySerializedImporter(importer);
            }

            if (sourceIdentifiers.Count == 0)
            {
                sourceIdentifiers = GetSourceMaterialIdentifiersByRendererNames(modelAssetPath);
            }

            if (materialSlotIndex < 0 || materialSlotIndex >= sourceIdentifiers.Count)
            {
                return string.Empty;
            }

            return GetIdentifierName(sourceIdentifiers[materialSlotIndex], string.Empty);
        }

        private static int ComparePrefabMaterialEntries(PrefabMaterialEntry left, PrefabMaterialEntry right)
        {
            string leftName = GetPrefabMaterialEntryDisplayName(left);
            string rightName = GetPrefabMaterialEntryDisplayName(right);
            return string.Compare(leftName, rightName, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetPrefabMaterialEntryDisplayName(PrefabMaterialEntry entry)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrEmpty(entry.m_SourceDisplayName))
            {
                return entry.m_SourceDisplayName;
            }

            if (entry.m_SourceMaterial != null)
            {
                return entry.m_SourceMaterial.name;
            }

            return entry.m_FbxMaterialLookupKey;
        }

        // 沿着 Prefab/模型来源链一直回溯，直到拿到真正来自 FBX 模型资源的源 renderer。
        // 原始材质状态只从 FBX 直出资源本体读取，不参考 prefab 当前材质与当前材质槽设置。
        private bool TryGetOriginalRendererMaterialState(Renderer renderer, out OriginalRendererMaterialState originalState)
        {
            originalState = null;
            Renderer sourceRenderer = ResolveOriginalFbxRenderer(renderer);
            if (sourceRenderer == null)
            {
                return false;
            }

            string fbxAssetPath = AssetDatabase.GetAssetPath(sourceRenderer.gameObject);
            if (!IsFbxAssetPath(fbxAssetPath))
            {
                return false;
            }

            if (!TryBuildOriginalFbxRendererStateMap(fbxAssetPath, out OriginalFbxRendererStateMap stateMap))
            {
                return false;
            }

            GameObject fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath);
            if (fbxRoot == null)
            {
                return false;
            }

            string rendererRelativePath = GetRendererRelativePath(sourceRenderer.transform, fbxRoot.transform);
            return stateMap.m_RendererStateByPath.TryGetValue(rendererRelativePath, out originalState);
        }

        private Renderer ResolveOriginalFbxRenderer(Renderer renderer)
        {
            if (renderer == null)
            {
                return null;
            }

            Renderer currentRenderer = renderer;
            HashSet<int> visited = new HashSet<int>();
            while (currentRenderer != null && visited.Add(currentRenderer.GetInstanceID()))
            {
                Mesh sharedMesh = GetRendererSharedMesh(currentRenderer);
                string meshAssetPath = sharedMesh != null ? AssetDatabase.GetAssetPath(sharedMesh) : string.Empty;
                string rendererAssetPath = AssetDatabase.GetAssetPath(currentRenderer.gameObject);
                if (IsFbxAssetPath(meshAssetPath) && IsFbxAssetPath(rendererAssetPath))
                {
                    return currentRenderer;
                }

                Renderer sourceRenderer = PrefabUtility.GetCorrespondingObjectFromSource(currentRenderer);
                if (sourceRenderer == null || sourceRenderer == currentRenderer)
                {
                    break;
                }

                currentRenderer = sourceRenderer;
            }

            return null;
        }

        private bool TryBuildOriginalFbxRendererStateMap(string fbxAssetPath, out OriginalFbxRendererStateMap stateMap)
        {
            stateMap = null;
            if (!IsFbxAssetPath(fbxAssetPath))
            {
                return false;
            }

            GameObject fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath);
            if (fbxRoot == null)
            {
                return false;
            }

            ModelImporter importer = AssetImporter.GetAtPath(fbxAssetPath) as ModelImporter;
            if (importer == null)
            {
                return false;
            }

            BuildOriginalFbxMaterialNameLookup(importer, fbxAssetPath, out OriginalFbxMaterialNameLookup materialNameLookup);
            Renderer[] sourceRenderers = fbxRoot.GetComponentsInChildren<Renderer>(true);
            OriginalFbxRendererStateMap result = new OriginalFbxRendererStateMap();
            for (int i = 0; i < sourceRenderers.Length; i++)
            {
                Renderer sourceRenderer = sourceRenderers[i];
                if (!IsSupportedRenderer(sourceRenderer))
                {
                    continue;
                }

                Material[] sourceMaterials = sourceRenderer.sharedMaterials;
                if (sourceMaterials == null || sourceMaterials.Length == 0)
                {
                    continue;
                }

                OriginalRendererMaterialState rendererState = new OriginalRendererMaterialState();
                rendererState.m_OriginalMaterials = new Material[sourceMaterials.Length];
                rendererState.m_OriginalMaterialNames = new string[sourceMaterials.Length];
                for (int j = 0; j < sourceMaterials.Length; j++)
                {
                    Material sourceMaterial = sourceMaterials[j];
                    rendererState.m_OriginalMaterials[j] = sourceMaterial;
                    rendererState.m_OriginalMaterialNames[j] = GetOriginalFbxMaterialName(sourceMaterial, materialNameLookup);
                }

                string rendererRelativePath = GetRendererRelativePath(sourceRenderer.transform, fbxRoot.transform);
                result.m_RendererStateByPath[rendererRelativePath] = rendererState;
            }

            stateMap = result;
            return true;
        }

        private void BuildOriginalFbxMaterialNameLookup(ModelImporter importer, string fbxAssetPath, out OriginalFbxMaterialNameLookup materialNameLookup)
        {
            materialNameLookup = new OriginalFbxMaterialNameLookup();

            List<AssetImporter.SourceAssetIdentifier> orderedSourceIdentifiers = GetOrderedSourceMaterialIdentifiers(importer, fbxAssetPath);
            Material[] embeddedMaterials = AssetDatabase.LoadAllAssetRepresentationsAtPath(fbxAssetPath)
                .OfType<Material>()
                .ToArray();

            int pairCount = Mathf.Min(orderedSourceIdentifiers.Count, embeddedMaterials.Length);
            for (int i = 0; i < pairCount; i++)
            {
                Material embeddedMaterial = embeddedMaterials[i];
                if (embeddedMaterial == null)
                {
                    continue;
                }

                string originalMaterialName = GetIdentifierName(orderedSourceIdentifiers[i], string.Empty);
                if (string.IsNullOrEmpty(originalMaterialName))
                {
                    continue;
                }

                materialNameLookup.m_NameByMaterialId[embeddedMaterial.GetInstanceID()] = originalMaterialName;
                if (!materialNameLookup.m_NameByMaterialName.ContainsKey(embeddedMaterial.name))
                {
                    materialNameLookup.m_NameByMaterialName.Add(embeddedMaterial.name, originalMaterialName);
                }
            }
        }

        private string GetOriginalFbxMaterialName(Material sourceMaterial, OriginalFbxMaterialNameLookup materialNameLookup)
        {
            if (sourceMaterial == null)
            {
                return string.Empty;
            }

            if (materialNameLookup != null)
            {
                if (materialNameLookup.m_NameByMaterialId.TryGetValue(sourceMaterial.GetInstanceID(), out string originalMaterialName))
                {
                    return originalMaterialName;
                }

                if (materialNameLookup.m_NameByMaterialName.TryGetValue(sourceMaterial.name, out originalMaterialName))
                {
                    return originalMaterialName;
                }
            }

            return sourceMaterial.name;
        }

        private List<AssetImporter.SourceAssetIdentifier> GetOrderedSourceMaterialIdentifiers(ModelImporter importer, string modelAssetPath)
        {
            List<AssetImporter.SourceAssetIdentifier> sourceIdentifiers = GetSourceMaterialIdentifiersByReflection(importer);
            if (sourceIdentifiers.Count > 0)
            {
                return sourceIdentifiers;
            }

            sourceIdentifiers = GetOrderedSourceMaterialIdentifiersBySerializedImporter(importer);
            if (sourceIdentifiers.Count > 0)
            {
                return sourceIdentifiers;
            }

            return GetOrderedSourceMaterialIdentifiersByRendererSlots(modelAssetPath);
        }

        private static List<AssetImporter.SourceAssetIdentifier> GetOrderedSourceMaterialIdentifiersBySerializedImporter(ModelImporter importer)
        {
            List<AssetImporter.SourceAssetIdentifier> identifiers = new List<AssetImporter.SourceAssetIdentifier>();
            SerializedObject serializedObject = new SerializedObject(importer);
            SerializedProperty materialsProperty = serializedObject.FindProperty("m_Materials");
            if (materialsProperty == null || !materialsProperty.isArray)
            {
                return identifiers;
            }

            for (int i = 0; i < materialsProperty.arraySize; i++)
            {
                SerializedProperty element = materialsProperty.GetArrayElementAtIndex(i);
                if (element == null)
                {
                    continue;
                }

                SerializedProperty nameProperty = element.FindPropertyRelative("name");
                string materialName = nameProperty != null ? nameProperty.stringValue : string.Empty;
                identifiers.Add(new AssetImporter.SourceAssetIdentifier(typeof(Material), materialName));
            }

            return identifiers;
        }

        private static List<AssetImporter.SourceAssetIdentifier> GetOrderedSourceMaterialIdentifiersByRendererSlots(string modelAssetPath)
        {
            List<AssetImporter.SourceAssetIdentifier> identifiers = new List<AssetImporter.SourceAssetIdentifier>();
            if (string.IsNullOrEmpty(modelAssetPath))
            {
                return identifiers;
            }

            GameObject modelRoot = AssetDatabase.LoadAssetAtPath<GameObject>(modelAssetPath);
            if (modelRoot == null)
            {
                return identifiers;
            }

            Renderer[] renderers = modelRoot.GetComponentsInChildren<Renderer>(true);
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
                    Material material = materials[j];
                    identifiers.Add(new AssetImporter.SourceAssetIdentifier(typeof(Material), material != null ? material.name : string.Empty));
                }
            }

            return identifiers;
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

                string lookupKey = GetPrefabMaterialLookupKey(entry);
                FindBestMatchByRegexAndOverlap(lookupKey, candidates, tokenIndex, out Material bestMaterial, out _, out _);
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
            Mesh sharedMesh = GetRendererSharedMesh(renderer);
            if (sharedMesh != null)
            {
                meshName = sharedMesh.name;
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

        private static Mesh GetRendererSharedMesh(Renderer renderer)
        {
            SkinnedMeshRenderer skinnedMeshRenderer = renderer as SkinnedMeshRenderer;
            if (skinnedMeshRenderer != null)
            {
                return skinnedMeshRenderer.sharedMesh;
            }

            MeshFilter meshFilter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            return meshFilter != null ? meshFilter.sharedMesh : null;
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
