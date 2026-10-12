// ================================================================
// Prefab MeshRenderer 批次分析工具
//
// 文件名称: PrefabMeshRendererBatchAnalyzerWindow.cs
// 版本: 1.0
//
// 描述:
// 该工具用于扫描指定 Prefab 资源中的全部 MeshRenderer，并按照
// “单个 Renderer 的 batches = 该 Renderer 的材质槽位数量”
// 的规则统计整个 Prefab 的 batches 总和。
//
// 同时，工具会把使用了相同材质的 MeshRenderer 归并到同一个列表中显示，
// 便于快速查看某个材质被哪些 Renderer 共同使用。
// 若同一个 Renderer 使用了多个材质，则会同时出现在多个材质分组中。
//
// 使用方式:
// 1. 打开菜单 Tools/Azumi/Prefab工具/Prefab MeshRenderer 批次分析。
// 2. 在窗口中指定一个 Prefab 资源。
// 3. 点击“扫描 Prefab”。
// 4. 在结果区域查看 MeshRenderer 总数、估算 batches 总数，以及按材质分组后的 Renderer 列表。
//
// 注意事项:
// 1. 本工具只扫描 MeshRenderer，不包含 SkinnedMeshRenderer。
// 2. batches 统计依据为 MeshRenderer.sharedMaterials.Length，不会触发运行时材质实例化。
// 3. 若某个材质槽位为空，工具会把它归到“空材质槽位”分组中，便于排查缺失材质的问题。
//
// 修改历史:
// 2026-06-11 - 初始版本
// ================================================================

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AzumisEditorTools
{
    public class PrefabMeshRendererBatchAnalyzerWindow : EditorWindow
    {
        private sealed class RendererUsageRecord
        {
            public string m_RendererName = string.Empty;
            public string m_TransformPath = string.Empty;
            public int m_MaterialSlotCount;
            public int m_BatchCount;
        }

        private sealed class MaterialUsageGroup
        {
            public string m_GroupName = string.Empty;
            public string m_MaterialPath = string.Empty;
            public Material m_Material;
            public bool m_IsExpanded = true;
            public int m_TotalMaterialSlotUsageCount;
            public readonly List<RendererUsageRecord> m_RendererRecords = new List<RendererUsageRecord>();
        }

        private const string m_WindowTitle = "Prefab MeshRenderer 批次分析";
        private const string m_MenuPath = "Tools/Azumi/Prefab工具/Prefab MeshRenderer 批次分析";

        private GameObject m_TargetPrefab;
        private Vector2 m_ScrollPosition;
        private string m_LastSummary = string.Empty;
        private string m_TargetPrefabPath = string.Empty;
        private int m_TotalMeshRendererCount;
        private int m_TotalBatchCount;
        private bool m_IsRendererListExpanded = true;
        private bool m_IsMaterialGroupListExpanded = true;
        private readonly List<RendererUsageRecord> m_RendererRecords = new List<RendererUsageRecord>();
        private readonly List<MaterialUsageGroup> m_MaterialUsageGroups = new List<MaterialUsageGroup>();

        [MenuItem(m_MenuPath)]
        public static void ShowWindow()
        {
            PrefabMeshRendererBatchAnalyzerWindow window = GetWindow<PrefabMeshRendererBatchAnalyzerWindow>(m_WindowTitle);
            window.minSize = new Vector2(820f, 620f);
            window.Show();
        }

        private void OnGUI()
        {
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);
            DrawInputSection();
            EditorGUILayout.Space(8f);
            DrawSummarySection();
            EditorGUILayout.Space(8f);
            DrawRendererResultSection();
            EditorGUILayout.Space(8f);
            DrawMaterialGroupSection();
            EditorGUILayout.EndScrollView();
        }

        // 输入区用于指定目标 Prefab，并触发一次完整扫描。
        private void DrawInputSection()
        {
            EditorGUILayout.LabelField("扫描设置", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            m_TargetPrefab = (GameObject)EditorGUILayout.ObjectField("目标 Prefab", m_TargetPrefab, typeof(GameObject), false);
            EditorGUILayout.HelpBox(
                "工具会扫描 Prefab 资源中的全部 MeshRenderer，按 sharedMaterials.Length 统计每个 Renderer 的 batches，" +
                "并把引用到同一材质的 MeshRenderer 归到同一个列表中显示。",
                MessageType.Info);

            if (GUILayout.Button("扫描 Prefab", GUILayout.Height(28f)))
            {
                ScanPrefab();
            }

            EditorGUILayout.EndVertical();
        }

        // 汇总区展示本次扫描的 Prefab 路径、Renderer 数量和按规则估算出的 batches 总数。
        private void DrawSummarySection()
        {
            EditorGUILayout.LabelField("扫描汇总", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.LabelField("MeshRenderer 总数", m_TotalMeshRendererCount.ToString());
            EditorGUILayout.LabelField("Batches 总和", m_TotalBatchCount.ToString());
            EditorGUILayout.LabelField("材质分组数", m_MaterialUsageGroups.Count.ToString());

            if (!string.IsNullOrEmpty(m_TargetPrefabPath))
            {
                EditorGUILayout.TextField("Prefab 路径", m_TargetPrefabPath);
            }

            if (!string.IsNullOrEmpty(m_LastSummary))
            {
                EditorGUILayout.HelpBox(m_LastSummary, MessageType.None);
            }

            EditorGUILayout.EndVertical();
        }

        // 该区域用于逐条列出 Prefab 中扫描到的全部 MeshRenderer，方便核对总 batches 来源。
        private void DrawRendererResultSection()
        {
            EditorGUILayout.LabelField("Renderer 列表", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            m_IsRendererListExpanded = EditorGUILayout.Foldout(
                m_IsRendererListExpanded,
                "全部 MeshRenderer (" + m_RendererRecords.Count + ")",
                true);

            if (!m_IsRendererListExpanded)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            if (m_RendererRecords.Count == 0)
            {
                EditorGUILayout.HelpBox("当前没有可显示的 Renderer 结果，请先执行一次扫描。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < m_RendererRecords.Count; i++)
            {
                DrawRendererRecord(m_RendererRecords[i], i);
            }

            EditorGUILayout.EndVertical();
        }

        // 该区域按照材质进行分组展示，同一个 Renderer 可出现在多个不同的材质分组中。
        private void DrawMaterialGroupSection()
        {
            EditorGUILayout.LabelField("材质分组", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            m_IsMaterialGroupListExpanded = EditorGUILayout.Foldout(
                m_IsMaterialGroupListExpanded,
                "材质列表 (" + m_MaterialUsageGroups.Count + ")",
                true);

            if (!m_IsMaterialGroupListExpanded)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            if (m_MaterialUsageGroups.Count == 0)
            {
                EditorGUILayout.HelpBox("当前没有可显示的材质分组结果，请先执行一次扫描。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            for (int i = 0; i < m_MaterialUsageGroups.Count; i++)
            {
                DrawMaterialUsageGroup(m_MaterialUsageGroups[i], i);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawRendererRecord(RendererUsageRecord rendererRecord, int index)
        {
            if (rendererRecord == null)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Renderer " + (index + 1), EditorStyles.boldLabel);
            EditorGUILayout.LabelField("名称", rendererRecord.m_RendererName);
            EditorGUILayout.TextField("层级路径", rendererRecord.m_TransformPath);
            EditorGUILayout.LabelField("材质槽位数", rendererRecord.m_MaterialSlotCount.ToString());
            EditorGUILayout.LabelField("Batches", rendererRecord.m_BatchCount.ToString());
            EditorGUILayout.EndVertical();
        }

        private void DrawMaterialUsageGroup(MaterialUsageGroup materialUsageGroup, int index)
        {
            if (materialUsageGroup == null)
            {
                return;
            }

            EditorGUILayout.BeginVertical("box");
            materialUsageGroup.m_IsExpanded = EditorGUILayout.Foldout(
                materialUsageGroup.m_IsExpanded,
                "材质 " + (index + 1) + " - " + materialUsageGroup.m_GroupName +
                " (Renderer: " + materialUsageGroup.m_RendererRecords.Count +
                "，槽位引用: " + materialUsageGroup.m_TotalMaterialSlotUsageCount + ")",
                true);

            if (!materialUsageGroup.m_IsExpanded)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("材质资源", materialUsageGroup.m_Material, typeof(Material), false);
            }

            if (!string.IsNullOrEmpty(materialUsageGroup.m_MaterialPath))
            {
                EditorGUILayout.TextField("材质路径", materialUsageGroup.m_MaterialPath);
            }

            EditorGUILayout.LabelField("命中的 Renderer 数", materialUsageGroup.m_RendererRecords.Count.ToString());
            EditorGUILayout.LabelField("材质槽位引用数", materialUsageGroup.m_TotalMaterialSlotUsageCount.ToString());

            for (int i = 0; i < materialUsageGroup.m_RendererRecords.Count; i++)
            {
                RendererUsageRecord rendererRecord = materialUsageGroup.m_RendererRecords[i];
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("Renderer " + (i + 1), EditorStyles.boldLabel);
                EditorGUILayout.LabelField("名称", rendererRecord.m_RendererName);
                EditorGUILayout.TextField("层级路径", rendererRecord.m_TransformPath);
                EditorGUILayout.LabelField("材质槽位数", rendererRecord.m_MaterialSlotCount.ToString());
                EditorGUILayout.LabelField("Batches", rendererRecord.m_BatchCount.ToString());
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndVertical();
        }

        private void ScanPrefab()
        {
            if (!ValidateTargetPrefab(out string prefabAssetPath))
            {
                return;
            }

            m_TargetPrefabPath = prefabAssetPath;
            m_RendererRecords.Clear();
            m_MaterialUsageGroups.Clear();
            m_TotalMeshRendererCount = 0;
            m_TotalBatchCount = 0;
            m_LastSummary = string.Empty;

            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(prefabAssetPath);
            try
            {
                MeshRenderer[] meshRenderers = prefabRoot.GetComponentsInChildren<MeshRenderer>(true);
                m_TotalMeshRendererCount = meshRenderers.Length;

                Dictionary<int, MaterialUsageGroup> materialGroupMap = new Dictionary<int, MaterialUsageGroup>();
                MaterialUsageGroup nullMaterialGroup = null;

                for (int i = 0; i < meshRenderers.Length; i++)
                {
                    MeshRenderer meshRenderer = meshRenderers[i];
                    if (meshRenderer == null)
                    {
                        continue;
                    }

                    RendererUsageRecord rendererRecord = CreateRendererUsageRecord(prefabRoot.transform, meshRenderer);
                    m_RendererRecords.Add(rendererRecord);
                    m_TotalBatchCount += rendererRecord.m_BatchCount;

                    Material[] sharedMaterials = meshRenderer.sharedMaterials;
                    if (sharedMaterials == null || sharedMaterials.Length == 0)
                    {
                        continue;
                    }

                    HashSet<int> addedMaterialInstanceIds = new HashSet<int>();
                    bool isAddedToNullGroup = false;

                    for (int j = 0; j < sharedMaterials.Length; j++)
                    {
                        Material material = sharedMaterials[j];
                        if (material == null)
                        {
                            if (nullMaterialGroup == null)
                            {
                                nullMaterialGroup = CreateNullMaterialUsageGroup();
                                m_MaterialUsageGroups.Add(nullMaterialGroup);
                            }

                            nullMaterialGroup.m_TotalMaterialSlotUsageCount++;
                            if (!isAddedToNullGroup)
                            {
                                nullMaterialGroup.m_RendererRecords.Add(rendererRecord);
                                isAddedToNullGroup = true;
                            }

                            continue;
                        }

                        int materialInstanceId = material.GetInstanceID();
                        MaterialUsageGroup materialUsageGroup = GetOrCreateMaterialUsageGroup(material, materialGroupMap);
                        materialUsageGroup.m_TotalMaterialSlotUsageCount++;

                        if (addedMaterialInstanceIds.Add(materialInstanceId))
                        {
                            materialUsageGroup.m_RendererRecords.Add(rendererRecord);
                        }
                    }
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }

            SortScanResults();
            m_LastSummary =
                "扫描完成。MeshRenderer 总数: " + m_TotalMeshRendererCount +
                "，Batches 总和: " + m_TotalBatchCount +
                "，材质分组数: " + m_MaterialUsageGroups.Count + "。";
        }

        // 仅允许 Project 视图中的 Prefab 资源进入扫描流程，避免把场景对象误当成目标资源。
        private bool ValidateTargetPrefab(out string prefabAssetPath)
        {
            prefabAssetPath = string.Empty;
            if (m_TargetPrefab == null)
            {
                EditorUtility.DisplayDialog("提示", "请先指定目标 Prefab。", "确定");
                return false;
            }

            prefabAssetPath = AssetDatabase.GetAssetPath(m_TargetPrefab);
            if (string.IsNullOrEmpty(prefabAssetPath))
            {
                EditorUtility.DisplayDialog("提示", "目标对象必须是 Project 视图中的 Prefab 资源。", "确定");
                return false;
            }

            PrefabAssetType prefabAssetType = PrefabUtility.GetPrefabAssetType(m_TargetPrefab);
            if (prefabAssetType == PrefabAssetType.NotAPrefab)
            {
                EditorUtility.DisplayDialog("提示", "当前选择的资源不是有效的 Prefab 资源。", "确定");
                return false;
            }

            return true;
        }

        // 每个 MeshRenderer 的 batches 按材质槽位数量计算，因此这里直接记录 sharedMaterials.Length。
        private static RendererUsageRecord CreateRendererUsageRecord(Transform prefabRootTransform, MeshRenderer meshRenderer)
        {
            RendererUsageRecord rendererRecord = new RendererUsageRecord();
            rendererRecord.m_RendererName = meshRenderer.name;
            rendererRecord.m_TransformPath = GetTransformPath(prefabRootTransform, meshRenderer.transform);
            rendererRecord.m_MaterialSlotCount = meshRenderer.sharedMaterials != null ? meshRenderer.sharedMaterials.Length : 0;
            rendererRecord.m_BatchCount = rendererRecord.m_MaterialSlotCount;
            return rendererRecord;
        }

        private MaterialUsageGroup GetOrCreateMaterialUsageGroup(Material material, Dictionary<int, MaterialUsageGroup> materialGroupMap)
        {
            int materialInstanceId = material.GetInstanceID();
            if (materialGroupMap.TryGetValue(materialInstanceId, out MaterialUsageGroup materialUsageGroup))
            {
                return materialUsageGroup;
            }

            materialUsageGroup = new MaterialUsageGroup();
            materialUsageGroup.m_Material = material;
            materialUsageGroup.m_GroupName = material.name;
            materialUsageGroup.m_MaterialPath = AssetDatabase.GetAssetPath(material);

            materialGroupMap.Add(materialInstanceId, materialUsageGroup);
            m_MaterialUsageGroups.Add(materialUsageGroup);
            return materialUsageGroup;
        }

        private static MaterialUsageGroup CreateNullMaterialUsageGroup()
        {
            MaterialUsageGroup materialUsageGroup = new MaterialUsageGroup();
            materialUsageGroup.m_GroupName = "空材质槽位";
            materialUsageGroup.m_MaterialPath = string.Empty;
            materialUsageGroup.m_Material = null;
            return materialUsageGroup;
        }

        // 结果排序优先保证同一材质分组内的层级路径可读，同时让命中 Renderer 更多的材质排在前面。
        private void SortScanResults()
        {
            m_RendererRecords.Sort(CompareRendererUsageRecord);

            for (int i = 0; i < m_MaterialUsageGroups.Count; i++)
            {
                m_MaterialUsageGroups[i].m_RendererRecords.Sort(CompareRendererUsageRecord);
            }

            m_MaterialUsageGroups.Sort(CompareMaterialUsageGroup);
        }

        private static int CompareRendererUsageRecord(RendererUsageRecord left, RendererUsageRecord right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }

            if (right == null)
            {
                return -1;
            }

            int pathCompare = string.Compare(left.m_TransformPath, right.m_TransformPath, StringComparison.OrdinalIgnoreCase);
            if (pathCompare != 0)
            {
                return pathCompare;
            }

            return string.Compare(left.m_RendererName, right.m_RendererName, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareMaterialUsageGroup(MaterialUsageGroup left, MaterialUsageGroup right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return 1;
            }

            if (right == null)
            {
                return -1;
            }

            int rendererCountCompare = right.m_RendererRecords.Count.CompareTo(left.m_RendererRecords.Count);
            if (rendererCountCompare != 0)
            {
                return rendererCountCompare;
            }

            return string.Compare(left.m_GroupName, right.m_GroupName, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetTransformPath(Transform rootTransform, Transform currentTransform)
        {
            if (rootTransform == null || currentTransform == null)
            {
                return string.Empty;
            }

            if (rootTransform == currentTransform)
            {
                return currentTransform.name;
            }

            Stack<string> pathParts = new Stack<string>();
            Transform walker = currentTransform;
            while (walker != null)
            {
                pathParts.Push(walker.name);
                if (walker == rootTransform)
                {
                    break;
                }

                walker = walker.parent;
            }

            return string.Join("/", pathParts.ToArray());
        }
    }
}
