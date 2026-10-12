// ================================================================
// 未使用资源查找工具
//
// 文件名称: UnusedAssetFinderWindow.cs
// 版本: 1.0
//
// 描述:
// 该工具用于扫描指定目录下可能未被其他资源引用的资源，并以列表形式展示结果。
// 结果列表支持快速定位资源、单独删除资源，以及一键批量删除当前扫描结果中的全部未使用资源。
//
// 使用方式:
// 1. 打开菜单 Tools/Azumi/资源工具/未使用资源查找。
// 2. 指定“待检查目录”，工具会在该目录下收集所有待判断资源。
// 3. 可选指定“引用扫描范围”；若不指定，则默认扫描整个 Assets 目录中的资源依赖关系。
// 4. 点击“扫描未使用资源”后，工具会统计哪些资源没有被扫描范围内的其他资源依赖。
// 5. 可在结果列表中点击“定位资源”快速 Ping 到 Project 视图中的资源位置。
// 6. 可点击“删除该资源”单独删除，也可点击“删除全部结果”批量删除当前结果中的全部资源。
//
// 注意事项:
// 1. 本工具基于 AssetDatabase 依赖关系判断“是否被使用”，无法识别代码动态加载、字符串路径加载、
//    Addressables、AssetBundle、Resources.Load 路径拼接等运行时引用。
// 2. 删除操作会直接删除项目中的资源文件，建议先提交版本控制后再批量处理。
// 3. 若扫描范围设置过小，可能会把范围外实际正在使用的资源误判为未使用资源。
//
// 修改历史:
// 2026-06-01 - 初始版本
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AzumisEditorTools
{
    public class UnusedAssetFinderWindow : EditorWindow
    {
        private sealed class UnusedAssetRecord
        {
            public string m_AssetPath = string.Empty;
            public string m_AssetTypeName = string.Empty;
            public UnityEngine.Object m_AssetObject;
        }

        private const string m_WindowTitle = "未使用资源查找";
        private const string m_MenuPath = "Tools/Azumi/资源工具/未使用资源查找";

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

        private DefaultAsset m_TargetFolder;
        private DefaultAsset m_SearchFolder;
        private Vector2 m_ScrollPosition;
        private string m_LastSummary = string.Empty;
        private int m_LastCandidateAssetCount;
        private int m_LastSearchAssetCount;
        private readonly List<UnusedAssetRecord> m_UnusedAssetRecords = new List<UnusedAssetRecord>();

        [MenuItem(m_MenuPath)]
        public static void ShowWindow()
        {
            UnusedAssetFinderWindow window = GetWindow<UnusedAssetFinderWindow>(m_WindowTitle);
            window.minSize = new Vector2(760f, 580f);
            window.Show();
        }

        private void OnGUI()
        {
            DrawInputSection();
            EditorGUILayout.Space(8f);
            DrawSummarySection();
            EditorGUILayout.Space(8f);
            DrawResultSection();
        }

        // 输入区负责选择待检查目录和引用扫描范围，并给出扫描限制说明。
        private void DrawInputSection()
        {
            EditorGUILayout.LabelField("扫描设置", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            m_TargetFolder = (DefaultAsset)EditorGUILayout.ObjectField("待检查目录", m_TargetFolder, typeof(DefaultAsset), false);
            m_SearchFolder = (DefaultAsset)EditorGUILayout.ObjectField("引用扫描范围", m_SearchFolder, typeof(DefaultAsset), false);

            EditorGUILayout.HelpBox(
                "待检查目录中的资源会被作为“候选资源”参与未使用判断；引用扫描范围中的资源会作为“引用来源”参与依赖统计。" +
                "若不指定引用扫描范围，则默认扫描整个 Assets 目录。工具仅能识别 AssetDatabase 可追踪的资源依赖，无法识别代码动态加载。",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("扫描未使用资源", GUILayout.Height(28f)))
                {
                    ScanUnusedAssets();
                }

                using (new EditorGUI.DisabledScope(m_UnusedAssetRecords.Count == 0))
                {
                    if (GUILayout.Button("删除全部结果", GUILayout.Height(28f)))
                    {
                        DeleteAllUnusedAssets();
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        // 汇总区用于快速查看本次扫描涉及的候选资源数、引用来源数和未使用结果数。
        private void DrawSummarySection()
        {
            EditorGUILayout.LabelField("扫描结果", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("候选资源数", m_LastCandidateAssetCount.ToString());
            EditorGUILayout.LabelField("引用来源资源数", m_LastSearchAssetCount.ToString());
            EditorGUILayout.LabelField("未使用资源数", m_UnusedAssetRecords.Count.ToString());

            if (!string.IsNullOrEmpty(m_LastSummary))
            {
                EditorGUILayout.HelpBox(m_LastSummary, MessageType.None);
            }

            EditorGUILayout.EndVertical();
        }

        // 结果区以列表形式展示当前判定为未使用的资源，并提供定位与删除入口。
        private void DrawResultSection()
        {
            EditorGUILayout.LabelField("详情列表", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (m_UnusedAssetRecords.Count == 0)
            {
                EditorGUILayout.HelpBox("当前没有可显示的未使用资源结果。请先执行一次扫描。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);
            for (int i = 0; i < m_UnusedAssetRecords.Count; i++)
            {
                DrawUnusedAssetRecord(m_UnusedAssetRecords[i], i);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawUnusedAssetRecord(UnusedAssetRecord record, int index)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("资源 " + (index + 1), EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("资源对象", record.m_AssetObject, typeof(UnityEngine.Object), false);
            }

            EditorGUILayout.LabelField("资源类型", record.m_AssetTypeName);
            EditorGUILayout.SelectableLabel(record.m_AssetPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("定位资源", GUILayout.Width(96f)))
                {
                    PingAsset(record);
                }

                if (GUILayout.Button("删除该资源", GUILayout.Width(108f)))
                {
                    DeleteSingleUnusedAsset(record);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void ScanUnusedAssets()
        {
            if (!TryGetFolderPath(m_TargetFolder, "待检查目录", true, out string targetFolderPath))
            {
                return;
            }

            string searchFolderPath = "Assets";
            if (m_SearchFolder != null && !TryGetFolderPath(m_SearchFolder, "引用扫描范围", false, out searchFolderPath))
            {
                return;
            }

            m_UnusedAssetRecords.Clear();
            m_LastSummary = string.Empty;
            m_LastCandidateAssetCount = 0;
            m_LastSearchAssetCount = 0;

            List<string> candidateAssetPaths = CollectCandidateAssetPaths(targetFolderPath);
            m_LastCandidateAssetCount = candidateAssetPaths.Count;
            if (candidateAssetPaths.Count == 0)
            {
                m_LastSummary = "待检查目录下没有可参与判断的资源。";
                return;
            }

            string[] searchAssetPaths = CollectSearchAssetPaths(searchFolderPath);
            m_LastSearchAssetCount = searchAssetPaths.Length;

            HashSet<string> candidatePathSet = new HashSet<string>(candidateAssetPaths, StringComparer.OrdinalIgnoreCase);
            HashSet<string> referencedCandidatePathSet = CollectReferencedCandidatePaths(candidatePathSet, searchAssetPaths);

            for (int i = 0; i < candidateAssetPaths.Count; i++)
            {
                string candidateAssetPath = candidateAssetPaths[i];
                if (referencedCandidatePathSet.Contains(candidateAssetPath))
                {
                    continue;
                }

                UnityEngine.Object assetObject = AssetDatabase.LoadMainAssetAtPath(candidateAssetPath);
                UnusedAssetRecord record = new UnusedAssetRecord();
                record.m_AssetPath = candidateAssetPath;
                record.m_AssetObject = assetObject;
                record.m_AssetTypeName = assetObject != null ? assetObject.GetType().Name : GetAssetTypeName(candidateAssetPath);
                m_UnusedAssetRecords.Add(record);
            }

            m_UnusedAssetRecords.Sort((left, right) => string.Compare(left.m_AssetPath, right.m_AssetPath, StringComparison.OrdinalIgnoreCase));
            m_LastSummary =
                "扫描完成。候选资源数: " + m_LastCandidateAssetCount +
                "，引用来源资源数: " + m_LastSearchAssetCount +
                "，未使用资源数: " + m_UnusedAssetRecords.Count + "。";
        }

        // 候选资源只来自待检查目录，并排除脚本、程序集定义等不适合按资源依赖方式判断的文件。
        private List<string> CollectCandidateAssetPaths(string targetFolderPath)
        {
            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { targetFolderPath });
            HashSet<string> uniqueAssetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < guids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!IsCandidateAssetPath(assetPath))
                {
                    continue;
                }

                uniqueAssetPaths.Add(assetPath);
            }

            return new List<string>(uniqueAssetPaths);
        }

        // 引用来源用于建立“谁依赖了候选资源”的集合，只要候选资源至少被其他资源依赖一次，就不会进入未使用列表。
        private string[] CollectSearchAssetPaths(string searchFolderPath)
        {
            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { searchFolderPath });
            List<string> searchAssetPaths = new List<string>(guids.Length);
            for (int i = 0; i < guids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath))
                {
                    continue;
                }

                searchAssetPaths.Add(assetPath);
            }

            return searchAssetPaths.ToArray();
        }

        private HashSet<string> CollectReferencedCandidatePaths(HashSet<string> candidatePathSet, string[] searchAssetPaths)
        {
            HashSet<string> referencedCandidatePathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                for (int i = 0; i < searchAssetPaths.Length; i++)
                {
                    string searchAssetPath = searchAssetPaths[i];
                    float progress = searchAssetPaths.Length > 0 ? (float)(i + 1) / searchAssetPaths.Length : 1f;
                    EditorUtility.DisplayProgressBar(m_WindowTitle, "正在分析依赖: " + searchAssetPath, progress);

                    string[] dependencyPaths = AssetDatabase.GetDependencies(searchAssetPath, false);
                    for (int j = 0; j < dependencyPaths.Length; j++)
                    {
                        string dependencyPath = dependencyPaths[j];
                        if (string.Equals(dependencyPath, searchAssetPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (candidatePathSet.Contains(dependencyPath))
                        {
                            referencedCandidatePathSet.Add(dependencyPath);
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return referencedCandidatePathSet;
        }

        private void DeleteSingleUnusedAsset(UnusedAssetRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.m_AssetPath))
            {
                return;
            }

            bool isConfirmed = EditorUtility.DisplayDialog(
                "确认删除",
                "即将删除资源:\n" + record.m_AssetPath + "\n\n是否继续？",
                "继续",
                "取消");
            if (!isConfirmed)
            {
                return;
            }

            int deletedCount = DeleteAssets(new[] { record.m_AssetPath });
            if (deletedCount > 0)
            {
                RescanAfterDelete();
                EditorUtility.DisplayDialog("完成", "已删除该资源。", "确定");
                return;
            }

            EditorUtility.DisplayDialog("提示", "删除失败，请检查资源是否仍存在或是否被 Unity 锁定。", "确定");
        }

        private void DeleteAllUnusedAssets()
        {
            if (m_UnusedAssetRecords.Count == 0)
            {
                EditorUtility.DisplayDialog("提示", "当前没有可删除的未使用资源。", "确定");
                return;
            }

            bool isConfirmed = EditorUtility.DisplayDialog(
                "确认批量删除",
                "即将删除当前扫描结果中的全部未使用资源，共 " + m_UnusedAssetRecords.Count + " 个。\n此操作会直接删除资源文件，是否继续？",
                "继续",
                "取消");
            if (!isConfirmed)
            {
                return;
            }

            List<string> assetPaths = new List<string>(m_UnusedAssetRecords.Count);
            for (int i = 0; i < m_UnusedAssetRecords.Count; i++)
            {
                assetPaths.Add(m_UnusedAssetRecords[i].m_AssetPath);
            }

            int deletedCount = DeleteAssets(assetPaths);
            RescanAfterDelete();
            EditorUtility.DisplayDialog("完成", "批量删除完成。\n成功删除资源数: " + deletedCount, "确定");
        }

        // 删除后重新扫描，确保列表状态和磁盘上的真实资源状态保持一致。
        private void RescanAfterDelete()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (m_TargetFolder == null)
            {
                m_UnusedAssetRecords.Clear();
                m_LastSummary = "目标目录已丢失，结果列表已清空。";
                m_LastCandidateAssetCount = 0;
                m_LastSearchAssetCount = 0;
                return;
            }

            string targetFolderPath = AssetDatabase.GetAssetPath(m_TargetFolder);
            if (string.IsNullOrEmpty(targetFolderPath) || !AssetDatabase.IsValidFolder(targetFolderPath))
            {
                m_UnusedAssetRecords.Clear();
                m_LastSummary = "目标目录已被删除或失效，结果列表已清空。";
                m_LastCandidateAssetCount = 0;
                m_LastSearchAssetCount = 0;
                return;
            }

            ScanUnusedAssets();
        }

        private int DeleteAssets(IEnumerable<string> assetPaths)
        {
            int deletedCount = 0;
            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (string assetPath in assetPaths)
                {
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        continue;
                    }

                    if (AssetDatabase.DeleteAsset(assetPath))
                    {
                        deletedCount++;
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            return deletedCount;
        }

        private void PingAsset(UnusedAssetRecord record)
        {
            if (record == null)
            {
                return;
            }

            UnityEngine.Object assetObject = record.m_AssetObject;
            if (assetObject == null)
            {
                assetObject = AssetDatabase.LoadMainAssetAtPath(record.m_AssetPath);
                record.m_AssetObject = assetObject;
            }

            if (assetObject == null)
            {
                EditorUtility.DisplayDialog("提示", "未能加载该资源，可能该资源已被删除。", "确定");
                return;
            }

            Selection.activeObject = assetObject;
            EditorGUIUtility.PingObject(assetObject);
        }

        private bool TryGetFolderPath(DefaultAsset folderAsset, string fieldName, bool isRequired, out string folderPath)
        {
            folderPath = string.Empty;
            if (folderAsset == null)
            {
                if (isRequired)
                {
                    EditorUtility.DisplayDialog("提示", "请先指定" + fieldName + "。", "确定");
                    return false;
                }

                return true;
            }

            folderPath = AssetDatabase.GetAssetPath(folderAsset);
            if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
            {
                EditorUtility.DisplayDialog("提示", fieldName + "不是有效的资源目录。", "确定");
                return false;
            }

            return true;
        }

        private static bool IsCandidateAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath))
            {
                return false;
            }

            string extension = Path.GetExtension(assetPath);
            if (!string.IsNullOrEmpty(extension) && m_IgnoredExtensions.Contains(extension))
            {
                return false;
            }

            return true;
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
    }
}
