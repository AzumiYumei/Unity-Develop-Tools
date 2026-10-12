// ================================================================
// 资源依赖分析与对比工具
//
// 文件名称: RecursiveAssetDependencyCompareWindow.cs
// 版本: 1.3
//
// 描述:
// 该工具用于分析某个资源的完整依赖集合，依赖获取方式与 Unity 导出包时的
// “包含依赖资源”逻辑一致，适合快速查看某个资源最终会带出哪些下游资源。
// 同时支持输入第二个资源，对比两个资源完整依赖集合中的重叠项与各自独有项，
// 便于快速判断两个资源在材质、贴图、模型、Shader 等依赖上的复用情况。
//
// 使用方式:
// 1. 打开菜单 Tools/Azumi/资源工具/资源依赖分析与对比。
// 2. 在“主资源”中指定要分析的资源。
// 3. 若需要对比，再在“对比资源”中指定第二个资源。
// 4. 点击“分析依赖”后，可查看主资源的完整依赖列表。
// 5. 若填写了第二个资源，可继续查看两个资源的重叠依赖与各自独有依赖。
//
// 注意事项:
// 1. 本工具基于 AssetDatabase 依赖关系分析，适用于 Unity 可追踪的资源依赖。
// 2. 默认会排除脚本、程序集定义等代码类资源，避免结果中混入大量工程脚本依赖。
// 3. 默认仅统计 Assets 目录下的资源，避免 Package 依赖过多影响阅读。
//
// 修改历史:
// 2026-06-29 - 初始版本
// 2026-06-29 - 移除递归树结构，改为使用类似导出包的完整依赖集合分析
// 2026-06-29 - 增加依赖项的引用依据展示，可查看具体引用对象与属性路径
// 2026-06-29 - 增加资源类型剔除筛选，支持排除纹理、材质等依赖项
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AzumisEditorTools
{
    public class RecursiveAssetDependencyCompareWindow : EditorWindow
    {
        private sealed class ReferenceEvidenceRecord
        {
            public string m_SourceAssetPath = string.Empty;
            public string m_SourceAssetKind = string.Empty;
            public string m_OwnerName = string.Empty;
            public string m_OwnerType = string.Empty;
            public string m_PropertyPath = string.Empty;
        }

        private sealed class AnalysisResult
        {
            public string m_RootAssetPath = string.Empty;
            public readonly HashSet<string> m_AllDependencyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> m_SortedDependencyPaths = new List<string>();
            public readonly Dictionary<string, List<ReferenceEvidenceRecord>> m_EvidenceByDependencyPath =
                new Dictionary<string, List<ReferenceEvidenceRecord>>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ComparisonResult
        {
            public readonly List<string> m_CommonPaths = new List<string>();
            public readonly List<string> m_OnlyPrimaryPaths = new List<string>();
            public readonly List<string> m_OnlySecondaryPaths = new List<string>();
        }

        private const string m_WindowTitle = "资源依赖分析";
        private const string m_MenuPath = "Tools/Azumi/资源工具/资源依赖分析与对比";

        private static readonly HashSet<string> m_IgnoredExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".js",
            ".boo",
            ".dll",
            ".asmdef",
            ".asmref",
            ".rsp",
            ".meta"
        };

        private UnityEngine.Object m_PrimaryAsset;
        private UnityEngine.Object m_SecondaryAsset;
        private bool m_OnlyIncludeAssetsFolder = true;
        private bool m_IgnoreCodeAssets = true;
        private bool m_ExcludeTextureAssets;
        private bool m_ExcludeMaterialAssets;
        private bool m_ExcludeShaderAssets;
        private bool m_ExcludeModelAssets;
        private bool m_ExcludePrefabAssets;
        private bool m_ExcludeSceneAssets;
        private bool m_ExcludeAnimationAssets;
        private bool m_ExcludeAudioAssets;
        private string m_CustomExcludedExtensions = string.Empty;
        private bool m_IsDependencySectionExpanded = true;
        private bool m_IsPrimaryDependencyListExpanded = true;
        private bool m_IsSecondaryDependencyListExpanded = true;
        private bool m_IsComparisonSectionExpanded = true;
        private bool m_IsCommonPathsExpanded = true;
        private bool m_IsOnlyPrimaryPathsExpanded = true;
        private bool m_IsOnlySecondaryPathsExpanded = true;
        private Vector2 m_ScrollPosition;
        private string m_LastSummary = string.Empty;
        private readonly Dictionary<string, bool> m_EvidenceFoldoutStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private AnalysisResult m_PrimaryResult;
        private AnalysisResult m_SecondaryResult;
        private ComparisonResult m_ComparisonResult;

        [MenuItem(m_MenuPath)]
        public static void ShowWindow()
        {
            RecursiveAssetDependencyCompareWindow window = GetWindow<RecursiveAssetDependencyCompareWindow>(m_WindowTitle);
            window.minSize = new Vector2(860f, 620f);
            window.Show();
        }

        private void OnGUI()
        {
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);
            DrawInputSection();
            EditorGUILayout.Space(10f);
            DrawActionSection();
            EditorGUILayout.Space(10f);
            DrawSummarySection();
            EditorGUILayout.Space(10f);
            DrawDependencyListSection();
            EditorGUILayout.Space(10f);
            DrawComparisonSection();
            EditorGUILayout.EndScrollView();
        }

        // 输入区用于指定分析对象与过滤规则，避免结果被脚本或 Package 依赖干扰。
        private void DrawInputSection()
        {
            EditorGUILayout.LabelField("分析设置", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            m_PrimaryAsset = EditorGUILayout.ObjectField("主资源", m_PrimaryAsset, typeof(UnityEngine.Object), false);
            m_SecondaryAsset = EditorGUILayout.ObjectField("对比资源", m_SecondaryAsset, typeof(UnityEngine.Object), false);
            m_OnlyIncludeAssetsFolder = EditorGUILayout.ToggleLeft("仅统计 Assets 目录中的资源", m_OnlyIncludeAssetsFolder);
            m_IgnoreCodeAssets = EditorGUILayout.ToggleLeft("排除脚本与程序集类资源", m_IgnoreCodeAssets);
            DrawExcludeFilterSection();

            EditorGUILayout.HelpBox(
                "分析逻辑与 Unity 导出包时的依赖收集方式一致，会直接给出资源完整依赖集合。" +
                "若填写“对比资源”，工具会额外输出两个资源依赖集合的重叠结果。",
                MessageType.Info);

            EditorGUILayout.EndVertical();
        }

        // 剔除区用于从最终依赖结果中排除某些常见资源类型，便于聚焦更关心的依赖类别。
        private void DrawExcludeFilterSection()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("剔除类型", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUILayout.HorizontalScope())
            {
                m_ExcludeTextureAssets = EditorGUILayout.ToggleLeft("Texture", m_ExcludeTextureAssets, GUILayout.Width(90f));
                m_ExcludeMaterialAssets = EditorGUILayout.ToggleLeft("Material", m_ExcludeMaterialAssets, GUILayout.Width(90f));
                m_ExcludeShaderAssets = EditorGUILayout.ToggleLeft("Shader", m_ExcludeShaderAssets, GUILayout.Width(90f));
                m_ExcludeModelAssets = EditorGUILayout.ToggleLeft("Model", m_ExcludeModelAssets, GUILayout.Width(90f));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                m_ExcludePrefabAssets = EditorGUILayout.ToggleLeft("Prefab", m_ExcludePrefabAssets, GUILayout.Width(90f));
                m_ExcludeSceneAssets = EditorGUILayout.ToggleLeft("Scene", m_ExcludeSceneAssets, GUILayout.Width(90f));
                m_ExcludeAnimationAssets = EditorGUILayout.ToggleLeft("Animation", m_ExcludeAnimationAssets, GUILayout.Width(90f));
                m_ExcludeAudioAssets = EditorGUILayout.ToggleLeft("Audio", m_ExcludeAudioAssets, GUILayout.Width(90f));
            }

            m_CustomExcludedExtensions = EditorGUILayout.TextField("自定义扩展名", m_CustomExcludedExtensions);
            EditorGUILayout.HelpBox(
                "可用开关直接排除常见资源类型。自定义扩展名支持输入多个，以逗号或分号分隔，例如 .tga,.psd,.bytes",
                MessageType.None);

            EditorGUILayout.EndVertical();
        }

        private void DrawActionSection()
        {
            EditorGUILayout.LabelField("操作", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("分析依赖", GUILayout.Height(28f)))
                {
                    AnalyzeDependencies();
                }

                if (GUILayout.Button("清空结果", GUILayout.Height(28f)))
                {
                    ClearResults();
                }
            }

            if (!string.IsNullOrEmpty(m_LastSummary))
            {
                EditorGUILayout.HelpBox(m_LastSummary, MessageType.None);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSummarySection()
        {
            EditorGUILayout.LabelField("汇总信息", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            DrawSingleSummary("主资源", m_PrimaryAsset, m_PrimaryResult);
            DrawSingleSummary("对比资源", m_SecondaryAsset, m_SecondaryResult);

            if (m_ComparisonResult != null)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("重叠依赖数", m_ComparisonResult.m_CommonPaths.Count.ToString());
                EditorGUILayout.LabelField("主资源独有依赖数", m_ComparisonResult.m_OnlyPrimaryPaths.Count.ToString());
                EditorGUILayout.LabelField("对比资源独有依赖数", m_ComparisonResult.m_OnlySecondaryPaths.Count.ToString());
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawSingleSummary(string label, UnityEngine.Object assetObject, AnalysisResult result)
        {
            string assetPath = assetObject != null ? AssetDatabase.GetAssetPath(assetObject) : string.Empty;
            EditorGUILayout.LabelField(label + "路径", string.IsNullOrEmpty(assetPath) ? "未指定" : assetPath);
            EditorGUILayout.LabelField(label + "依赖数", result != null ? result.m_AllDependencyPaths.Count.ToString() : "0");
        }

        // 列表区用于直接展示完整依赖集合，便于快速核对导出包会带出的资源。
        private void DrawDependencyListSection()
        {
            m_IsDependencySectionExpanded = EditorGUILayout.Foldout(m_IsDependencySectionExpanded, "依赖列表", true);
            if (!m_IsDependencySectionExpanded)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");

            if (m_PrimaryResult == null)
            {
                EditorGUILayout.HelpBox("当前没有可显示的依赖结果。请先指定主资源并执行分析。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            DrawAnalysisPathList("主资源依赖列表", m_PrimaryResult, ref m_IsPrimaryDependencyListExpanded);

            if (m_SecondaryResult != null)
            {
                EditorGUILayout.Space(8f);
                DrawAnalysisPathList("对比资源依赖列表", m_SecondaryResult, ref m_IsSecondaryDependencyListExpanded);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawAnalysisPathList(string title, AnalysisResult result, ref bool isExpanded)
        {
            isExpanded = EditorGUILayout.Foldout(
                isExpanded,
                title + " (" + (result != null ? result.m_SortedDependencyPaths.Count : 0) + ")",
                true);

            if (!isExpanded)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");
            if (result == null || result.m_SortedDependencyPaths.Count == 0)
            {
                EditorGUILayout.HelpBox("当前列表为空。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < result.m_SortedDependencyPaths.Count; i++)
            {
                string dependencyPath = result.m_SortedDependencyPaths[i];
                DrawDependencyEntry(result, dependencyPath, "analysis|" + result.m_RootAssetPath + "|" + dependencyPath);
            }

            EditorGUILayout.EndVertical();
        }

        // 对比区输出两个资源递归依赖集合的交集和差集，便于快速判断复用与差异。
        private void DrawComparisonSection()
        {
            m_IsComparisonSectionExpanded = EditorGUILayout.Foldout(m_IsComparisonSectionExpanded, "对比结果", true);
            if (!m_IsComparisonSectionExpanded)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");

            if (m_ComparisonResult == null)
            {
                EditorGUILayout.HelpBox("当前没有可显示的对比结果。请先指定主资源与对比资源后执行分析。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            DrawPathList("重叠依赖", m_ComparisonResult.m_CommonPaths, ref m_IsCommonPathsExpanded);
            EditorGUILayout.Space(6f);
            DrawPathList("主资源独有依赖", m_ComparisonResult.m_OnlyPrimaryPaths, ref m_IsOnlyPrimaryPathsExpanded);
            EditorGUILayout.Space(6f);
            DrawPathList("对比资源独有依赖", m_ComparisonResult.m_OnlySecondaryPaths, ref m_IsOnlySecondaryPathsExpanded);

            EditorGUILayout.EndVertical();
        }

        private void DrawPathList(string title, List<string> paths, ref bool isExpanded)
        {
            isExpanded = EditorGUILayout.Foldout(isExpanded, title + " (" + paths.Count + ")", true);
            if (!isExpanded)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");

            if (paths.Count == 0)
            {
                EditorGUILayout.HelpBox("当前列表为空。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < paths.Count; i++)
            {
                DrawComparisonDependencyEntry(paths[i], title + "|" + paths[i]);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawDependencyEntry(AnalysisResult result, string dependencyPath, string foldoutKey)
        {
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.SelectableLabel(
                    dependencyPath,
                    EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));

                if (GUILayout.Button("定位", GUILayout.Width(56f)))
                {
                    PingAsset(dependencyPath);
                }
            }

            List<ReferenceEvidenceRecord> evidenceRecords = GetEvidenceRecords(result, dependencyPath);
            DrawEvidenceList("引用依据", evidenceRecords, foldoutKey);
            EditorGUILayout.EndVertical();
        }

        private void DrawComparisonDependencyEntry(string dependencyPath, string foldoutKeyPrefix)
        {
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.SelectableLabel(
                    dependencyPath,
                    EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));

                if (GUILayout.Button("定位", GUILayout.Width(56f)))
                {
                    PingAsset(dependencyPath);
                }
            }

            DrawEvidenceList("主资源引用依据", GetEvidenceRecords(m_PrimaryResult, dependencyPath), foldoutKeyPrefix + "|primary");
            if (m_SecondaryResult != null)
            {
                DrawEvidenceList("对比资源引用依据", GetEvidenceRecords(m_SecondaryResult, dependencyPath), foldoutKeyPrefix + "|secondary");
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawEvidenceList(string title, List<ReferenceEvidenceRecord> evidenceRecords, string foldoutKey)
        {
            int evidenceCount = evidenceRecords != null ? evidenceRecords.Count : 0;
            bool isExpanded = GetEvidenceFoldoutState(foldoutKey);
            isExpanded = EditorGUILayout.Foldout(isExpanded, title + " (" + evidenceCount + ")", true);
            m_EvidenceFoldoutStates[foldoutKey] = isExpanded;

            if (!isExpanded)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");
            if (evidenceCount == 0)
            {
                EditorGUILayout.HelpBox("当前未定位到具体序列化字段引用，可能属于 Unity 隐式依赖、导入依赖或非序列化依赖。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < evidenceRecords.Count; i++)
            {
                ReferenceEvidenceRecord record = evidenceRecords[i];
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("来源资源类型", record.m_SourceAssetKind);
                EditorGUILayout.SelectableLabel(record.m_SourceAssetPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.LabelField("持有对象", record.m_OwnerName + " (" + record.m_OwnerType + ")");
                EditorGUILayout.LabelField("属性路径", record.m_PropertyPath);
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndVertical();
        }

        private bool GetEvidenceFoldoutState(string foldoutKey)
        {
            if (string.IsNullOrEmpty(foldoutKey))
            {
                return false;
            }

            if (m_EvidenceFoldoutStates.TryGetValue(foldoutKey, out bool isExpanded))
            {
                return isExpanded;
            }

            return false;
        }

        private void AnalyzeDependencies()
        {
            if (!TryGetPersistentAssetPath(m_PrimaryAsset, "主资源", out string primaryAssetPath))
            {
                return;
            }

            try
            {
                EditorUtility.DisplayProgressBar(m_WindowTitle, "正在分析主资源依赖...", 0.2f);
                m_PrimaryResult = BuildAnalysisResult(primaryAssetPath);

                m_SecondaryResult = null;
                m_ComparisonResult = null;

                if (m_SecondaryAsset != null)
                {
                    if (!TryGetPersistentAssetPath(m_SecondaryAsset, "对比资源", out string secondaryAssetPath))
                    {
                        return;
                    }

                    EditorUtility.DisplayProgressBar(m_WindowTitle, "正在分析对比资源依赖...", 0.6f);
                    m_SecondaryResult = BuildAnalysisResult(secondaryAssetPath);
                    m_ComparisonResult = BuildComparisonResult(m_PrimaryResult, m_SecondaryResult);
                }

                EditorUtility.DisplayProgressBar(m_WindowTitle, "正在整理结果...", 0.9f);
                m_LastSummary =
                    "分析完成。主资源依赖数: " + (m_PrimaryResult != null ? m_PrimaryResult.m_AllDependencyPaths.Count : 0) +
                    "。" +
                    (m_ComparisonResult != null
                        ? " 重叠依赖数: " + m_ComparisonResult.m_CommonPaths.Count + "。"
                        : string.Empty);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void ClearResults()
        {
            m_PrimaryResult = null;
            m_SecondaryResult = null;
            m_ComparisonResult = null;
            m_LastSummary = string.Empty;
        }

        private AnalysisResult BuildAnalysisResult(string rootAssetPath)
        {
            AnalysisResult result = new AnalysisResult();
            result.m_RootAssetPath = rootAssetPath;
            string[] rawDependencyPaths = AssetDatabase.GetDependencies(rootAssetPath, true);
            for (int i = 0; i < rawDependencyPaths.Length; i++)
            {
                string dependencyPath = rawDependencyPaths[i];
                if (string.IsNullOrEmpty(dependencyPath))
                {
                    continue;
                }

                if (string.Equals(dependencyPath, rootAssetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!ShouldIncludeDependencyPath(dependencyPath))
                {
                    continue;
                }

                result.m_AllDependencyPaths.Add(dependencyPath);
            }

            result.m_SortedDependencyPaths.AddRange(result.m_AllDependencyPaths);
            result.m_SortedDependencyPaths.Sort((left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
            BuildReferenceEvidence(result);
            return result;
        }

        private void BuildReferenceEvidence(AnalysisResult result)
        {
            if (result == null || string.IsNullOrEmpty(result.m_RootAssetPath) || result.m_AllDependencyPaths.Count == 0)
            {
                return;
            }

            List<string> sourceAssetPaths = new List<string>(result.m_SortedDependencyPaths.Count + 1);
            sourceAssetPaths.Add(result.m_RootAssetPath);
            sourceAssetPaths.AddRange(result.m_SortedDependencyPaths);

            for (int i = 0; i < sourceAssetPaths.Count; i++)
            {
                string sourceAssetPath = sourceAssetPaths[i];
                if (IsScenePath(sourceAssetPath))
                {
                    CollectSceneReferenceEvidence(sourceAssetPath, result);
                    continue;
                }

                if (IsPrefabPath(sourceAssetPath))
                {
                    CollectPrefabReferenceEvidence(sourceAssetPath, result);
                }
                else
                {
                    CollectGenericReferenceEvidence(sourceAssetPath, result);
                }

                CollectImporterReferenceEvidence(sourceAssetPath, result);
            }
        }

        private void CollectGenericReferenceEvidence(string assetPath, AnalysisResult result)
        {
            UnityEngine.Object[] assetObjects = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            string assetKind = GetAssetKind(assetPath);
            for (int i = 0; i < assetObjects.Length; i++)
            {
                UnityEngine.Object ownerObject = assetObjects[i];
                if (ownerObject == null)
                {
                    continue;
                }

                CollectObjectReferenceEvidence(ownerObject, assetPath, assetKind, result);
            }
        }

        private void CollectPrefabReferenceEvidence(string assetPath, AnalysisResult result)
        {
            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                List<UnityEngine.Object> hierarchyObjects = new List<UnityEngine.Object>();
                CollectHierarchyObjects(prefabRoot, hierarchyObjects);
                for (int i = 0; i < hierarchyObjects.Count; i++)
                {
                    UnityEngine.Object ownerObject = hierarchyObjects[i];
                    if (ownerObject == null)
                    {
                        continue;
                    }

                    CollectObjectReferenceEvidence(ownerObject, assetPath, "Prefab", result);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        private void CollectSceneReferenceEvidence(string assetPath, AnalysisResult result)
        {
            Scene scene = SceneManager.GetSceneByPath(assetPath);
            bool isOpenedByTool = false;

            if (!scene.IsValid() || !scene.isLoaded)
            {
                scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
                isOpenedByTool = true;
            }

            try
            {
                List<UnityEngine.Object> sceneObjects = new List<UnityEngine.Object>();
                CollectSceneObjects(scene, sceneObjects);
                for (int i = 0; i < sceneObjects.Count; i++)
                {
                    UnityEngine.Object ownerObject = sceneObjects[i];
                    if (ownerObject == null)
                    {
                        continue;
                    }

                    CollectObjectReferenceEvidence(ownerObject, assetPath, "Scene", result);
                }
            }
            finally
            {
                if (isOpenedByTool && scene.IsValid())
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private void CollectImporterReferenceEvidence(string assetPath, AnalysisResult result)
        {
            AssetImporter importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null)
            {
                return;
            }

            CollectObjectReferenceEvidence(importer, assetPath, "Importer", result);
        }

        private void CollectObjectReferenceEvidence(UnityEngine.Object ownerObject, string sourceAssetPath, string sourceAssetKind, AnalysisResult result)
        {
            if (ownerObject == null || result == null)
            {
                return;
            }

            SerializedObject serializedObject;
            try
            {
                serializedObject = new SerializedObject(ownerObject);
            }
            catch
            {
                return;
            }

            SerializedProperty iterator = serializedObject.GetIterator();
            bool enterChildren = true;
            while (iterator.Next(enterChildren))
            {
                enterChildren = true;
                if (iterator.propertyType != SerializedPropertyType.ObjectReference)
                {
                    continue;
                }

                UnityEngine.Object referencedObject = iterator.objectReferenceValue;
                if (referencedObject == null)
                {
                    continue;
                }

                string referencedAssetPath = AssetDatabase.GetAssetPath(referencedObject);
                if (string.IsNullOrEmpty(referencedAssetPath))
                {
                    continue;
                }

                if (!result.m_AllDependencyPaths.Contains(referencedAssetPath))
                {
                    continue;
                }

                AddEvidenceRecord(result, referencedAssetPath, sourceAssetPath, sourceAssetKind, ownerObject, iterator.propertyPath);
            }
        }

        private void AddEvidenceRecord(
            AnalysisResult result,
            string dependencyPath,
            string sourceAssetPath,
            string sourceAssetKind,
            UnityEngine.Object ownerObject,
            string propertyPath)
        {
            if (result == null || string.IsNullOrEmpty(dependencyPath))
            {
                return;
            }

            if (!result.m_EvidenceByDependencyPath.TryGetValue(dependencyPath, out List<ReferenceEvidenceRecord> evidenceRecords))
            {
                evidenceRecords = new List<ReferenceEvidenceRecord>();
                result.m_EvidenceByDependencyPath.Add(dependencyPath, evidenceRecords);
            }

            for (int i = 0; i < evidenceRecords.Count; i++)
            {
                ReferenceEvidenceRecord existingRecord = evidenceRecords[i];
                if (string.Equals(existingRecord.m_SourceAssetPath, sourceAssetPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existingRecord.m_OwnerName, ownerObject != null ? ownerObject.name : string.Empty, StringComparison.Ordinal) &&
                    string.Equals(existingRecord.m_OwnerType, ownerObject != null ? ownerObject.GetType().Name : string.Empty, StringComparison.Ordinal) &&
                    string.Equals(existingRecord.m_PropertyPath, propertyPath, StringComparison.Ordinal))
                {
                    return;
                }
            }

            ReferenceEvidenceRecord record = new ReferenceEvidenceRecord();
            record.m_SourceAssetPath = sourceAssetPath;
            record.m_SourceAssetKind = sourceAssetKind;
            record.m_OwnerName = ownerObject != null ? ownerObject.name : "Unknown";
            record.m_OwnerType = ownerObject != null ? ownerObject.GetType().Name : "Unknown";
            record.m_PropertyPath = propertyPath;
            evidenceRecords.Add(record);
        }

        private List<ReferenceEvidenceRecord> GetEvidenceRecords(AnalysisResult result, string dependencyPath)
        {
            if (result == null || string.IsNullOrEmpty(dependencyPath))
            {
                return null;
            }

            if (result.m_EvidenceByDependencyPath.TryGetValue(dependencyPath, out List<ReferenceEvidenceRecord> evidenceRecords))
            {
                return evidenceRecords;
            }

            return null;
        }

        private bool ShouldIncludeDependencyPath(string dependencyPath)
        {
            if (m_OnlyIncludeAssetsFolder && !dependencyPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (AssetDatabase.IsValidFolder(dependencyPath))
            {
                return false;
            }

            if (!m_IgnoreCodeAssets)
            {
                return !ShouldExcludeByUserFilter(dependencyPath);
            }

            string extension = Path.GetExtension(dependencyPath);
            if (string.IsNullOrEmpty(extension))
            {
                return !ShouldExcludeByUserFilter(dependencyPath);
            }

            if (m_IgnoredExtensions.Contains(extension))
            {
                return false;
            }

            return !ShouldExcludeByUserFilter(dependencyPath);
        }

        private bool ShouldExcludeByUserFilter(string dependencyPath)
        {
            if (string.IsNullOrEmpty(dependencyPath))
            {
                return false;
            }

            string extension = Path.GetExtension(dependencyPath);
            string normalizedExtension = string.IsNullOrEmpty(extension) ? string.Empty : extension.ToLowerInvariant();

            if (m_ExcludeTextureAssets && IsTextureAssetPath(dependencyPath, normalizedExtension))
            {
                return true;
            }

            if (m_ExcludeMaterialAssets && string.Equals(normalizedExtension, ".mat", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (m_ExcludeShaderAssets && IsShaderAssetPath(normalizedExtension))
            {
                return true;
            }

            if (m_ExcludeModelAssets && IsModelAssetPath(normalizedExtension))
            {
                return true;
            }

            if (m_ExcludePrefabAssets && string.Equals(normalizedExtension, ".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (m_ExcludeSceneAssets && string.Equals(normalizedExtension, ".unity", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (m_ExcludeAnimationAssets && IsAnimationAssetPath(normalizedExtension))
            {
                return true;
            }

            if (m_ExcludeAudioAssets && IsAudioAssetPath(normalizedExtension))
            {
                return true;
            }

            return IsCustomExcludedExtension(normalizedExtension);
        }

        private bool IsCustomExcludedExtension(string normalizedExtension)
        {
            if (string.IsNullOrWhiteSpace(normalizedExtension) || string.IsNullOrWhiteSpace(m_CustomExcludedExtensions))
            {
                return false;
            }

            string[] tokens = m_CustomExcludedExtensions.Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }

                if (!token.StartsWith(".", StringComparison.Ordinal))
                {
                    token = "." + token;
                }

                if (string.Equals(token, normalizedExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsTextureAssetPath(string dependencyPath, string normalizedExtension)
        {
            if (string.IsNullOrEmpty(dependencyPath))
            {
                return false;
            }

            if (normalizedExtension == ".png" ||
                normalizedExtension == ".jpg" ||
                normalizedExtension == ".jpeg" ||
                normalizedExtension == ".tga" ||
                normalizedExtension == ".psd" ||
                normalizedExtension == ".tif" ||
                normalizedExtension == ".tiff" ||
                normalizedExtension == ".exr" ||
                normalizedExtension == ".hdr" ||
                normalizedExtension == ".bmp" ||
                normalizedExtension == ".gif" ||
                normalizedExtension == ".iff" ||
                normalizedExtension == ".pict" ||
                normalizedExtension == ".dds" ||
                normalizedExtension == ".ktx" ||
                normalizedExtension == ".rendertexture")
            {
                return true;
            }

            Type mainAssetType = AssetDatabase.GetMainAssetTypeAtPath(dependencyPath);
            return mainAssetType != null && typeof(Texture).IsAssignableFrom(mainAssetType);
        }

        private static bool IsShaderAssetPath(string normalizedExtension)
        {
            return normalizedExtension == ".shader" ||
                   normalizedExtension == ".shadergraph" ||
                   normalizedExtension == ".shadersubgraph" ||
                   normalizedExtension == ".compute" ||
                   normalizedExtension == ".cginc" ||
                   normalizedExtension == ".hlsl";
        }

        private static bool IsModelAssetPath(string normalizedExtension)
        {
            return normalizedExtension == ".fbx" ||
                   normalizedExtension == ".obj" ||
                   normalizedExtension == ".dae" ||
                   normalizedExtension == ".3ds" ||
                   normalizedExtension == ".dxf" ||
                   normalizedExtension == ".blend" ||
                   normalizedExtension == ".mb" ||
                   normalizedExtension == ".ma" ||
                   normalizedExtension == ".max";
        }

        private static bool IsAnimationAssetPath(string normalizedExtension)
        {
            return normalizedExtension == ".anim" ||
                   normalizedExtension == ".controller" ||
                   normalizedExtension == ".overridecontroller" ||
                   normalizedExtension == ".playable";
        }

        private static bool IsAudioAssetPath(string normalizedExtension)
        {
            return normalizedExtension == ".wav" ||
                   normalizedExtension == ".mp3" ||
                   normalizedExtension == ".ogg" ||
                   normalizedExtension == ".aif" ||
                   normalizedExtension == ".aiff" ||
                   normalizedExtension == ".xm" ||
                   normalizedExtension == ".mod" ||
                   normalizedExtension == ".it" ||
                   normalizedExtension == ".s3m";
        }

        private ComparisonResult BuildComparisonResult(AnalysisResult primaryResult, AnalysisResult secondaryResult)
        {
            if (primaryResult == null || secondaryResult == null)
            {
                return null;
            }

            ComparisonResult result = new ComparisonResult();

            foreach (string primaryPath in primaryResult.m_AllDependencyPaths)
            {
                if (secondaryResult.m_AllDependencyPaths.Contains(primaryPath))
                {
                    result.m_CommonPaths.Add(primaryPath);
                }
                else
                {
                    result.m_OnlyPrimaryPaths.Add(primaryPath);
                }
            }

            foreach (string secondaryPath in secondaryResult.m_AllDependencyPaths)
            {
                if (!primaryResult.m_AllDependencyPaths.Contains(secondaryPath))
                {
                    result.m_OnlySecondaryPaths.Add(secondaryPath);
                }
            }

            result.m_CommonPaths.Sort((left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
            result.m_OnlyPrimaryPaths.Sort((left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
            result.m_OnlySecondaryPaths.Sort((left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private bool TryGetPersistentAssetPath(UnityEngine.Object assetObject, string fieldName, out string assetPath)
        {
            assetPath = string.Empty;
            if (assetObject == null)
            {
                EditorUtility.DisplayDialog("提示", "请先指定" + fieldName + "。", "确定");
                return false;
            }

            assetPath = AssetDatabase.GetAssetPath(assetObject);
            if (string.IsNullOrEmpty(assetPath))
            {
                EditorUtility.DisplayDialog("提示", fieldName + "必须是 Project 视图中的持久化资源。", "确定");
                return false;
            }

            if (AssetDatabase.IsValidFolder(assetPath))
            {
                EditorUtility.DisplayDialog("提示", fieldName + "暂不支持文件夹，请选择具体资源。", "确定");
                return false;
            }

            return true;
        }

        private void PingAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            UnityEngine.Object assetObject = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (assetObject == null)
            {
                EditorUtility.DisplayDialog("提示", "未能定位该资源，可能该资源已被删除或路径已失效。", "确定");
                return;
            }

            Selection.activeObject = assetObject;
            EditorGUIUtility.PingObject(assetObject);
        }

        private static string GetAssetTypeName(string assetPath)
        {
            string extension = Path.GetExtension(assetPath);
            if (string.IsNullOrEmpty(extension))
            {
                return "Unknown";
            }

            return extension.TrimStart('.').ToUpperInvariant();
        }

        private static void CollectSceneObjects(Scene scene, List<UnityEngine.Object> result)
        {
            GameObject[] rootObjects = scene.GetRootGameObjects();
            for (int i = 0; i < rootObjects.Length; i++)
            {
                CollectHierarchyObjects(rootObjects[i], result);
            }
        }

        private static void CollectHierarchyObjects(GameObject root, List<UnityEngine.Object> result)
        {
            if (root == null)
            {
                return;
            }

            Stack<Transform> stack = new Stack<Transform>();
            stack.Push(root.transform);
            while (stack.Count > 0)
            {
                Transform current = stack.Pop();
                if (current == null)
                {
                    continue;
                }

                result.Add(current.gameObject);

                Component[] components = current.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] != null)
                    {
                        result.Add(components[i]);
                    }
                }

                for (int i = current.childCount - 1; i >= 0; i--)
                {
                    stack.Push(current.GetChild(i));
                }
            }
        }

        private static string GetAssetKind(string assetPath)
        {
            if (IsScenePath(assetPath))
            {
                return "Scene";
            }

            if (IsPrefabPath(assetPath))
            {
                return "Prefab";
            }

            string extension = Path.GetExtension(assetPath);
            if (string.IsNullOrEmpty(extension))
            {
                return "Asset";
            }

            return extension.TrimStart('.').ToUpperInvariant();
        }

        private static bool IsScenePath(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPrefabPath(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }
    }
}
