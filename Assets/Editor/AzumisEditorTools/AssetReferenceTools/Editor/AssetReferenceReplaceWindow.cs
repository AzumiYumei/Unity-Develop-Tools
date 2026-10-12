// ================================================================
// 资源引用查找与替换工具
//
// 文件名称: AssetReferenceReplaceWindow.cs
// 版本: 1.2
//
// 描述: 在 Unity 编辑器内扫描指定资源被哪些资源引用，并列出引用来源。
//      工具会遍历 Assets 目录下的普通资源、Prefab、Scene 以及 Importer 设置中的
//      Unity Object 引用字段，并支持将目标引用批量替换为新的资源引用。
//
// 修改历史:
// 2026-05-26 - 初始版本
// 2026-05-26 - 增加列表单条删除引用与单条替换引用功能
// 2026-06-29 - 增加目标资源内部引用展开扫描，可递归查找目标资源引用集合在指定目录中的被引用情况
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
    public class AssetReferenceReplaceWindow : EditorWindow
    {
        private sealed class ReferenceRecord
        {
            public string m_AssetPath = string.Empty;
            public string m_AssetKind = string.Empty;
            public string m_OwnerName = string.Empty;
            public string m_OwnerType = string.Empty;
            public string m_PropertyPath = string.Empty;
            public string m_MatchedTargetPath = string.Empty;
            public string m_MatchedTargetKind = string.Empty;
            public bool m_IsMatchedRootTarget;
            public int m_RecordIndexInAssetKind;
        }

        private sealed class TargetReferenceRecord
        {
            public string m_AssetPath = string.Empty;
            public string m_AssetKind = string.Empty;
            public string m_SourceAssetPath = string.Empty;
            public string m_SourceAssetKind = string.Empty;
            public string m_OwnerName = string.Empty;
            public string m_OwnerType = string.Empty;
            public string m_PropertyPath = string.Empty;
            public bool m_IsRootAsset;
            public int m_Depth;
        }

        private struct ProcessStats
        {
            public int m_AssetCount;
            public int m_OwnerCount;
            public int m_ReferenceCount;
            public int m_ReplaceCount;
        }

        private const string m_WindowTitle = "资源引用查找与替换";
        private const string m_MenuPath = "Tools/Azumi/资源工具/资源引用查找与替换";

        private UnityEngine.Object m_TargetAsset;
        private UnityEngine.Object m_ReplacementAsset;
        private DefaultAsset m_SearchFolder;
        private bool m_IncludeImporterSettings = true;
        private bool m_IsTargetMatchSetExpanded = true;
        private bool m_IsResultExpanded = true;
        private string m_LastSummary = string.Empty;
        private Vector2 m_ScrollPosition;
        private readonly List<ReferenceRecord> m_ReferenceRecords = new List<ReferenceRecord>();
        private readonly List<TargetReferenceRecord> m_TargetReferenceRecords = new List<TargetReferenceRecord>();

        [MenuItem(m_MenuPath)]
        public static void ShowWindow()
        {
            AssetReferenceReplaceWindow window = GetWindow<AssetReferenceReplaceWindow>(m_WindowTitle);
            window.minSize = new Vector2(760f, 640f);
            window.Show();
        }

        private void OnGUI()
        {
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);
            DrawInputSection();
            EditorGUILayout.Space(10f);
            DrawActionSection();
            EditorGUILayout.Space(10f);
            DrawResultSection();
            EditorGUILayout.EndScrollView();
        }

        // 输入区用于选择要查找的目标资源、替换资源和扫描范围。
        private void DrawInputSection()
        {
            EditorGUILayout.LabelField("输入参数", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            m_TargetAsset = EditorGUILayout.ObjectField("目标资源", m_TargetAsset, typeof(UnityEngine.Object), false);
            m_ReplacementAsset = EditorGUILayout.ObjectField("替换资源", m_ReplacementAsset, typeof(UnityEngine.Object), false);
            m_SearchFolder = (DefaultAsset)EditorGUILayout.ObjectField("扫描目录", m_SearchFolder, typeof(DefaultAsset), false);
            m_IncludeImporterSettings = EditorGUILayout.ToggleLeft("包含 Importer 设置中的引用", m_IncludeImporterSettings);

            EditorGUILayout.HelpBox(
                "会扫描 Assets 目录下的普通资源、Prefab、Scene，以及可选的 Importer 设置中的 Object 引用。" +
                "新增“扫描目标及内部引用”后，会先递归展开目标资源内部显式引用，再扫描目录中是否存在对这些资源的引用。" +
                "替换操作会直接写回对应资源文件。为了避免把当前未保存的场景一并保存，执行替换前请先保存已打开场景。",
                MessageType.Info);

            EditorGUILayout.EndVertical();
        }

        // 操作区负责触发扫描和批量替换。
        private void DrawActionSection()
        {
            EditorGUILayout.LabelField("操作", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("扫描引用", GUILayout.Height(28f)))
                {
                    ScanReferences();
                }

                if (GUILayout.Button("扫描目标及内部引用", GUILayout.Height(28f)))
                {
                    ScanTargetReferences();
                }

                using (new EditorGUI.DisabledScope(m_TargetAsset == null))
                {
                    if (GUILayout.Button("批量替换引用", GUILayout.Height(28f)))
                    {
                        ReplaceReferences();
                    }
                }
            }

            if (!string.IsNullOrEmpty(m_LastSummary))
            {
                EditorGUILayout.HelpBox(m_LastSummary, MessageType.None);
            }

            EditorGUILayout.EndVertical();
        }

        // 结果区用于展示扫描到的全部引用来源，便于定位和核对替换范围。
        private void DrawResultSection()
        {
            DrawTargetMatchSetSection();
            EditorGUILayout.Space(10f);

            EditorGUILayout.LabelField("扫描结果", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            m_IsResultExpanded = EditorGUILayout.Foldout(m_IsResultExpanded, "引用列表 (" + m_ReferenceRecords.Count + ")", true);
            if (!m_IsResultExpanded)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            if (m_ReferenceRecords.Count == 0)
            {
                EditorGUILayout.HelpBox("当前没有可显示的引用结果。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < m_ReferenceRecords.Count; i++)
            {
                DrawReferenceRecord(m_ReferenceRecords[i], i);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawTargetMatchSetSection()
        {
            EditorGUILayout.LabelField("当前匹配目标集合", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            m_IsTargetMatchSetExpanded = EditorGUILayout.Foldout(
                m_IsTargetMatchSetExpanded,
                "目标项列表 (" + m_TargetReferenceRecords.Count + ")",
                true);

            if (!m_IsTargetMatchSetExpanded)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            if (m_TargetReferenceRecords.Count == 0)
            {
                EditorGUILayout.HelpBox("当前还没有目标匹配集合。执行扫描后，这里会显示本次用于匹配的目标资源集合。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < m_TargetReferenceRecords.Count; i++)
            {
                DrawTargetReferenceRecord(m_TargetReferenceRecords[i], i);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawReferenceRecord(ReferenceRecord record, int index)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("引用 " + (index + 1), EditorStyles.boldLabel);
            EditorGUILayout.LabelField("资源类型", record.m_AssetKind);
            EditorGUILayout.SelectableLabel(record.m_AssetPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.LabelField("命中目标类型", record.m_MatchedTargetKind + (record.m_IsMatchedRootTarget ? " (目标资源本体)" : " (目标内部引用)"));
            EditorGUILayout.SelectableLabel(record.m_MatchedTargetPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.LabelField("持有对象", record.m_OwnerName + " (" + record.m_OwnerType + ")");
            EditorGUILayout.LabelField("属性路径", record.m_PropertyPath);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("定位资源路径", GUILayout.Width(96f)))
                {
                    UnityEngine.Object assetObject = AssetDatabase.LoadMainAssetAtPath(record.m_AssetPath);
                    if (assetObject != null)
                    {
                        Selection.activeObject = assetObject;
                        EditorGUIUtility.PingObject(assetObject);
                    }
                }

                if (GUILayout.Button("定位命中目标", GUILayout.Width(108f)))
                {
                    UnityEngine.Object matchedTargetObject = AssetDatabase.LoadMainAssetAtPath(record.m_MatchedTargetPath);
                    if (matchedTargetObject != null)
                    {
                        Selection.activeObject = matchedTargetObject;
                        EditorGUIUtility.PingObject(matchedTargetObject);
                    }
                }

                if (GUILayout.Button("单独删除引用", GUILayout.Width(108f)))
                {
                    DeleteSingleReference(record);
                }

                using (new EditorGUI.DisabledScope(m_ReplacementAsset == null))
                {
                    if (GUILayout.Button("单独替换引用", GUILayout.Width(108f)))
                    {
                        ReplaceSingleReference(record);
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawTargetReferenceRecord(TargetReferenceRecord record, int index)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("目标项 " + (index + 1), EditorStyles.boldLabel);
            EditorGUILayout.LabelField("资源类型", record.m_AssetKind + (record.m_IsRootAsset ? " (根目标)" : " (内部引用)"));
            EditorGUILayout.LabelField("递归层级", record.m_Depth.ToString());
            EditorGUILayout.SelectableLabel(record.m_AssetPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));

            if (!record.m_IsRootAsset)
            {
                EditorGUILayout.LabelField("来源资源类型", record.m_SourceAssetKind);
                EditorGUILayout.SelectableLabel(record.m_SourceAssetPath, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.LabelField("持有对象", record.m_OwnerName + " (" + record.m_OwnerType + ")");
                EditorGUILayout.LabelField("属性路径", record.m_PropertyPath);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("定位目标项", GUILayout.Width(96f)))
                {
                    UnityEngine.Object assetObject = AssetDatabase.LoadMainAssetAtPath(record.m_AssetPath);
                    if (assetObject != null)
                    {
                        Selection.activeObject = assetObject;
                        EditorGUIUtility.PingObject(assetObject);
                    }
                }

                if (!record.m_IsRootAsset && GUILayout.Button("定位来源资源", GUILayout.Width(96f)))
                {
                    UnityEngine.Object sourceAssetObject = AssetDatabase.LoadMainAssetAtPath(record.m_SourceAssetPath);
                    if (sourceAssetObject != null)
                    {
                        Selection.activeObject = sourceAssetObject;
                        EditorGUIUtility.PingObject(sourceAssetObject);
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void ScanReferences()
        {
            if (!ValidateInput(false))
            {
                return;
            }

            PrepareDirectTargetMatchSet();
            m_ReferenceRecords.Clear();
            ProcessStats stats = ProcessAllAssets(false, true, false, BuildTargetReferenceLookup());
            m_LastSummary =
                "扫描完成。已扫描资源数: " + stats.m_AssetCount +
                "，对象数: " + stats.m_OwnerCount +
                "，命中引用数: " + stats.m_ReferenceCount + "。";
        }

        private void ScanTargetReferences()
        {
            if (!ValidateInput(false))
            {
                return;
            }

            BuildExpandedTargetMatchSet();
            m_ReferenceRecords.Clear();
            ProcessStats stats = ProcessAllAssets(false, true, false, BuildTargetReferenceLookup());
            m_LastSummary =
                "扫描完成。当前匹配目标集合数: " + m_TargetReferenceRecords.Count +
                "，已扫描资源数: " + stats.m_AssetCount +
                "，对象数: " + stats.m_OwnerCount +
                "，命中引用数: " + stats.m_ReferenceCount + "。";
        }

        private void ReplaceReferences()
        {
            if (!ValidateInput(true))
            {
                return;
            }

            if (!CanReplaceOpenScenes())
            {
                return;
            }

            bool isConfirmed = EditorUtility.DisplayDialog(
                "确认替换",
                "将会把扫描范围内所有指向目标资源的引用替换为新的资源引用，此操作会直接写回资源文件，是否继续？",
                "继续",
                "取消");

            if (!isConfirmed)
            {
                return;
            }

            m_ReferenceRecords.Clear();
            PrepareDirectTargetMatchSet();
            ProcessStats stats = ProcessAllAssets(true, false, true, BuildTargetReferenceLookup());
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            ScanReferences();
            EditorUtility.DisplayDialog(
                "替换完成",
                "处理资源数: " + stats.m_AssetCount +
                "\n命中引用数: " + stats.m_ReferenceCount +
                "\n成功替换数: " + stats.m_ReplaceCount,
                "确定");
        }

        private void DeleteSingleReference(ReferenceRecord record)
        {
            if (record == null || !ValidateInput(false))
            {
                return;
            }

            if (!CanModifySingleRecord(record))
            {
                return;
            }

            bool isConfirmed = EditorUtility.DisplayDialog(
                "确认删除引用",
                "将把当前这条引用记录改为 Null，是否继续？",
                "继续",
                "取消");

            if (!isConfirmed)
            {
                return;
            }

            ExecuteSingleRecordReplace(record, null, "删除");
        }

        private void ReplaceSingleReference(ReferenceRecord record)
        {
            if (record == null || !ValidateInput(false))
            {
                return;
            }

            if (!ValidateSingleReplacementAsset())
            {
                return;
            }

            if (!CanModifySingleRecord(record))
            {
                return;
            }

            bool isConfirmed = EditorUtility.DisplayDialog(
                "确认替换引用",
                "将只替换当前这条引用记录，是否继续？",
                "继续",
                "取消");

            if (!isConfirmed)
            {
                return;
            }

            ExecuteSingleRecordReplace(record, m_ReplacementAsset, "替换");
        }

        private void ExecuteSingleRecordReplace(ReferenceRecord record, UnityEngine.Object replacementAsset, string operationName)
        {
            ProcessStats stats = new ProcessStats();
            bool isChanged = false;

            try
            {
                EditorUtility.DisplayProgressBar(m_WindowTitle, "正在处理: " + record.m_AssetPath, 1f);
                if (string.Equals(record.m_AssetKind, "Scene", StringComparison.OrdinalIgnoreCase))
                {
                    isChanged = ProcessSceneAsset(record.m_AssetPath, true, false, ref stats, record.m_RecordIndexInAssetKind, replacementAsset);
                }
                else if (string.Equals(record.m_AssetKind, "Prefab", StringComparison.OrdinalIgnoreCase))
                {
                    isChanged = ProcessPrefabAsset(record.m_AssetPath, true, false, ref stats, record.m_RecordIndexInAssetKind, replacementAsset);
                }
                else if (string.Equals(record.m_AssetKind, "Importer", StringComparison.OrdinalIgnoreCase))
                {
                    isChanged = ProcessImporterAtPath(record.m_AssetPath, true, false, ref stats, record.m_RecordIndexInAssetKind, replacementAsset);
                }
                else
                {
                    isChanged = ProcessGenericAssetPath(record.m_AssetPath, true, false, ref stats, record.m_RecordIndexInAssetKind, replacementAsset);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (!isChanged)
            {
                EditorUtility.DisplayDialog("提示", "未能修改该条引用，可能该记录已失效或替换资源类型不兼容。", "确定");
                ScanReferences();
                return;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            ScanReferences();
            EditorUtility.DisplayDialog("完成", "已完成当前记录的" + operationName + "操作。", "确定");
        }

        private void PrepareDirectTargetMatchSet()
        {
            m_TargetReferenceRecords.Clear();
            if (m_TargetAsset == null)
            {
                return;
            }

            string targetAssetPath = AssetDatabase.GetAssetPath(m_TargetAsset);
            if (string.IsNullOrEmpty(targetAssetPath))
            {
                return;
            }

            TargetReferenceRecord rootRecord = new TargetReferenceRecord();
            rootRecord.m_AssetPath = targetAssetPath;
            rootRecord.m_AssetKind = GetAssetKind(targetAssetPath);
            rootRecord.m_IsRootAsset = true;
            rootRecord.m_Depth = 0;
            m_TargetReferenceRecords.Add(rootRecord);
        }

        private void BuildExpandedTargetMatchSet()
        {
            PrepareDirectTargetMatchSet();
            if (m_TargetReferenceRecords.Count == 0)
            {
                return;
            }

            Dictionary<string, TargetReferenceRecord> collectedRecordMap = BuildTargetReferenceLookup();
            Queue<TargetReferenceRecord> pendingRecords = new Queue<TargetReferenceRecord>();
            pendingRecords.Enqueue(m_TargetReferenceRecords[0]);

            while (pendingRecords.Count > 0)
            {
                TargetReferenceRecord currentRecord = pendingRecords.Dequeue();
                List<TargetReferenceRecord> directReferences = CollectDirectTargetReferences(currentRecord.m_AssetPath, currentRecord.m_Depth + 1);
                for (int i = 0; i < directReferences.Count; i++)
                {
                    TargetReferenceRecord directReference = directReferences[i];
                    if (string.IsNullOrEmpty(directReference.m_AssetPath) || collectedRecordMap.ContainsKey(directReference.m_AssetPath))
                    {
                        continue;
                    }

                    collectedRecordMap.Add(directReference.m_AssetPath, directReference);
                    m_TargetReferenceRecords.Add(directReference);
                    pendingRecords.Enqueue(directReference);
                }
            }
        }

        private Dictionary<string, TargetReferenceRecord> BuildTargetReferenceLookup()
        {
            Dictionary<string, TargetReferenceRecord> lookup = new Dictionary<string, TargetReferenceRecord>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < m_TargetReferenceRecords.Count; i++)
            {
                TargetReferenceRecord record = m_TargetReferenceRecords[i];
                if (record == null || string.IsNullOrEmpty(record.m_AssetPath) || lookup.ContainsKey(record.m_AssetPath))
                {
                    continue;
                }

                lookup.Add(record.m_AssetPath, record);
            }

            return lookup;
        }

        // 对输入资源和替换资源进行基本校验，避免把场景对象或无效目录传入批处理流程。
        private bool ValidateInput(bool isReplace)
        {
            if (m_TargetAsset == null)
            {
                EditorUtility.DisplayDialog("提示", "请先指定目标资源。", "确定");
                return false;
            }

            string targetPath = AssetDatabase.GetAssetPath(m_TargetAsset);
            if (string.IsNullOrEmpty(targetPath))
            {
                EditorUtility.DisplayDialog("提示", "目标资源必须是 Project 视图中的持久化资源。", "确定");
                return false;
            }

            if (isReplace)
            {
                if (m_TargetAsset == m_ReplacementAsset)
                {
                    EditorUtility.DisplayDialog("提示", "替换资源不能与目标资源相同。", "确定");
                    return false;
                }

                if (m_ReplacementAsset != null)
                {
                    string replacementPath = AssetDatabase.GetAssetPath(m_ReplacementAsset);
                    if (string.IsNullOrEmpty(replacementPath))
                    {
                        EditorUtility.DisplayDialog("提示", "替换资源必须是 Project 视图中的持久化资源。", "确定");
                        return false;
                    }
                }
            }

            if (m_SearchFolder != null)
            {
                string folderPath = AssetDatabase.GetAssetPath(m_SearchFolder);
                if (string.IsNullOrEmpty(folderPath) || !AssetDatabase.IsValidFolder(folderPath))
                {
                    EditorUtility.DisplayDialog("提示", "扫描目录不是有效的资源目录。", "确定");
                    return false;
                }
            }

            return true;
        }

        private TargetReferenceRecord GetMatchedTargetRecord(UnityEngine.Object referencedObject, bool isExactTargetOnly, Dictionary<string, TargetReferenceRecord> targetReferenceLookup)
        {
            if (referencedObject == null)
            {
                return null;
            }

            if (isExactTargetOnly)
            {
                if (referencedObject != m_TargetAsset)
                {
                    return null;
                }

                string exactTargetPath = AssetDatabase.GetAssetPath(referencedObject);
                if (string.IsNullOrEmpty(exactTargetPath))
                {
                    return null;
                }

                if (targetReferenceLookup != null && targetReferenceLookup.TryGetValue(exactTargetPath, out TargetReferenceRecord exactMatchRecord))
                {
                    return exactMatchRecord;
                }

                TargetReferenceRecord fallbackRecord = new TargetReferenceRecord();
                fallbackRecord.m_AssetPath = exactTargetPath;
                fallbackRecord.m_AssetKind = GetAssetKind(exactTargetPath);
                fallbackRecord.m_IsRootAsset = true;
                fallbackRecord.m_Depth = 0;
                return fallbackRecord;
            }

            string referencedAssetPath = AssetDatabase.GetAssetPath(referencedObject);
            if (string.IsNullOrEmpty(referencedAssetPath) || targetReferenceLookup == null)
            {
                return null;
            }

            if (targetReferenceLookup.TryGetValue(referencedAssetPath, out TargetReferenceRecord matchedRecord))
            {
                return matchedRecord;
            }

            return null;
        }

        // 替换场景中的引用时，如果当前已有未保存场景，保存操作可能一并带上其他改动，因此这里强制先由用户自行保存。
        private bool CanReplaceOpenScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty)
                {
                    EditorUtility.DisplayDialog("提示", "检测到当前有未保存的已打开场景，请先保存场景后再执行替换。", "确定");
                    return false;
                }
            }

            return true;
        }

        private bool ValidateSingleReplacementAsset()
        {
            if (m_ReplacementAsset == null)
            {
                EditorUtility.DisplayDialog("提示", "请先指定替换资源。", "确定");
                return false;
            }

            if (m_ReplacementAsset == m_TargetAsset)
            {
                EditorUtility.DisplayDialog("提示", "替换资源不能与目标资源相同。", "确定");
                return false;
            }

            string replacementPath = AssetDatabase.GetAssetPath(m_ReplacementAsset);
            if (string.IsNullOrEmpty(replacementPath))
            {
                EditorUtility.DisplayDialog("提示", "替换资源必须是 Project 视图中的持久化资源。", "确定");
                return false;
            }

            return true;
        }

        private bool CanModifySingleRecord(ReferenceRecord record)
        {
            if (record == null)
            {
                return false;
            }

            if (string.Equals(record.m_AssetKind, "Scene", StringComparison.OrdinalIgnoreCase))
            {
                return CanReplaceOpenScenes();
            }

            return true;
        }

        private ProcessStats ProcessAllAssets(bool isReplace, bool isCollectRecords, bool isExactTargetOnly, Dictionary<string, TargetReferenceRecord> targetReferenceLookup)
        {
            ProcessStats stats = new ProcessStats();
            string[] assetPaths = GetSearchAssetPaths();

            try
            {
                for (int i = 0; i < assetPaths.Length; i++)
                {
                    string assetPath = assetPaths[i];
                    stats.m_AssetCount++;

                    float progress = assetPaths.Length > 0 ? (float)(i + 1) / assetPaths.Length : 1f;
                    EditorUtility.DisplayProgressBar(
                        m_WindowTitle,
                        "正在处理: " + assetPath,
                        progress);

                    if (IsScenePath(assetPath))
                    {
                        ProcessSceneAsset(assetPath, isReplace, isCollectRecords, ref stats, -1, null, isExactTargetOnly, targetReferenceLookup);
                        continue;
                    }

                    if (IsPrefabPath(assetPath))
                    {
                        ProcessPrefabAsset(assetPath, isReplace, isCollectRecords, ref stats, -1, null, isExactTargetOnly, targetReferenceLookup);
                    }
                    else
                    {
                        ProcessGenericAssetPath(assetPath, isReplace, isCollectRecords, ref stats, -1, null, isExactTargetOnly, targetReferenceLookup);
                    }

                    if (m_IncludeImporterSettings)
                    {
                        ProcessImporterAtPath(assetPath, isReplace, isCollectRecords, ref stats, -1, null, isExactTargetOnly, targetReferenceLookup);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return stats;
        }

        private string[] GetSearchAssetPaths()
        {
            string searchRoot = "Assets";
            if (m_SearchFolder != null)
            {
                string folderPath = AssetDatabase.GetAssetPath(m_SearchFolder);
                if (!string.IsNullOrEmpty(folderPath) && AssetDatabase.IsValidFolder(folderPath))
                {
                    searchRoot = folderPath;
                }
            }

            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { searchRoot });
            List<string> assetPaths = new List<string>(guids.Length);
            for (int i = 0; i < guids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath))
                {
                    continue;
                }

                assetPaths.Add(assetPath);
            }

            return assetPaths.ToArray();
        }

        // 普通资源通过 LoadAllAssetsAtPath 获取主资源和子资源，再逐个扫描其序列化字段。
        private bool ProcessGenericAssetPath(
            string assetPath,
            bool isReplace,
            bool isCollectRecords,
            ref ProcessStats stats,
            int targetRecordIndexInAssetKind = -1,
            UnityEngine.Object replacementAssetOverride = null,
            bool isExactTargetOnly = true,
            Dictionary<string, TargetReferenceRecord> targetReferenceLookup = null)
        {
            UnityEngine.Object[] objects = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            bool isAssetChanged = false;
            string assetKind = GetAssetKind(assetPath);
            int recordIndexInAssetKind = 0;

            for (int i = 0; i < objects.Length; i++)
            {
                UnityEngine.Object ownerObject = objects[i];
                if (ownerObject == null)
                {
                    continue;
                }

                stats.m_OwnerCount++;
                if (ProcessObjectReferences(
                    ownerObject,
                    assetPath,
                    assetKind,
                    isReplace,
                    isCollectRecords,
                    ref stats,
                    ref recordIndexInAssetKind,
                    targetRecordIndexInAssetKind,
                    replacementAssetOverride,
                    isExactTargetOnly,
                    targetReferenceLookup))
                {
                    isAssetChanged = true;
                    EditorUtility.SetDirty(ownerObject);
                }
            }

            if (isReplace && isAssetChanged)
            {
                AssetDatabase.ImportAsset(assetPath);
            }

            return isAssetChanged;
        }

        // Prefab 资源需要通过 LoadPrefabContents 打开可编辑副本，修改后再整体保存回资源文件。
        private bool ProcessPrefabAsset(
            string assetPath,
            bool isReplace,
            bool isCollectRecords,
            ref ProcessStats stats,
            int targetRecordIndexInAssetKind = -1,
            UnityEngine.Object replacementAssetOverride = null,
            bool isExactTargetOnly = true,
            Dictionary<string, TargetReferenceRecord> targetReferenceLookup = null)
        {
            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(assetPath);
            bool isPrefabChanged = false;
            int recordIndexInAssetKind = 0;

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

                    stats.m_OwnerCount++;
                    if (ProcessObjectReferences(
                        ownerObject,
                        assetPath,
                        "Prefab",
                        isReplace,
                        isCollectRecords,
                        ref stats,
                        ref recordIndexInAssetKind,
                        targetRecordIndexInAssetKind,
                        replacementAssetOverride,
                        isExactTargetOnly,
                        targetReferenceLookup))
                    {
                        isPrefabChanged = true;
                    }
                }

                if (isReplace && isPrefabChanged)
                {
                    PrefabUtility.SaveAsPrefabAsset(prefabRoot, assetPath);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }

            return isPrefabChanged;
        }

        // Scene 资源在扫描时会临时加载，完成后关闭；如果场景本来就已打开，则直接使用当前已加载场景。
        private bool ProcessSceneAsset(
            string assetPath,
            bool isReplace,
            bool isCollectRecords,
            ref ProcessStats stats,
            int targetRecordIndexInAssetKind = -1,
            UnityEngine.Object replacementAssetOverride = null,
            bool isExactTargetOnly = true,
            Dictionary<string, TargetReferenceRecord> targetReferenceLookup = null)
        {
            Scene scene = SceneManager.GetSceneByPath(assetPath);
            bool isOpenedByTool = false;
            bool isSceneChanged = false;
            int recordIndexInAssetKind = 0;

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

                    stats.m_OwnerCount++;
                    if (ProcessObjectReferences(
                        ownerObject,
                        assetPath,
                        "Scene",
                        isReplace,
                        isCollectRecords,
                        ref stats,
                        ref recordIndexInAssetKind,
                        targetRecordIndexInAssetKind,
                        replacementAssetOverride,
                        isExactTargetOnly,
                        targetReferenceLookup))
                    {
                        isSceneChanged = true;
                    }
                }

                if (isReplace && isSceneChanged)
                {
                    EditorSceneManager.SaveScene(scene);
                }
            }
            finally
            {
                if (isOpenedByTool && scene.IsValid())
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            return isSceneChanged;
        }

        // Importer 上也可能存在对象引用，例如部分导入参数或外部资源映射，因此单独补扫一遍。
        private bool ProcessImporterAtPath(
            string assetPath,
            bool isReplace,
            bool isCollectRecords,
            ref ProcessStats stats,
            int targetRecordIndexInAssetKind = -1,
            UnityEngine.Object replacementAssetOverride = null,
            bool isExactTargetOnly = true,
            Dictionary<string, TargetReferenceRecord> targetReferenceLookup = null)
        {
            AssetImporter importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null)
            {
                return false;
            }

            int recordIndexInAssetKind = 0;
            stats.m_OwnerCount++;
            bool isImporterChanged = ProcessObjectReferences(
                importer,
                assetPath,
                "Importer",
                isReplace,
                isCollectRecords,
                ref stats,
                ref recordIndexInAssetKind,
                targetRecordIndexInAssetKind,
                replacementAssetOverride,
                isExactTargetOnly,
                targetReferenceLookup);
            if (!isImporterChanged)
            {
                return false;
            }

            if (isReplace)
            {
                importer.SaveAndReimport();
            }

            return true;
        }

        private bool ProcessObjectReferences(
            UnityEngine.Object ownerObject,
            string assetPath,
            string assetKind,
            bool isReplace,
            bool isCollectRecords,
            ref ProcessStats stats,
            ref int recordIndexInAssetKind,
            int targetRecordIndexInAssetKind = -1,
            UnityEngine.Object replacementAssetOverride = null,
            bool isExactTargetOnly = true,
            Dictionary<string, TargetReferenceRecord> targetReferenceLookup = null)
        {
            SerializedObject serializedObject;
            try
            {
                serializedObject = new SerializedObject(ownerObject);
            }
            catch
            {
                return false;
            }

            bool isChanged = false;
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

                TargetReferenceRecord matchedTargetRecord = GetMatchedTargetRecord(referencedObject, isExactTargetOnly, targetReferenceLookup);
                if (matchedTargetRecord == null)
                {
                    continue;
                }

                stats.m_ReferenceCount++;
                int currentRecordIndex = recordIndexInAssetKind;
                if (isCollectRecords)
                {
                    m_ReferenceRecords.Add(CreateReferenceRecord(assetPath, assetKind, ownerObject, iterator.propertyPath, matchedTargetRecord, currentRecordIndex));
                }

                bool isTargetRecord = targetRecordIndexInAssetKind < 0 || currentRecordIndex == targetRecordIndexInAssetKind;
                if (isReplace && isTargetRecord && TryAssignReplacement(iterator, replacementAssetOverride))
                {
                    isChanged = true;
                    stats.m_ReplaceCount++;
                }

                recordIndexInAssetKind++;
            }

            if (isReplace && isChanged)
            {
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
            }

            return isChanged;
        }

        private ReferenceRecord CreateReferenceRecord(
            string assetPath,
            string assetKind,
            UnityEngine.Object ownerObject,
            string propertyPath,
            TargetReferenceRecord matchedTargetRecord,
            int recordIndexInAssetKind)
        {
            ReferenceRecord record = new ReferenceRecord();
            record.m_AssetPath = assetPath;
            record.m_AssetKind = assetKind;
            record.m_OwnerName = ownerObject != null ? ownerObject.name : "Unknown";
            record.m_OwnerType = ownerObject != null ? ownerObject.GetType().Name : "Unknown";
            record.m_PropertyPath = propertyPath;
            record.m_MatchedTargetPath = matchedTargetRecord != null ? matchedTargetRecord.m_AssetPath : string.Empty;
            record.m_MatchedTargetKind = matchedTargetRecord != null ? matchedTargetRecord.m_AssetKind : "Unknown";
            record.m_IsMatchedRootTarget = matchedTargetRecord != null && matchedTargetRecord.m_IsRootAsset;
            record.m_RecordIndexInAssetKind = recordIndexInAssetKind;
            return record;
        }

        // 替换时会尝试把引用改成新的资源对象；如果类型不兼容，Unity 不会真正接受赋值，此时会自动回退。
        private bool TryAssignReplacement(SerializedProperty property, UnityEngine.Object replacementAsset)
        {
            UnityEngine.Object originalValue = property.objectReferenceValue;
            property.objectReferenceValue = replacementAsset;

            if (replacementAsset == null)
            {
                if (property.objectReferenceValue == null)
                {
                    return true;
                }

                property.objectReferenceValue = originalValue;
                return false;
            }

            if (property.objectReferenceValue == replacementAsset)
            {
                return true;
            }

            property.objectReferenceValue = originalValue;
            return false;
        }

        private static void CollectSceneObjects(Scene scene, List<UnityEngine.Object> result)
        {
            GameObject[] rootObjects = scene.GetRootGameObjects();
            for (int i = 0; i < rootObjects.Length; i++)
            {
                CollectHierarchyObjects(rootObjects[i], result);
            }
        }

        // 递归收集层级中的 GameObject 和全部 Component，确保不会漏掉场景或 Prefab 节点上的序列化引用。
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

        private List<TargetReferenceRecord> CollectDirectTargetReferences(string assetPath, int depth)
        {
            List<TargetReferenceRecord> result = new List<TargetReferenceRecord>();
            HashSet<string> recordedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(assetPath))
            {
                return result;
            }

            if (IsScenePath(assetPath))
            {
                CollectSceneTargetReferences(assetPath, depth, result, recordedPaths);
                return result;
            }

            if (IsPrefabPath(assetPath))
            {
                CollectPrefabTargetReferences(assetPath, depth, result, recordedPaths);
            }
            else
            {
                CollectGenericTargetReferences(assetPath, depth, result, recordedPaths);
            }

            if (m_IncludeImporterSettings)
            {
                CollectImporterTargetReferences(assetPath, depth, result, recordedPaths);
            }

            return result;
        }

        private void CollectGenericTargetReferences(string assetPath, int depth, List<TargetReferenceRecord> result, HashSet<string> recordedPaths)
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

                CollectObjectTargetReferences(ownerObject, assetPath, assetKind, depth, result, recordedPaths);
            }
        }

        private void CollectPrefabTargetReferences(string assetPath, int depth, List<TargetReferenceRecord> result, HashSet<string> recordedPaths)
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

                    CollectObjectTargetReferences(ownerObject, assetPath, "Prefab", depth, result, recordedPaths);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        private void CollectSceneTargetReferences(string assetPath, int depth, List<TargetReferenceRecord> result, HashSet<string> recordedPaths)
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

                    CollectObjectTargetReferences(ownerObject, assetPath, "Scene", depth, result, recordedPaths);
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

        private void CollectImporterTargetReferences(string assetPath, int depth, List<TargetReferenceRecord> result, HashSet<string> recordedPaths)
        {
            AssetImporter importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null)
            {
                return;
            }

            CollectObjectTargetReferences(importer, assetPath, "Importer", depth, result, recordedPaths);
        }

        private void CollectObjectTargetReferences(
            UnityEngine.Object ownerObject,
            string sourceAssetPath,
            string sourceAssetKind,
            int depth,
            List<TargetReferenceRecord> result,
            HashSet<string> recordedPaths)
        {
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
                if (string.IsNullOrEmpty(referencedAssetPath) ||
                    AssetDatabase.IsValidFolder(referencedAssetPath) ||
                    string.Equals(referencedAssetPath, sourceAssetPath, StringComparison.OrdinalIgnoreCase) ||
                    recordedPaths.Contains(referencedAssetPath))
                {
                    continue;
                }

                recordedPaths.Add(referencedAssetPath);

                TargetReferenceRecord record = new TargetReferenceRecord();
                record.m_AssetPath = referencedAssetPath;
                record.m_AssetKind = GetAssetKind(referencedAssetPath);
                record.m_SourceAssetPath = sourceAssetPath;
                record.m_SourceAssetKind = sourceAssetKind;
                record.m_OwnerName = ownerObject != null ? ownerObject.name : "Unknown";
                record.m_OwnerType = ownerObject != null ? ownerObject.GetType().Name : "Unknown";
                record.m_PropertyPath = iterator.propertyPath;
                record.m_Depth = depth;
                result.Add(record);
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
