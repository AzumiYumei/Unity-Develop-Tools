using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AzumisEditorTools.MaterialShaderVariantCollector
{
    /// <summary>
    /// 扫描指定目录中的材质关键字组合，并生成 ShaderVariantCollection。
    /// </summary>
    public sealed class MaterialShaderVariantCollectorWindow : EditorWindow
    {
        private sealed class MaterialUsage
        {
            public Material m_Material;
            public string m_AssetPath = string.Empty;
            public string[] m_InvalidKeywords = Array.Empty<string>();
        }

        private sealed class VariantUsage
        {
            public string m_Key = string.Empty;
            public string[] m_Keywords = Array.Empty<string>();
            public string[] m_MaterialKeywords = Array.Empty<string>();
            public string[] m_DerivedKeywords = Array.Empty<string>();
            public bool m_IsExpanded;
            public readonly List<MaterialUsage> m_Materials = new List<MaterialUsage>();
        }

        private sealed class PassUsage
        {
            public string m_PassName = string.Empty;
            public string m_LightMode = string.Empty;
            public PassType m_PassType;
        }

        private sealed class ShaderUsage
        {
            public Shader m_Shader;
            public string m_ShaderPath = string.Empty;
            public bool m_IsExpanded = true;
            public string m_PassWarning = string.Empty;
            public string[] m_DerivedKeywords = Array.Empty<string>();
            public readonly List<PassUsage> m_Passes = new List<PassUsage>();
            public readonly List<PassType> m_UniquePassTypes = new List<PassType>();
            public readonly List<VariantUsage> m_Variants = new List<VariantUsage>();
        }

        private const string MenuPath = "Tools/Azumi/Shader工具/材质 Shader Variant 收集器";
        private const string WindowTitle = "材质 Shader Variant 收集器";
        private static readonly ShaderTagId LightModeTag = new ShaderTagId("LightMode");

        [SerializeField] private List<DefaultAsset> m_TargetFolders = new List<DefaultAsset>();
        [SerializeField] private bool m_ShowPassDetails = true;
        [SerializeField] private bool m_ShowMaterials = true;
        [SerializeField] private bool m_IncludeEnabledGlobalKeywords = true;
        [SerializeField] private bool m_IncludeUrpDerivedKeywords = true;
        [SerializeField] private string m_SearchText = string.Empty;

        private readonly List<ShaderUsage> m_ShaderUsages = new List<ShaderUsage>();
        private Vector2 m_ResultScrollPosition;
        private string m_Status = string.Empty;
        private int m_ScannedMaterialCount;
        private int m_KeywordCombinationCount;
        private int m_CollectionVariantCount;
        private int m_InvalidKeywordMaterialCount;

        [MenuItem(MenuPath)]
        private static void ShowWindow()
        {
            MaterialShaderVariantCollectorWindow window = GetWindow<MaterialShaderVariantCollectorWindow>(WindowTitle);
            window.minSize = new Vector2(760f, 560f);
            window.Show();
        }

        private void OnGUI()
        {
            DrawFolderSettings();
            EditorGUILayout.Space(8f);
            DrawActionsAndSummary();
            EditorGUILayout.Space(8f);
            DrawResults();
        }

        private void DrawFolderSettings()
        {
            EditorGUILayout.LabelField("扫描目录", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope("box"))
            {
                for (int i = 0; i < m_TargetFolders.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        m_TargetFolders[i] = (DefaultAsset)EditorGUILayout.ObjectField(
                            string.Format("目录 {0}", i + 1),
                            m_TargetFolders[i],
                            typeof(DefaultAsset),
                            false);

                        if (GUILayout.Button("移除", GUILayout.Width(54f)))
                        {
                            m_TargetFolders.RemoveAt(i);
                            GUIUtility.ExitGUI();
                        }
                    }

                    DefaultAsset folder = m_TargetFolders[i];
                    if (folder != null && !AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(folder)))
                    {
                        EditorGUILayout.HelpBox("该资源不是文件夹，将在扫描时忽略。", MessageType.Warning);
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("添加空目录项"))
                    {
                        m_TargetFolders.Add(null);
                    }

                    if (GUILayout.Button("添加当前选中的文件夹"))
                    {
                        AddSelectedFolders();
                    }

                    if (GUILayout.Button("添加 Assets 根目录"))
                    {
                        AddFolder(AssetDatabase.LoadAssetAtPath<DefaultAsset>("Assets"));
                    }

                    using (new EditorGUI.DisabledScope(m_TargetFolders.Count == 0))
                    {
                        if (GUILayout.Button("清空", GUILayout.Width(54f)))
                        {
                            m_TargetFolders.Clear();
                        }
                    }
                }

                EditorGUILayout.HelpBox(
                    "支持同时指定多个目录，并会自动去除嵌套目录产生的重复材质。扫描包含子目录及模型资源中的 Material 子资源。",
                    MessageType.Info);

                EditorGUILayout.Space(5f);
                EditorGUILayout.LabelField("关键字来源", EditorStyles.boldLabel);
                m_IncludeEnabledGlobalKeywords = EditorGUILayout.ToggleLeft(
                    "合并当前启用的全局 Shader 关键字",
                    m_IncludeEnabledGlobalKeywords);
                m_IncludeUrpDerivedKeywords = EditorGUILayout.ToggleLeft(
                    "根据当前 URP Asset 和构建平台补充管线派生关键字",
                    m_IncludeUrpDerivedKeywords);
                EditorGUILayout.HelpBox(
                    "材质资源只保存本地关键字。EVALUATE_SH_VERTEX、灯光、雾和 Lightmap 等关键字由渲染管线或运行时全局状态提供。工具会把当前可确定的全局关键字合并进最终变体；运行过程中动态切换出的全部组合仍应以 Unity 的实际运行收集结果为准。",
                    MessageType.Info);
            }
        }

        private void DrawActionsAndSummary()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("扫描材质变体", GUILayout.Height(32f)))
                {
                    ScanMaterials();
                }

                using (new EditorGUI.DisabledScope(m_ShaderUsages.Count == 0))
                {
                    if (GUILayout.Button("一键生成 ShaderVariantCollection", GUILayout.Height(32f)))
                    {
                        GenerateShaderVariantCollection();
                    }
                }
            }

            if (m_ShaderUsages.Count > 0)
            {
                EditorGUILayout.LabelField(
                    string.Format(
                        "已扫描材质 {0} 个，Shader {1} 个，关键字组合 {2} 个，可生成变体 {3} 个，无效关键字材质 {4} 个。",
                        m_ScannedMaterialCount,
                        m_ShaderUsages.Count,
                        m_KeywordCombinationCount,
                        m_CollectionVariantCount,
                        m_InvalidKeywordMaterialCount),
                    EditorStyles.wordWrappedLabel);
            }

            if (!string.IsNullOrEmpty(m_Status))
            {
                EditorGUILayout.HelpBox(m_Status, MessageType.None);
            }
        }

        private void DrawResults()
        {
            EditorGUILayout.LabelField("扫描结果", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("搜索", GUILayout.Width(34f));
                m_SearchText = GUILayout.TextField(m_SearchText, GUI.skin.FindStyle("ToolbarSearchTextField"));

                if (GUILayout.Button("展开", EditorStyles.toolbarButton, GUILayout.Width(46f)))
                {
                    SetAllExpanded(true);
                }

                if (GUILayout.Button("折叠", EditorStyles.toolbarButton, GUILayout.Width(46f)))
                {
                    SetAllExpanded(false);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                m_ShowPassDetails = EditorGUILayout.ToggleLeft("显示 Shader Pass", m_ShowPassDetails, GUILayout.Width(130f));
                m_ShowMaterials = EditorGUILayout.ToggleLeft("显示使用材质", m_ShowMaterials, GUILayout.Width(130f));
            }

            m_ResultScrollPosition = EditorGUILayout.BeginScrollView(m_ResultScrollPosition);
            foreach (ShaderUsage shaderUsage in m_ShaderUsages)
            {
                DrawShaderUsage(shaderUsage);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawShaderUsage(ShaderUsage shaderUsage)
        {
            List<VariantUsage> visibleVariants = shaderUsage.m_Variants
                .Where(variant => MatchesSearch(shaderUsage, variant))
                .ToList();
            if (visibleVariants.Count == 0)
            {
                return;
            }

            int materialCount = shaderUsage.m_Variants.Sum(variant => variant.m_Materials.Count);
            string title = string.Format(
                "{0}  ·  {1} 个材质 / {2} 个关键字组合 / {3} 个 PassType",
                shaderUsage.m_Shader == null ? "<Missing Shader>" : shaderUsage.m_Shader.name,
                materialCount,
                shaderUsage.m_Variants.Count,
                shaderUsage.m_UniquePassTypes.Count);

            using (new EditorGUILayout.VerticalScope("box"))
            {
                shaderUsage.m_IsExpanded = EditorGUILayout.Foldout(shaderUsage.m_IsExpanded, title, true, EditorStyles.foldoutHeader);
                if (!shaderUsage.m_IsExpanded)
                {
                    return;
                }

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("Shader", shaderUsage.m_Shader, typeof(Shader), false);
                }

                EditorGUILayout.LabelField("路径：" + shaderUsage.m_ShaderPath, EditorStyles.wordWrappedMiniLabel);

                if (!string.IsNullOrEmpty(shaderUsage.m_PassWarning))
                {
                    EditorGUILayout.HelpBox(shaderUsage.m_PassWarning, MessageType.Warning);
                }

                if (m_ShowPassDetails)
                {
                    DrawPassDetails(shaderUsage);
                }

                foreach (VariantUsage variant in visibleVariants)
                {
                    DrawVariantUsage(shaderUsage, variant);
                }
            }
        }

        private static void DrawPassDetails(ShaderUsage shaderUsage)
        {
            EditorGUILayout.LabelField("有效 SubShader Pass", EditorStyles.boldLabel);
            foreach (PassUsage pass in shaderUsage.m_Passes)
            {
                string lightMode = string.IsNullOrEmpty(pass.m_LightMode) ? "<空>" : pass.m_LightMode;
                EditorGUILayout.LabelField(
                    string.Format("• {0}  |  LightMode: {1}  |  SVC PassType: {2}", pass.m_PassName, lightMode, pass.m_PassType),
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void DrawVariantUsage(ShaderUsage shaderUsage, VariantUsage variant)
        {
            string variantTitle = string.Format(
                "关键字组合 · {0} 个材质 · {1} 个集合变体",
                variant.m_Materials.Count,
                shaderUsage.m_UniquePassTypes.Count);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                variant.m_IsExpanded = EditorGUILayout.Foldout(variant.m_IsExpanded, variantTitle, true);
                if (!variant.m_IsExpanded)
                {
                    return;
                }

                string keywordText = variant.m_Keywords.Length == 0
                    ? "<无关键字>"
                    : string.Join("  ", variant.m_Keywords);
                EditorGUILayout.LabelField("最终变体关键字：" + keywordText, EditorStyles.wordWrappedLabel);

                string materialKeywordText = variant.m_MaterialKeywords.Length == 0
                    ? "<无>"
                    : string.Join("  ", variant.m_MaterialKeywords);
                EditorGUILayout.LabelField("材质关键字：" + materialKeywordText, EditorStyles.wordWrappedMiniLabel);

                string derivedKeywordText = variant.m_DerivedKeywords.Length == 0
                    ? "<无>"
                    : string.Join("  ", variant.m_DerivedKeywords);
                EditorGUILayout.LabelField("管线/全局关键字：" + derivedKeywordText, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField(
                    "PassType：" + string.Join(", ", shaderUsage.m_UniquePassTypes.Select(passType => passType.ToString())),
                    EditorStyles.wordWrappedMiniLabel);

                if (!m_ShowMaterials)
                {
                    return;
                }

                EditorGUILayout.LabelField("使用该组合的材质", EditorStyles.boldLabel);
                foreach (MaterialUsage materialUsage in variant.m_Materials)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(true))
                        {
                            EditorGUILayout.ObjectField(materialUsage.m_Material, typeof(Material), false);
                        }

                        if (GUILayout.Button("定位", GUILayout.Width(44f)))
                        {
                            Selection.activeObject = materialUsage.m_Material;
                            EditorGUIUtility.PingObject(materialUsage.m_Material);
                        }
                    }

                    EditorGUILayout.LabelField(materialUsage.m_AssetPath, EditorStyles.wordWrappedMiniLabel);
                    if (materialUsage.m_InvalidKeywords.Length > 0)
                    {
                        EditorGUILayout.HelpBox(
                            "材质遗留的无效关键字（不会写入变体）：" + string.Join("  ", materialUsage.m_InvalidKeywords),
                            MessageType.Warning);
                    }
                }
            }
        }

        private void AddSelectedFolders()
        {
            foreach (UnityEngine.Object selectedObject in Selection.objects)
            {
                string selectedPath = AssetDatabase.GetAssetPath(selectedObject);
                if (!AssetDatabase.IsValidFolder(selectedPath))
                {
                    continue;
                }

                AddFolder(AssetDatabase.LoadAssetAtPath<DefaultAsset>(selectedPath));
            }
        }

        private void AddFolder(DefaultAsset folder)
        {
            if (folder == null)
            {
                return;
            }

            string folderPath = AssetDatabase.GetAssetPath(folder);
            bool alreadyAdded = m_TargetFolders.Any(existingFolder =>
                existingFolder != null &&
                string.Equals(AssetDatabase.GetAssetPath(existingFolder), folderPath, StringComparison.OrdinalIgnoreCase));
            if (!alreadyAdded)
            {
                m_TargetFolders.Add(folder);
            }
        }

        private void ScanMaterials()
        {
            string[] validFolders = GetValidFolderPaths();
            if (validFolders.Length == 0)
            {
                EditorUtility.DisplayDialog("缺少扫描目录", "请至少指定一个有效的 Assets 文件夹。", "确定");
                return;
            }

            try
            {
                List<Material> materials = CollectMaterials(validFolders);
                BuildScanResults(materials);
                m_Status = string.Format("扫描完成：共找到 {0} 个材质。", m_ScannedMaterialCount);
            }
            catch (OperationCanceledException)
            {
                ClearResults();
                m_Status = "扫描已取消。";
            }
            catch (Exception exception)
            {
                ClearResults();
                m_Status = "扫描失败：" + exception.Message;
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("扫描失败", exception.Message, "确定");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        private string[] GetValidFolderPaths()
        {
            return m_TargetFolders
                .Where(folder => folder != null)
                .Select(AssetDatabase.GetAssetPath)
                .Where(AssetDatabase.IsValidFolder)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static List<Material> CollectMaterials(string[] folderPaths)
        {
            HashSet<string> assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string guid in AssetDatabase.FindAssets("t:Material", folderPaths))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    assetPaths.Add(assetPath);
                }
            }

            List<Material> materials = new List<Material>();
            HashSet<string> materialIdentifiers = new HashSet<string>(StringComparer.Ordinal);
            string[] sortedPaths = assetPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            for (int i = 0; i < sortedPaths.Length; i++)
            {
                if ((i & 15) == 0 && EditorUtility.DisplayCancelableProgressBar(
                        "收集材质",
                        sortedPaths[i],
                        sortedPaths.Length == 0 ? 1f : i / (float)sortedPaths.Length))
                {
                    throw new OperationCanceledException();
                }

                foreach (Material material in AssetDatabase.LoadAllAssetsAtPath(sortedPaths[i]).OfType<Material>())
                {
                    string identifier = GetMaterialIdentifier(material, sortedPaths[i]);
                    if (materialIdentifiers.Add(identifier))
                    {
                        materials.Add(material);
                    }
                }
            }

            return materials
                .OrderBy(material => AssetDatabase.GetAssetPath(material), StringComparer.OrdinalIgnoreCase)
                .ThenBy(material => material.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string GetMaterialIdentifier(Material material, string assetPath)
        {
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long localId))
            {
                return guid + ":" + localId;
            }

            return assetPath + ":" + material.name;
        }

        private void BuildScanResults(List<Material> materials)
        {
            ClearResults();
            Dictionary<Shader, ShaderUsage> shaderLookup = new Dictionary<Shader, ShaderUsage>();
            Dictionary<Shader, Dictionary<string, VariantUsage>> variantLookups =
                new Dictionary<Shader, Dictionary<string, VariantUsage>>();

            for (int i = 0; i < materials.Count; i++)
            {
                Material material = materials[i];
                if ((i & 15) == 0 && EditorUtility.DisplayCancelableProgressBar(
                        "分析材质关键字",
                        material == null ? "<Missing Material>" : material.name,
                        materials.Count == 0 ? 1f : i / (float)materials.Count))
                {
                    throw new OperationCanceledException();
                }

                if (material == null || material.shader == null)
                {
                    continue;
                }

                Shader shader = material.shader;
                if (!shaderLookup.TryGetValue(shader, out ShaderUsage shaderUsage))
                {
                    shaderUsage = CreateShaderUsage(shader);
                    shaderUsage.m_DerivedKeywords = GetDerivedKeywords(shader);
                    shaderLookup.Add(shader, shaderUsage);
                    variantLookups.Add(shader, new Dictionary<string, VariantUsage>(StringComparer.Ordinal));
                }

                GetMaterialKeywords(material, shader, out string[] materialKeywords, out string[] invalidKeywords);
                string[] finalKeywords = materialKeywords
                    .Concat(shaderUsage.m_DerivedKeywords)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(keyword => keyword, StringComparer.Ordinal)
                    .ToArray();
                string variantKey = finalKeywords.Length == 0 ? "<NO_KEYWORDS>" : string.Join("\u001F", finalKeywords);
                Dictionary<string, VariantUsage> variantLookup = variantLookups[shader];
                if (!variantLookup.TryGetValue(variantKey, out VariantUsage variantUsage))
                {
                    variantUsage = new VariantUsage
                    {
                        m_Key = variantKey,
                        m_Keywords = finalKeywords,
                        m_MaterialKeywords = materialKeywords,
                        m_DerivedKeywords = shaderUsage.m_DerivedKeywords
                    };
                    variantLookup.Add(variantKey, variantUsage);
                    shaderUsage.m_Variants.Add(variantUsage);
                }

                variantUsage.m_Materials.Add(new MaterialUsage
                {
                    m_Material = material,
                    m_AssetPath = AssetDatabase.GetAssetPath(material),
                    m_InvalidKeywords = invalidKeywords
                });

                if (invalidKeywords.Length > 0)
                {
                    m_InvalidKeywordMaterialCount++;
                }
            }

            m_ShaderUsages.AddRange(shaderLookup.Values.OrderBy(usage => usage.m_Shader.name, StringComparer.OrdinalIgnoreCase));
            foreach (ShaderUsage shaderUsage in m_ShaderUsages)
            {
                shaderUsage.m_Variants.Sort((left, right) => string.Compare(left.m_Key, right.m_Key, StringComparison.Ordinal));
            }

            m_ScannedMaterialCount = materials.Count(material => material != null && material.shader != null);
            m_KeywordCombinationCount = m_ShaderUsages.Sum(usage => usage.m_Variants.Count);
            m_CollectionVariantCount = m_ShaderUsages.Sum(usage => usage.m_Variants.Count * usage.m_UniquePassTypes.Count);
        }

        private static ShaderUsage CreateShaderUsage(Shader shader)
        {
            ShaderUsage shaderUsage = new ShaderUsage
            {
                m_Shader = shader,
                m_ShaderPath = AssetDatabase.GetAssetPath(shader)
            };

            try
            {
                ShaderData shaderData = ShaderUtil.GetShaderData(shader);
                if (shaderData == null || shaderData.ActiveSubshaderIndex < 0)
                {
                    AddFallbackPass(shaderUsage, "Shader 没有当前平台可用的 SubShader，已回退到 Normal PassType。");
                    return shaderUsage;
                }

                ShaderData.Subshader subshader = shaderData.ActiveSubshader;
                for (int passIndex = 0; passIndex < subshader.PassCount; passIndex++)
                {
                    ShaderData.Pass pass = subshader.GetPass(passIndex);
                    string passName = string.IsNullOrEmpty(pass.Name) ? string.Format("Pass {0}", passIndex) : pass.Name;
                    string lightMode = pass.FindTagValue(LightModeTag).name;
                    PassType passType = pass.IsGrabPass ? PassType.GrabPass : MapPassType(lightMode);
                    shaderUsage.m_Passes.Add(new PassUsage
                    {
                        m_PassName = passName,
                        m_LightMode = lightMode,
                        m_PassType = passType
                    });

                    if (!shaderUsage.m_UniquePassTypes.Contains(passType))
                    {
                        shaderUsage.m_UniquePassTypes.Add(passType);
                    }
                }

                if (shaderUsage.m_UniquePassTypes.Count == 0)
                {
                    AddFallbackPass(shaderUsage, "Shader 的有效 SubShader 没有 Pass，已回退到 Normal PassType。");
                }
            }
            catch (Exception exception)
            {
                AddFallbackPass(shaderUsage, "读取 Shader Pass 失败，已回退到 Normal PassType：" + exception.Message);
            }

            shaderUsage.m_UniquePassTypes.Sort();
            return shaderUsage;
        }

        private static void AddFallbackPass(ShaderUsage shaderUsage, string warning)
        {
            shaderUsage.m_PassWarning = warning;
            shaderUsage.m_Passes.Add(new PassUsage
            {
                m_PassName = "Fallback",
                m_LightMode = string.Empty,
                m_PassType = PassType.Normal
            });
            shaderUsage.m_UniquePassTypes.Clear();
            shaderUsage.m_UniquePassTypes.Add(PassType.Normal);
        }

        // 保留对旧版 Built-in Shader LightMode 的识别，因此局部允许使用已废弃的 PassType。
#pragma warning disable CS0618
        private static PassType MapPassType(string lightMode)
        {
            if (string.IsNullOrEmpty(lightMode) || string.Equals(lightMode, "Always", StringComparison.OrdinalIgnoreCase))
            {
                return PassType.Normal;
            }

            switch (lightMode.ToLowerInvariant())
            {
                case "vertex":
                    return PassType.Vertex;
                case "vertexlm":
                    return PassType.VertexLM;
                case "vertexlmrgbm":
                    return PassType.VertexLMRGBM;
                case "forwardbase":
                    return PassType.ForwardBase;
                case "forwardadd":
                    return PassType.ForwardAdd;
                case "prepassbase":
                    return PassType.LightPrePassBase;
                case "prepassfinal":
                    return PassType.LightPrePassFinal;
                case "shadowcaster":
                    return PassType.ShadowCaster;
                case "deferred":
                    return PassType.Deferred;
                case "meta":
                    return PassType.Meta;
                case "motionvectors":
                    return PassType.MotionVectors;
                case "srpdefaultunlit":
                    return PassType.ScriptableRenderPipelineDefaultUnlit;
                default:
                    // URP/HDRP 的自定义 LightMode 在 SVC 中统一使用 ScriptableRenderPipeline。
                    return PassType.ScriptableRenderPipeline;
            }
        }
#pragma warning restore CS0618

        private static void GetMaterialKeywords(
            Material material,
            Shader shader,
            out string[] validKeywords,
            out string[] invalidKeywords)
        {
            HashSet<string> declaredKeywords = new HashSet<string>(
                shader.keywordSpace.keywords.Select(keyword => keyword.name),
                StringComparer.Ordinal);
            HashSet<string> valid = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> invalid = new HashSet<string>(StringComparer.Ordinal);

            foreach (string keyword in material.shaderKeywords)
            {
                if (string.IsNullOrEmpty(keyword))
                {
                    continue;
                }

                if (declaredKeywords.Contains(keyword))
                {
                    valid.Add(keyword);
                }
                else
                {
                    invalid.Add(keyword);
                }
            }

            validKeywords = valid.OrderBy(keyword => keyword, StringComparer.Ordinal).ToArray();
            invalidKeywords = invalid.OrderBy(keyword => keyword, StringComparer.Ordinal).ToArray();
        }

        private string[] GetDerivedKeywords(Shader shader)
        {
            Dictionary<string, LocalKeyword> declaredKeywords = shader.keywordSpace.keywords
                .ToDictionary(keyword => keyword.name, keyword => keyword, StringComparer.Ordinal);
            HashSet<string> derivedKeywords = new HashSet<string>(StringComparer.Ordinal);

            if (m_IncludeEnabledGlobalKeywords)
            {
                foreach (GlobalKeyword globalKeyword in Shader.enabledGlobalKeywords)
                {
                    if (declaredKeywords.TryGetValue(globalKeyword.name, out LocalKeyword localKeyword) &&
                        localKeyword.isOverridable)
                    {
                        derivedKeywords.Add(globalKeyword.name);
                    }
                }
            }

            if (m_IncludeUrpDerivedKeywords)
            {
                // SH 评估关键字互斥，使用当前 URP Asset 和目标平台重新确定最终状态。
                derivedKeywords.Remove("EVALUATE_SH_MIXED");
                derivedKeywords.Remove("EVALUATE_SH_VERTEX");
                string shEvaluationKeyword = GetUrpShEvaluationKeyword();
                if (!string.IsNullOrEmpty(shEvaluationKeyword) && declaredKeywords.ContainsKey(shEvaluationKeyword))
                {
                    derivedKeywords.Add(shEvaluationKeyword);
                }
            }

            return derivedKeywords.OrderBy(keyword => keyword, StringComparer.Ordinal).ToArray();
        }

        private static string GetUrpShEvaluationKeyword()
        {
            UniversalRenderPipelineAsset pipelineAsset =
                GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipelineAsset == null)
            {
                pipelineAsset = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            }

            if (pipelineAsset == null)
            {
                return string.Empty;
            }

            ShEvalMode evaluationMode = pipelineAsset.shEvalMode;
            if (evaluationMode == ShEvalMode.Auto)
            {
                bool isMobileShaderApi = GraphicsSettings.HasShaderDefine(BuiltinShaderDefine.SHADER_API_MOBILE);
                BuildTarget activeBuildTarget = EditorUserBuildSettings.activeBuildTarget;
                bool isMobileBuildTarget = activeBuildTarget == BuildTarget.Android ||
                                           activeBuildTarget == BuildTarget.iOS ||
                                           activeBuildTarget == BuildTarget.tvOS ||
                                           activeBuildTarget == BuildTarget.WebGL;
                bool isSwitch = activeBuildTarget == BuildTarget.Switch;
                evaluationMode = isMobileShaderApi || isMobileBuildTarget || isSwitch
                    ? ShEvalMode.PerVertex
                    : ShEvalMode.PerPixel;
            }

            switch (evaluationMode)
            {
                case ShEvalMode.PerVertex:
                    return "EVALUATE_SH_VERTEX";
                case ShEvalMode.Mixed:
                    return "EVALUATE_SH_MIXED";
                default:
                    return string.Empty;
            }
        }

        private void GenerateShaderVariantCollection()
        {
            if (m_ShaderUsages.Count == 0)
            {
                return;
            }

            string outputPath = EditorUtility.SaveFilePanelInProject(
                "保存 ShaderVariantCollection",
                "MaterialShaderVariants",
                "shadervariants",
                "请选择集合资源的保存位置");
            if (string.IsNullOrEmpty(outputPath))
            {
                return;
            }

            // 不覆盖已有资源，避免误删手工维护的变体集合。
            outputPath = AssetDatabase.GenerateUniqueAssetPath(outputPath);
            ShaderVariantCollection collection = new ShaderVariantCollection();
            int addedCount = 0;
            int failedCount = 0;

            try
            {
                int shaderIndex = 0;
                foreach (ShaderUsage shaderUsage in m_ShaderUsages)
                {
                    EditorUtility.DisplayProgressBar(
                        "生成 ShaderVariantCollection",
                        shaderUsage.m_Shader.name,
                        m_ShaderUsages.Count == 0 ? 1f : shaderIndex / (float)m_ShaderUsages.Count);
                    shaderIndex++;

                    foreach (VariantUsage variantUsage in shaderUsage.m_Variants)
                    {
                        foreach (PassType passType in shaderUsage.m_UniquePassTypes)
                        {
                            try
                            {
                                ShaderVariantCollection.ShaderVariant shaderVariant =
                                    new ShaderVariantCollection.ShaderVariant(
                                        shaderUsage.m_Shader,
                                        passType,
                                        variantUsage.m_Keywords);
                                if (collection.Add(shaderVariant))
                                {
                                    addedCount++;
                                }
                            }
                            catch (Exception exception)
                            {
                                failedCount++;
                                Debug.LogWarningFormat(
                                    "添加 Shader Variant 失败。Shader: {0}, PassType: {1}, Keywords: {2}\n{3}",
                                    shaderUsage.m_Shader.name,
                                    passType,
                                    variantUsage.m_Keywords.Length == 0 ? "<无>" : string.Join(" ", variantUsage.m_Keywords),
                                    exception.Message);
                            }
                        }
                    }
                }

                if (addedCount == 0)
                {
                    DestroyImmediate(collection);
                    EditorUtility.DisplayDialog("生成失败", "没有成功添加任何 Shader Variant，请查看 Console。", "确定");
                    return;
                }

                AssetDatabase.CreateAsset(collection, outputPath);
                EditorUtility.SetDirty(collection);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Selection.activeObject = collection;
                EditorGUIUtility.PingObject(collection);
                m_Status = string.Format(
                    "生成完成：{0}\n成功添加 {1} 个变体，失败 {2} 个。",
                    outputPath,
                    addedCount,
                    failedCount);
                EditorUtility.DisplayDialog("生成完成", m_Status, "确定");
            }
            catch (Exception exception)
            {
                if (AssetDatabase.GetAssetPath(collection) == string.Empty)
                {
                    DestroyImmediate(collection);
                }

                m_Status = "生成失败：" + exception.Message;
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("生成失败", exception.Message, "确定");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private bool MatchesSearch(ShaderUsage shaderUsage, VariantUsage variantUsage)
        {
            if (string.IsNullOrWhiteSpace(m_SearchText))
            {
                return true;
            }

            string search = m_SearchText.Trim();
            if (ContainsIgnoreCase(shaderUsage.m_Shader.name, search) ||
                ContainsIgnoreCase(shaderUsage.m_ShaderPath, search) ||
                variantUsage.m_Keywords.Any(keyword => ContainsIgnoreCase(keyword, search)))
            {
                return true;
            }

            return variantUsage.m_Materials.Any(materialUsage =>
                ContainsIgnoreCase(materialUsage.m_Material.name, search) ||
                ContainsIgnoreCase(materialUsage.m_AssetPath, search) ||
                materialUsage.m_InvalidKeywords.Any(keyword => ContainsIgnoreCase(keyword, search)));
        }

        private static bool ContainsIgnoreCase(string value, string search)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SetAllExpanded(bool expanded)
        {
            foreach (ShaderUsage shaderUsage in m_ShaderUsages)
            {
                shaderUsage.m_IsExpanded = expanded;
                foreach (VariantUsage variantUsage in shaderUsage.m_Variants)
                {
                    variantUsage.m_IsExpanded = expanded;
                }
            }
        }

        private void ClearResults()
        {
            m_ShaderUsages.Clear();
            m_ScannedMaterialCount = 0;
            m_KeywordCombinationCount = 0;
            m_CollectionVariantCount = 0;
            m_InvalidKeywordMaterialCount = 0;
        }
    }
}
