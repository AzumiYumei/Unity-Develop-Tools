// ================================================================
// Prefab AB打包预览工具
//
// 文件名称: PrefabAssetBundlePreviewWindow.cs
// 版本: 1.2
//
// 描述:
// 该工具用于预览一个或多个 Prefab 作为同一组根资源参与打包时，会打入哪个 AB 包，
// 以及它们的依赖资源中，哪些会随当前包一起打入，哪些会落入其他 AB 包。
// 工具的规则与项目中 BuildHelper.BuildAssetBundle() 当前使用的分包逻辑保持一致，
// 适合在正式打包前快速核对资源归属，避免误判某些依赖会不会被打进当前包。
//
// 使用方式:
// 1. 打开菜单 Tools/Azumi/资源工具/Prefab AB打包预览。
// 2. 在“目标 Prefab 列表”中添加一个或多个 Project 视图中的 Prefab 资源。
// 3. 如有需要，可填写一个“预览包名”用于表示这组 Prefab 会一起打入的目标包。
// 4. 点击“分析打包结果”后，查看当前包名、包内资源、外部依赖包和未纳入规则的说明。
// 5. 若某些 Prefab 不在当前 BuildHelper 的打包规则覆盖范围内，工具会给出明确提示。
//
// 注意事项:
// 1. 本工具按当前 BuildHelper 中写死的打包规则推导结果，而不是直接修改或触发正式打包。
// 2. 对于未被单独分配到其他 AB 的依赖资源，工具会认为其将随当前根资源集合一起打入当前包。
// 3. 若后续 BuildHelper 的分包规则发生变化，需要同步更新本工具中的规则映射逻辑。
//
// 修改历史:
// 2026-06-29 - 初始版本
// 2026-06-29 - 新增两个 AB 包内容重叠对比功能
// 2026-06-29 - 调整为支持两组 Prefab 打包内容重叠对比
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using EPT.Editor;
using UnityEditor;
using UnityEngine;

namespace AzumisEditorTools
{
    public class PrefabAssetBundlePreviewWindow : EditorWindow
    {
        private sealed class DependencyRecord
        {
            public string m_AssetPath = string.Empty;
            public string m_TargetBundleName = string.Empty;
            public string m_AssignedBundleName = string.Empty;
            public string m_Reason = string.Empty;
            public bool m_IsPackedIntoCurrentBundle;
        }

        private sealed class ExternalBundleRecord
        {
            public string m_BundleName = string.Empty;
            public readonly List<string> m_AssetPaths = new List<string>();
            public bool m_IsExpanded = true;
        }

        private sealed class PreviewResult
        {
            public readonly List<string> m_TargetPrefabPaths = new List<string>();
            public string m_TargetBundleName = string.Empty;
            public int m_CoveredTargetPrefabCount;
            public int m_TotalTargetPrefabCount;
            public readonly List<DependencyRecord> m_PackedIntoCurrentBundle = new List<DependencyRecord>();
            public readonly List<DependencyRecord> m_PackedIntoOtherBundle = new List<DependencyRecord>();
            public readonly List<ExternalBundleRecord> m_ExternalBundles = new List<ExternalBundleRecord>();
        }

        private sealed class BundleCompareResult
        {
            public string m_GroupLabelA = string.Empty;
            public string m_GroupLabelB = string.Empty;
            public string m_TargetBundleNameA = string.Empty;
            public string m_TargetBundleNameB = string.Empty;
            public readonly List<string> m_OverlapAssetPaths = new List<string>();
            public readonly List<string> m_OnlyInBundleAAssetPaths = new List<string>();
            public readonly List<string> m_OnlyInBundleBAssetPaths = new List<string>();
            public readonly List<string> m_BundleAAssetPaths = new List<string>();
            public readonly List<string> m_BundleBAssetPaths = new List<string>();
        }

        private const string m_WindowTitle = "Prefab AB打包预览";
        private const string m_MenuPath = "Tools/Azumi/资源工具/Prefab AB打包预览";
        private const string m_CompareGroupABundleName = "preview/compare/group_a.ab";
        private const string m_CompareGroupBBundleName = "preview/compare/group_b.ab";

        private string m_PreviewBundleName = string.Empty;
        private readonly List<GameObject> m_TargetPrefabs = new List<GameObject>();
        private readonly List<GameObject> m_ComparePrefabsA = new List<GameObject>();
        private readonly List<GameObject> m_ComparePrefabsB = new List<GameObject>();
        private bool m_OnlyIncludeAssetsFolder = true;
        private bool m_IsCurrentBundleListExpanded = true;
        private bool m_IsOtherBundleListExpanded = true;
        private bool m_IsExternalBundleListExpanded = true;
        private bool m_IsBundleCompareSectionExpanded = true;
        private bool m_IsComparePrefabListAExpanded = true;
        private bool m_IsComparePrefabListBExpanded = true;
        private bool m_IsOverlapAssetListExpanded = true;
        private bool m_IsOnlyBundleAAssetListExpanded = true;
        private bool m_IsOnlyBundleBAssetListExpanded = true;
        private Vector2 m_ScrollPosition;
        private string m_LastSummary = string.Empty;
        private string m_LastCompareSummary = string.Empty;
        private PreviewResult m_PreviewResult;
        private BundleCompareResult m_BundleCompareResult;

        [MenuItem(m_MenuPath)]
        public static void ShowWindow()
        {
            PrefabAssetBundlePreviewWindow window = GetWindow<PrefabAssetBundlePreviewWindow>(m_WindowTitle);
            window.minSize = new Vector2(900f, 620f);
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
            DrawCurrentBundleSection();
            EditorGUILayout.Space(10f);
            DrawOtherBundleSection();
            EditorGUILayout.Space(10f);
            DrawExternalBundleSection();
            EditorGUILayout.Space(10f);
            DrawBundleCompareSection();
            EditorGUILayout.EndScrollView();
        }

        // 输入区用于选择目标 Prefab 集合，并控制是否只统计 Assets 目录中的项目资源。
        private void DrawInputSection()
        {
            EditorGUILayout.LabelField("分析设置", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            DrawTargetPrefabList();
            m_PreviewBundleName = EditorGUILayout.TextField("预览包名", m_PreviewBundleName);
            m_OnlyIncludeAssetsFolder = EditorGUILayout.ToggleLeft("仅统计 Assets 目录中的资源", m_OnlyIncludeAssetsFolder);

            EditorGUILayout.HelpBox(
                "工具会基于项目当前 BuildHelper 的打包规则，推导目标 Prefab 集合所属 AB 包，以及依赖资源最终会落入哪个包。" +
                "若依赖资源没有被其他规则单独分包，则会被视为随当前根资源集合一起打入当前包。",
                MessageType.Info);

            EditorGUILayout.EndVertical();
        }

        private void DrawTargetPrefabList()
        {
            DrawPrefabListEditor("目标 Prefab 列表", "Prefab", m_TargetPrefabs, "当前还没有目标 Prefab，请添加一个或多个 Prefab。", "清空目标");
        }

        private void DrawActionSection()
        {
            EditorGUILayout.LabelField("操作", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("分析打包结果", GUILayout.Height(28f)))
                {
                    AnalyzeTargetPrefab();
                }

                if (GUILayout.Button("清空结果", GUILayout.Height(28f)))
                {
                    ClearResult();
                }
            }

            if (!string.IsNullOrEmpty(m_LastSummary))
            {
                EditorGUILayout.HelpBox(m_LastSummary, MessageType.None);
            }

            EditorGUILayout.EndVertical();
        }

        // 对比区用于分别选择两组 Prefab，并对比这两组资源各自参与打包时最终包内容是否重叠。
        private void DrawBundleCompareSection()
        {
            m_IsBundleCompareSectionExpanded = EditorGUILayout.Foldout(
                m_IsBundleCompareSectionExpanded,
                "Prefab 组内容重叠对比",
                true);

            if (!m_IsBundleCompareSectionExpanded)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");
            DrawPrefabListEditor(
                "Prefab组 A",
                "A",
                m_ComparePrefabsA,
                "Prefab组 A 还没有资源，请添加一个或多个 Prefab。",
                "清空 A",
                ref m_IsComparePrefabListAExpanded);
            DrawPrefabListEditor(
                "Prefab组 B",
                "B",
                m_ComparePrefabsB,
                "Prefab组 B 还没有资源，请添加一个或多个 Prefab。",
                "清空 B",
                ref m_IsComparePrefabListBExpanded);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("对比两组 Prefab", GUILayout.Height(24f)))
                {
                    AnalyzePrefabGroupOverlap();
                }

                if (GUILayout.Button("清空对比", GUILayout.Height(24f)))
                {
                    ClearCompareResult();
                }
            }

            EditorGUILayout.HelpBox(
                "该功能会分别把 Prefab组 A 与 Prefab组 B 作为两次独立的打包输入，基于当前 BuildHelper 规则推导它们各自最终会进入当前包的资源集合，" +
                "然后给出交集、A 独有、B 独有资源列表。",
                MessageType.Info);

            if (!string.IsNullOrEmpty(m_LastCompareSummary))
            {
                EditorGUILayout.HelpBox(m_LastCompareSummary, MessageType.None);
            }

            if (m_BundleCompareResult == null)
            {
                EditorGUILayout.HelpBox("请先为 Prefab组 A 和 Prefab组 B 各添加至少一个 Prefab，然后执行对比。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.LabelField(m_BundleCompareResult.m_GroupLabelA + " 预览包名", m_BundleCompareResult.m_TargetBundleNameA);
            EditorGUILayout.LabelField(m_BundleCompareResult.m_GroupLabelB + " 预览包名", m_BundleCompareResult.m_TargetBundleNameB);
            EditorGUILayout.LabelField(m_BundleCompareResult.m_GroupLabelA + " 资源数", m_BundleCompareResult.m_BundleAAssetPaths.Count.ToString());
            EditorGUILayout.LabelField(m_BundleCompareResult.m_GroupLabelB + " 资源数", m_BundleCompareResult.m_BundleBAssetPaths.Count.ToString());
            EditorGUILayout.LabelField("重叠资源数", m_BundleCompareResult.m_OverlapAssetPaths.Count.ToString());
            DrawAssetPathListFoldout(
                ref m_IsOverlapAssetListExpanded,
                "重叠资源",
                m_BundleCompareResult.m_OverlapAssetPaths,
                "当前两组 Prefab 推导出的包内容没有重叠。");
            DrawAssetPathListFoldout(
                ref m_IsOnlyBundleAAssetListExpanded,
                "仅 Prefab组 A 拥有的资源",
                m_BundleCompareResult.m_OnlyInBundleAAssetPaths,
                "当前没有仅属于 Prefab组 A 的资源。");
            DrawAssetPathListFoldout(
                ref m_IsOnlyBundleBAssetListExpanded,
                "仅 Prefab组 B 拥有的资源",
                m_BundleCompareResult.m_OnlyInBundleBAssetPaths,
                "当前没有仅属于 Prefab组 B 的资源。");
            EditorGUILayout.EndVertical();
        }

        private void DrawPrefabListEditor(
            string title,
            string itemPrefix,
            List<GameObject> prefabList,
            string emptyMessage,
            string clearButtonText)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            DrawPrefabListEditorContent(itemPrefix, prefabList, emptyMessage, clearButtonText);
            EditorGUILayout.EndVertical();
        }

        private void DrawPrefabListEditor(
            string title,
            string itemPrefix,
            List<GameObject> prefabList,
            string emptyMessage,
            string clearButtonText,
            ref bool isExpanded)
        {
            isExpanded = EditorGUILayout.Foldout(isExpanded, title, true);
            if (!isExpanded)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");
            DrawPrefabListEditorContent(itemPrefix, prefabList, emptyMessage, clearButtonText);
            EditorGUILayout.EndVertical();
        }

        private void DrawPrefabListEditorContent(
            string itemPrefix,
            List<GameObject> prefabList,
            string emptyMessage,
            string clearButtonText)
        {
            if (prefabList == null || prefabList.Count == 0)
            {
                EditorGUILayout.HelpBox(emptyMessage, MessageType.Info);
            }

            if (prefabList != null)
            {
                for (int i = 0; i < prefabList.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        prefabList[i] = (GameObject)EditorGUILayout.ObjectField(
                            itemPrefix + " " + (i + 1),
                            prefabList[i],
                            typeof(GameObject),
                            false);

                        if (GUILayout.Button("删除", GUILayout.Width(56f)))
                        {
                            prefabList.RemoveAt(i);
                            i--;
                        }
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("添加空项", GUILayout.Height(24f)))
                {
                    prefabList.Add(null);
                }

                if (GUILayout.Button("添加当前选择", GUILayout.Height(24f)))
                {
                    AddSelectedPrefabs(prefabList);
                }

                if (GUILayout.Button(clearButtonText, GUILayout.Height(24f)))
                {
                    prefabList.Clear();
                }
            }
        }

        private void DrawSummarySection()
        {
            EditorGUILayout.LabelField("汇总信息", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.LabelField("目标 Prefab 数量", m_TargetPrefabs.Count.ToString());

            if (m_PreviewResult == null)
            {
                EditorGUILayout.LabelField("当前包名", "未分析");
                EditorGUILayout.LabelField("当前包内资源数", "0");
                EditorGUILayout.LabelField("其他包内资源数", "0");
                EditorGUILayout.LabelField("外部依赖包数", "0");
                EditorGUILayout.LabelField("命中打包规则的目标数", "0");
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.LabelField(
                "当前包名",
                string.IsNullOrEmpty(m_PreviewResult.m_TargetBundleName) ? "未命中当前打包规则" : m_PreviewResult.m_TargetBundleName);
            EditorGUILayout.LabelField("当前包内资源数", m_PreviewResult.m_PackedIntoCurrentBundle.Count.ToString());
            EditorGUILayout.LabelField("其他包内资源数", m_PreviewResult.m_PackedIntoOtherBundle.Count.ToString());
            EditorGUILayout.LabelField("外部依赖包数", m_PreviewResult.m_ExternalBundles.Count.ToString());
            EditorGUILayout.LabelField(
                "命中打包规则的目标数",
                m_PreviewResult.m_CoveredTargetPrefabCount + " / " + m_PreviewResult.m_TotalTargetPrefabCount);

            EditorGUILayout.EndVertical();
        }

        // 当前包区展示最终会和目标 Prefab 一起打入当前 AB 包的资源集合。
        private void DrawCurrentBundleSection()
        {
            m_IsCurrentBundleListExpanded = EditorGUILayout.Foldout(
                m_IsCurrentBundleListExpanded,
                "当前包内资源",
                true);

            if (!m_IsCurrentBundleListExpanded)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");
            if (m_PreviewResult == null)
            {
                EditorGUILayout.HelpBox("当前没有可显示的结果，请先执行分析。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            if (m_PreviewResult.m_PackedIntoCurrentBundle.Count == 0)
            {
                EditorGUILayout.HelpBox("当前列表为空。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < m_PreviewResult.m_PackedIntoCurrentBundle.Count; i++)
            {
                DrawDependencyRecord(m_PreviewResult.m_PackedIntoCurrentBundle[i]);
            }

            EditorGUILayout.EndVertical();
        }

        // 其他包区展示依赖中已被项目规则分配到其他 AB 包的资源。
        private void DrawOtherBundleSection()
        {
            m_IsOtherBundleListExpanded = EditorGUILayout.Foldout(
                m_IsOtherBundleListExpanded,
                "其他包内资源",
                true);

            if (!m_IsOtherBundleListExpanded)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");
            if (m_PreviewResult == null)
            {
                EditorGUILayout.HelpBox("当前没有可显示的结果，请先执行分析。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            if (m_PreviewResult.m_PackedIntoOtherBundle.Count == 0)
            {
                EditorGUILayout.HelpBox("当前没有资源被打入其他 AB 包。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < m_PreviewResult.m_PackedIntoOtherBundle.Count; i++)
            {
                DrawDependencyRecord(m_PreviewResult.m_PackedIntoOtherBundle[i]);
            }

            EditorGUILayout.EndVertical();
        }

        // 外部依赖包区用于从 AB 维度汇总目标 Prefab 需要依赖的其他包。
        private void DrawExternalBundleSection()
        {
            m_IsExternalBundleListExpanded = EditorGUILayout.Foldout(
                m_IsExternalBundleListExpanded,
                "外部依赖包",
                true);

            if (!m_IsExternalBundleListExpanded)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");
            if (m_PreviewResult == null)
            {
                EditorGUILayout.HelpBox("当前没有可显示的结果，请先执行分析。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            if (m_PreviewResult.m_ExternalBundles.Count == 0)
            {
                EditorGUILayout.HelpBox("当前没有外部依赖包。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < m_PreviewResult.m_ExternalBundles.Count; i++)
            {
                DrawExternalBundleRecord(m_PreviewResult.m_ExternalBundles[i]);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawDependencyRecord(DependencyRecord record)
        {
            if (record == null)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("归属包", record.m_TargetBundleName);
            if (!string.IsNullOrEmpty(record.m_AssignedBundleName) &&
                !string.Equals(record.m_TargetBundleName, record.m_AssignedBundleName, StringComparison.OrdinalIgnoreCase))
            {
                EditorGUILayout.LabelField("规则指定包", record.m_AssignedBundleName);
            }

            EditorGUILayout.SelectableLabel(record.m_AssetPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.LabelField("判定说明", record.m_Reason);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("定位资源", GUILayout.Width(96f)))
                {
                    PingAsset(record.m_AssetPath);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawExternalBundleRecord(ExternalBundleRecord record)
        {
            if (record == null)
            {
                return;
            }

            record.m_IsExpanded = EditorGUILayout.Foldout(
                record.m_IsExpanded,
                record.m_BundleName + " (" + record.m_AssetPaths.Count + ")",
                true);

            if (!record.m_IsExpanded)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");
            for (int i = 0; i < record.m_AssetPaths.Count; i++)
            {
                string assetPath = record.m_AssetPaths[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.SelectableLabel(assetPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    if (GUILayout.Button("定位", GUILayout.Width(56f)))
                    {
                        PingAsset(assetPath);
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawAssetPathListFoldout(ref bool isExpanded, string title, List<string> assetPaths, string emptyMessage)
        {
            int assetCount = assetPaths == null ? 0 : assetPaths.Count;
            isExpanded = EditorGUILayout.Foldout(isExpanded, title + " (" + assetCount + ")", true);
            if (!isExpanded)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");
            if (assetPaths == null || assetPaths.Count == 0)
            {
                EditorGUILayout.HelpBox(emptyMessage, MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < assetPaths.Count; i++)
            {
                string assetPath = assetPaths[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.SelectableLabel(assetPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    if (GUILayout.Button("定位", GUILayout.Width(56f)))
                    {
                        PingAsset(assetPath);
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void AnalyzeTargetPrefab()
        {
            if (!TryGetValidPrefabPaths(m_TargetPrefabs, "请先指定至少一个目标 Prefab。", out List<string> prefabPaths))
            {
                return;
            }

            Dictionary<string, string> assetBundleMap = BuildAssetBundleRuleMap();
            PreviewResult result = BuildPreviewResult(prefabPaths, assetBundleMap, m_PreviewBundleName);
            m_PreviewResult = result;
            m_LastSummary =
                "分析完成。根 Prefab 数: " + result.m_TotalTargetPrefabCount +
                "，当前包内资源数: " + result.m_PackedIntoCurrentBundle.Count +
                "，其他包内资源数: " + result.m_PackedIntoOtherBundle.Count +
                "，外部依赖包数: " + result.m_ExternalBundles.Count + "。";

            if (result.m_CoveredTargetPrefabCount < result.m_TotalTargetPrefabCount)
            {
                m_LastSummary += " 注意：部分目标 Prefab 未命中当前 BuildHelper 中的显式打包规则。";
            }
        }

        private void AnalyzePrefabGroupOverlap()
        {
            if (!TryGetValidPrefabPaths(m_ComparePrefabsA, "请先为 Prefab组 A 指定至少一个 Prefab。", out List<string> prefabPathsA) ||
                !TryGetValidPrefabPaths(m_ComparePrefabsB, "请先为 Prefab组 B 指定至少一个 Prefab。", out List<string> prefabPathsB))
            {
                return;
            }

            Dictionary<string, string> assetBundleMap = BuildAssetBundleRuleMap();
            PreviewResult groupAResult = BuildPreviewResult(prefabPathsA, assetBundleMap, m_CompareGroupABundleName);
            PreviewResult groupBResult = BuildPreviewResult(prefabPathsB, assetBundleMap, m_CompareGroupBBundleName);
            HashSet<string> bundleAAssetSet = CollectCurrentBundleAssetPaths(groupAResult);
            HashSet<string> bundleBAssetSet = CollectCurrentBundleAssetPaths(groupBResult);

            BundleCompareResult result = new BundleCompareResult();
            result.m_GroupLabelA = "Prefab组 A";
            result.m_GroupLabelB = "Prefab组 B";
            result.m_TargetBundleNameA = groupAResult.m_TargetBundleName;
            result.m_TargetBundleNameB = groupBResult.m_TargetBundleName;
            result.m_BundleAAssetPaths.AddRange(bundleAAssetSet);
            result.m_BundleBAssetPaths.AddRange(bundleBAssetSet);

            foreach (string assetPath in bundleAAssetSet)
            {
                if (bundleBAssetSet.Contains(assetPath))
                {
                    result.m_OverlapAssetPaths.Add(assetPath);
                }
                else
                {
                    result.m_OnlyInBundleAAssetPaths.Add(assetPath);
                }
            }

            foreach (string assetPath in bundleBAssetSet)
            {
                if (!bundleAAssetSet.Contains(assetPath))
                {
                    result.m_OnlyInBundleBAssetPaths.Add(assetPath);
                }
            }

            SortAssetPathList(result.m_BundleAAssetPaths);
            SortAssetPathList(result.m_BundleBAssetPaths);
            SortAssetPathList(result.m_OverlapAssetPaths);
            SortAssetPathList(result.m_OnlyInBundleAAssetPaths);
            SortAssetPathList(result.m_OnlyInBundleBAssetPaths);

            m_BundleCompareResult = result;
            m_LastCompareSummary =
                "对比完成。Prefab组 A 资源数: " + result.m_BundleAAssetPaths.Count +
                "，Prefab组 B 资源数: " + result.m_BundleBAssetPaths.Count +
                "，重叠资源数: " + result.m_OverlapAssetPaths.Count + "。";
        }

        private void ClearResult()
        {
            m_PreviewResult = null;
            m_LastSummary = string.Empty;
        }

        private void ClearCompareResult()
        {
            m_BundleCompareResult = null;
            m_LastCompareSummary = string.Empty;
        }

        private string ResolvePreviewBundleName(List<string> prefabPaths, Dictionary<string, string> assetBundleMap)
        {
            if (!string.IsNullOrWhiteSpace(m_PreviewBundleName))
            {
                return NormalizeBundleName(m_PreviewBundleName);
            }

            if (prefabPaths != null && prefabPaths.Count == 1)
            {
                string singlePrefabPath = prefabPaths[0];
                if (!string.IsNullOrEmpty(singlePrefabPath) && assetBundleMap.TryGetValue(singlePrefabPath, out string singleBundleName))
                {
                    return singleBundleName;
                }

                return "preview/custom/single_prefab.ab";
            }

            return "preview/custom/multi_prefabs.ab";
        }

        private bool TryGetValidPrefabPaths(List<GameObject> prefabObjects, string emptyMessage, out List<string> prefabPaths)
        {
            prefabPaths = new List<string>();
            HashSet<string> uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (prefabObjects == null)
            {
                EditorUtility.DisplayDialog("提示", emptyMessage, "确定");
                return false;
            }

            for (int i = 0; i < prefabObjects.Count; i++)
            {
                GameObject prefabObject = prefabObjects[i];
                if (prefabObject == null)
                {
                    continue;
                }

                string prefabPath = AssetDatabase.GetAssetPath(prefabObject);
                if (string.IsNullOrEmpty(prefabPath))
                {
                    continue;
                }

                if (!prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (uniquePaths.Add(prefabPath))
                {
                    prefabPaths.Add(prefabPath);
                }
            }

            if (prefabPaths.Count == 0)
            {
                EditorUtility.DisplayDialog("提示", emptyMessage, "确定");
                return false;
            }

            return true;
        }

        private void AddSelectedPrefabs(List<GameObject> targetPrefabList)
        {
            if (targetPrefabList == null)
            {
                return;
            }

            UnityEngine.Object[] selectedObjects = Selection.objects;
            if (selectedObjects == null || selectedObjects.Length == 0)
            {
                return;
            }

            HashSet<string> existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targetPrefabList.Count; i++)
            {
                GameObject existingPrefab = targetPrefabList[i];
                if (existingPrefab == null)
                {
                    continue;
                }

                string existingPath = AssetDatabase.GetAssetPath(existingPrefab);
                if (!string.IsNullOrEmpty(existingPath))
                {
                    existingPaths.Add(existingPath);
                }
            }

            for (int i = 0; i < selectedObjects.Length; i++)
            {
                GameObject selectedPrefab = selectedObjects[i] as GameObject;
                if (selectedPrefab == null)
                {
                    continue;
                }

                string prefabPath = AssetDatabase.GetAssetPath(selectedPrefab);
                if (string.IsNullOrEmpty(prefabPath) || !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (existingPaths.Add(prefabPath))
                {
                    targetPrefabList.Add(selectedPrefab);
                }
            }
        }

        // 该方法会把一组 Prefab 按当前 BuildHelper 的规则执行一次完整的预览分析，返回其当前包、其他包和外部依赖包结果。
        private PreviewResult BuildPreviewResult(List<string> prefabPaths, Dictionary<string, string> assetBundleMap, string previewBundleNameOverride)
        {
            PreviewResult result = new PreviewResult();
            result.m_TargetPrefabPaths.AddRange(prefabPaths);
            result.m_TotalTargetPrefabCount = prefabPaths.Count;
            HashSet<string> rootPrefabPathSet = new HashSet<string>(prefabPaths, StringComparer.OrdinalIgnoreCase);
            HashSet<string> rootAssignedBundleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < prefabPaths.Count; i++)
            {
                string rootPrefabPath = prefabPaths[i];
                if (assetBundleMap.TryGetValue(rootPrefabPath, out string rootAssignedBundleName) && !string.IsNullOrEmpty(rootAssignedBundleName))
                {
                    result.m_CoveredTargetPrefabCount++;
                    rootAssignedBundleNames.Add(rootAssignedBundleName);
                }
            }

            result.m_TargetBundleName = ResolvePreviewBundleName(prefabPaths, assetBundleMap, previewBundleNameOverride);

            HashSet<string> dependencyPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < prefabPaths.Count; i++)
            {
                string[] prefabDependencyPaths = AssetDatabase.GetDependencies(prefabPaths[i], true);
                for (int j = 0; j < prefabDependencyPaths.Length; j++)
                {
                    dependencyPathSet.Add(prefabDependencyPaths[j]);
                }
            }

            Dictionary<string, ExternalBundleRecord> externalBundleMap = new Dictionary<string, ExternalBundleRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (string dependencyPath in dependencyPathSet)
            {
                if (!ShouldIncludeDependencyPath(dependencyPath))
                {
                    continue;
                }

                if (rootPrefabPathSet.Contains(dependencyPath))
                {
                    string assignedRootBundleName = string.Empty;
                    assetBundleMap.TryGetValue(dependencyPath, out assignedRootBundleName);
                    DependencyRecord rootRecord = CreateCurrentBundleRecord(dependencyPath, result.m_TargetBundleName, true);
                    rootRecord.m_AssignedBundleName = assignedRootBundleName;
                    if (!string.IsNullOrEmpty(assignedRootBundleName) &&
                        !string.Equals(assignedRootBundleName, result.m_TargetBundleName, StringComparison.OrdinalIgnoreCase))
                    {
                        rootRecord.m_Reason = "这是当前分析集合中的根 Prefab 之一。虽然现有规则可能把它分到其他包，但在本次联合打包预览中会作为当前包主资源之一被打入。";
                    }
                    else
                    {
                        rootRecord.m_Reason = "这是当前分析集合中的根 Prefab 之一，会作为当前 AB 包主资源之一被打入。";
                    }

                    result.m_PackedIntoCurrentBundle.Add(rootRecord);
                    continue;
                }

                string assignedBundleName = string.Empty;
                assetBundleMap.TryGetValue(dependencyPath, out assignedBundleName);
                bool isPackedIntoCurrentBundle = string.IsNullOrEmpty(assignedBundleName) ||
                                                string.Equals(assignedBundleName, result.m_TargetBundleName, StringComparison.OrdinalIgnoreCase) ||
                                                rootAssignedBundleNames.Contains(assignedBundleName);

                if (isPackedIntoCurrentBundle)
                {
                    DependencyRecord currentRecord = CreateCurrentBundleRecord(dependencyPath, result.m_TargetBundleName, false);
                    if (!string.IsNullOrEmpty(assignedBundleName))
                    {
                        currentRecord.m_AssignedBundleName = assignedBundleName;
                        if (rootAssignedBundleNames.Contains(assignedBundleName) &&
                            !string.Equals(assignedBundleName, result.m_TargetBundleName, StringComparison.OrdinalIgnoreCase))
                        {
                            currentRecord.m_Reason = "该资源原本归属于某个根 Prefab 的规则包。由于当前按多个 Prefab 联合打包预览，因此会被并入当前包。";
                        }
                        else
                        {
                            currentRecord.m_Reason = "该资源在当前规则中与当前预览包同属一个 AB 包。";
                        }
                    }

                    result.m_PackedIntoCurrentBundle.Add(currentRecord);
                }
                else
                {
                    DependencyRecord otherRecord = new DependencyRecord();
                    otherRecord.m_AssetPath = dependencyPath;
                    otherRecord.m_TargetBundleName = assignedBundleName;
                    otherRecord.m_AssignedBundleName = assignedBundleName;
                    otherRecord.m_IsPackedIntoCurrentBundle = false;
                    otherRecord.m_Reason = "该资源已被当前打包规则显式分配到其他 AB 包，不会打入目标 Prefab 当前包内。";
                    result.m_PackedIntoOtherBundle.Add(otherRecord);

                    if (!externalBundleMap.TryGetValue(assignedBundleName, out ExternalBundleRecord externalRecord))
                    {
                        externalRecord = new ExternalBundleRecord();
                        externalRecord.m_BundleName = assignedBundleName;
                        externalBundleMap.Add(assignedBundleName, externalRecord);
                    }

                    externalRecord.m_AssetPaths.Add(dependencyPath);
                }
            }

            result.m_ExternalBundles.AddRange(externalBundleMap.Values);
            result.m_ExternalBundles.Sort((left, right) => string.Compare(left.m_BundleName, right.m_BundleName, StringComparison.OrdinalIgnoreCase));
            for (int i = 0; i < result.m_ExternalBundles.Count; i++)
            {
                result.m_ExternalBundles[i].m_AssetPaths.Sort((left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
            }

            result.m_PackedIntoCurrentBundle.Sort((left, right) => string.Compare(left.m_AssetPath, right.m_AssetPath, StringComparison.OrdinalIgnoreCase));
            result.m_PackedIntoOtherBundle.Sort((left, right) => string.Compare(left.m_AssetPath, right.m_AssetPath, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private string ResolvePreviewBundleName(List<string> prefabPaths, Dictionary<string, string> assetBundleMap, string previewBundleNameOverride)
        {
            if (!string.IsNullOrWhiteSpace(previewBundleNameOverride))
            {
                return NormalizeBundleName(previewBundleNameOverride);
            }

            return ResolvePreviewBundleName(prefabPaths, assetBundleMap);
        }

        // 这里把项目 BuildHelper 当前使用的显式分包规则映射成“资源路径 -> AB包名”表。
        private Dictionary<string, string> BuildAssetBundleRuleMap()
        {
            Dictionary<string, string> assetBundleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddPerAssetBundleRule(assetBundleMap, "ResourcesEx/Prefabs", BuildHelper.PrefabExt, "Prefabs");
            AddPerAssetBundleRule(assetBundleMap, "ResourcesEx/Configs", BuildHelper.AssetExt, "Configs");
            AddPerAssetBundleRule(assetBundleMap, "ResourcesEx/Textures", BuildHelper.TextureExt, "Textures");
            AddCarBundleRule(assetBundleMap);
            return assetBundleMap;
        }

        private void AddPerAssetBundleRule(Dictionary<string, string> assetBundleMap, string relativeFolder, string extension, string outputPrefix)
        {
            string absoluteFolder = Path.Combine(Application.dataPath, relativeFolder);
            if (!Directory.Exists(absoluteFolder))
            {
                return;
            }

            string[] files = Directory.GetFiles(absoluteFolder, "*" + extension, SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                string assetPath = ConvertToAssetPath(files[i]);
                if (string.IsNullOrEmpty(assetPath))
                {
                    continue;
                }

                string relativeAssetPath = assetPath.Substring(("Assets/" + relativeFolder.Replace("\\", "/")).Length + 1);
                string bundleName = Path.Combine(outputPrefix, Path.ChangeExtension(relativeAssetPath, BuildHelper.AssetBundleExt)).Replace("\\", "/");
                assetBundleMap[assetPath] = bundleName;
            }
        }

        private void AddCarBundleRule(Dictionary<string, string> assetBundleMap)
        {
            List<string> carAssets = new List<string>();
            string[] lightTextureNames = { "CS_Direction", "CS_High_Point", "CS_Stop", "Front_High_Beam_noa", "Front_Low_Beam_noa" };
            for (int i = 0; i < lightTextureNames.Length; i++)
            {
                carAssets.Add(NormalizeAssetPath("Assets/Raw/N60AS/Materials/Light/Tex/" + lightTextureNames[i] + ".png"));
            }

            string[] fbxNames =
            {
                "N60AS_Launcher_new",
                "N60_New_Outline_Export",
                "N60AS_DIM",
                "N60AS_Wheel_Warn_Car",
                "N60AS_Wheel_01",
                "N60AS_Wheel_02",
                "N60AS_Wheel_Warn"
            };

            for (int i = 0; i < fbxNames.Length; i++)
            {
                carAssets.Add(NormalizeAssetPath("Assets/Raw/N60AS/" + fbxNames[i] + ".fbx"));
            }

            AddAssetsBySearchPattern(carAssets, "Assets/Raw/N60AS/Materials/Car", "*.mat");

            for (int i = 0; i < carAssets.Count; i++)
            {
                string assetPath = carAssets[i];
                if (!string.IsNullOrEmpty(assetPath))
                {
                    assetBundleMap[assetPath] = "common/car/models.ab";
                }
            }
        }

        private void AddAssetsBySearchPattern(List<string> assetPaths, string assetFolder, string searchPattern)
        {
            if (assetPaths == null)
            {
                return;
            }

            string absoluteFolder = Path.Combine(EditorUtil.ProjectPath, assetFolder.Replace("/", "\\"));
            if (!Directory.Exists(absoluteFolder))
            {
                return;
            }

            string[] files = Directory.GetFiles(absoluteFolder, searchPattern, SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                string assetPath = ConvertToAssetPath(files[i]);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    assetPaths.Add(assetPath);
                }
            }
        }

        private HashSet<string> CollectCurrentBundleAssetPaths(PreviewResult previewResult)
        {
            HashSet<string> assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (previewResult == null)
            {
                return assetPaths;
            }

            for (int i = 0; i < previewResult.m_PackedIntoCurrentBundle.Count; i++)
            {
                DependencyRecord record = previewResult.m_PackedIntoCurrentBundle[i];
                if (record != null && !string.IsNullOrEmpty(record.m_AssetPath))
                {
                    assetPaths.Add(record.m_AssetPath);
                }
            }

            return assetPaths;
        }

        private DependencyRecord CreateCurrentBundleRecord(string assetPath, string currentBundleName, bool isRootPrefab)
        {
            DependencyRecord record = new DependencyRecord();
            record.m_AssetPath = assetPath;
            record.m_TargetBundleName = currentBundleName;
            record.m_IsPackedIntoCurrentBundle = true;
            if (isRootPrefab)
            {
                record.m_Reason = "这是当前分析的目标 Prefab，自身会作为当前 AB 包的主资源被打入。";
            }
            else if (string.IsNullOrEmpty(currentBundleName))
            {
                record.m_Reason = "该依赖资源未被当前 BuildHelper 规则单独分配到其他包，若目标 Prefab 参与打包，将随目标资源一起进入同一个包。";
            }
            else
            {
                record.m_Reason = "该依赖资源未被当前规则单独分配到其他包，因此会随目标 Prefab 一起打入当前 AB 包。";
            }

            return record;
        }

        private bool ShouldIncludeDependencyPath(string dependencyPath)
        {
            if (string.IsNullOrEmpty(dependencyPath))
            {
                return false;
            }

            if (m_OnlyIncludeAssetsFolder && !dependencyPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !AssetDatabase.IsValidFolder(dependencyPath);
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
                EditorUtility.DisplayDialog("提示", "未能定位该资源，可能路径已失效。", "确定");
                return;
            }

            Selection.activeObject = assetObject;
            EditorGUIUtility.PingObject(assetObject);
        }

        private static string ConvertToAssetPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return string.Empty;
            }

            string normalizedFilePath = Path.GetFullPath(filePath).Replace("\\", "/");
            string normalizedAssetsPath = Application.dataPath.Replace("\\", "/");
            if (!normalizedFilePath.StartsWith(normalizedAssetsPath, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return "Assets" + normalizedFilePath.Substring(normalizedAssetsPath.Length);
        }

        private static string NormalizeAssetPath(string assetPath)
        {
            return string.IsNullOrEmpty(assetPath) ? string.Empty : assetPath.Replace("\\", "/");
        }

        private static string NormalizeBundleName(string bundleName)
        {
            if (string.IsNullOrWhiteSpace(bundleName))
            {
                return string.Empty;
            }

            string normalizedName = bundleName.Trim().Replace("\\", "/");
            if (!normalizedName.EndsWith(BuildHelper.AssetBundleExt, StringComparison.OrdinalIgnoreCase))
            {
                normalizedName += BuildHelper.AssetBundleExt;
            }

            return normalizedName;
        }

        private static void SortAssetPathList(List<string> assetPaths)
        {
            if (assetPaths == null)
            {
                return;
            }

            assetPaths.Sort((left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
        }
    }
}
