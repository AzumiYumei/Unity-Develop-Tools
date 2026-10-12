// ================================================================
// Cubemap 转经纬图 HDRI 工具
//
// 文件名称: ReflectionProbeTextureMapperWindow.cs
// 版本: 2.0
//
// 描述:
// 将 Unity 的 Cubemap 或 Reflection Probe 中的 Cubemap 贴图
// 转换为 2:1 比例的经纬图（LatLong / Equirectangular）EXR。
// 工具内部通过 Shader 对 Cubemap 做球面投影采样，
// 输出线性 HDR 贴图，便于继续用于天空盒、IBL、外部 DCC 或烘焙流程。
//
// 修改历史:
// 2026-07-15 - 2.0 完全重做为 Cubemap / Reflection Probe 转经纬图 EXR 工具
// ================================================================
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AzumisEditorTools.ReflectionProbeTextureMapper
{
    public class ReflectionProbeTextureMapperWindow : EditorWindow
    {
        private const string MenuPath = "Tools/Azumi/纹理生成与修改/Cubemap 转经纬图 HDRI";
        private const string PrefPrefix = "Azumi_CubemapToLatLong_";
        private const string ShaderName = "Hidden/Azumi/CubemapToLatLongHDRI";

        private static readonly string[] ResolutionLabels = { "512", "1024", "2048", "4096", "8192" };
        private static readonly int[] ResolutionValues = { 512, 1024, 2048, 4096, 8192 };

        private Cubemap m_SourceCubemap;
        private ReflectionProbe m_SourceProbe;
        private int m_OutputWidth = 2048;
        private float m_RotationDegrees;
        private bool m_FlipHorizontal;
        private bool m_FlipVertical;
        private string m_OutputFolder = "Assets";
        private string m_OutputName = "ReflectionProbe_LatLong";
        private string m_Status = string.Empty;
        private Material m_ConversionMaterial;

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            ReflectionProbeTextureMapperWindow window = GetWindow<ReflectionProbeTextureMapperWindow>("Cube -> LatLong");
            window.minSize = new Vector2(500f, 320f);
            window.Show();
        }

        private void OnEnable()
        {
            m_OutputWidth = EditorPrefs.GetInt(PrefPrefix + "OutputWidth", m_OutputWidth);
            m_RotationDegrees = EditorPrefs.GetFloat(PrefPrefix + "RotationDegrees", m_RotationDegrees);
            m_FlipHorizontal = EditorPrefs.GetBool(PrefPrefix + "FlipHorizontal", m_FlipHorizontal);
            m_FlipVertical = EditorPrefs.GetBool(PrefPrefix + "FlipVertical", m_FlipVertical);
            m_OutputFolder = EditorPrefs.GetString(PrefPrefix + "OutputFolder", m_OutputFolder);
            m_OutputName = EditorPrefs.GetString(PrefPrefix + "OutputName", m_OutputName);
        }

        private void OnDisable()
        {
            EditorPrefs.SetInt(PrefPrefix + "OutputWidth", m_OutputWidth);
            EditorPrefs.SetFloat(PrefPrefix + "RotationDegrees", m_RotationDegrees);
            EditorPrefs.SetBool(PrefPrefix + "FlipHorizontal", m_FlipHorizontal);
            EditorPrefs.SetBool(PrefPrefix + "FlipVertical", m_FlipVertical);
            EditorPrefs.SetString(PrefPrefix + "OutputFolder", m_OutputFolder);
            EditorPrefs.SetString(PrefPrefix + "OutputName", m_OutputName);

            if (m_ConversionMaterial != null)
            {
                DestroyImmediate(m_ConversionMaterial);
                m_ConversionMaterial = null;
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("输入", EditorStyles.boldLabel);
            m_SourceCubemap = (Cubemap)EditorGUILayout.ObjectField("Cubemap", m_SourceCubemap, typeof(Cubemap), false);
            m_SourceProbe = (ReflectionProbe)EditorGUILayout.ObjectField("Reflection Probe", m_SourceProbe, typeof(ReflectionProbe), true);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("使用当前选择", GUILayout.Height(22f)))
                {
                    UseCurrentSelection();
                }

                if (GUILayout.Button("清空输入", GUILayout.Height(22f)))
                {
                    m_SourceCubemap = null;
                    m_SourceProbe = null;
                }
            }

            EditorGUILayout.HelpBox(
                "优先使用直接指定的 Cubemap；如果未指定 Cubemap，则会尝试从 Reflection Probe 读取自定义或当前可用的 Cubemap 贴图。\n" +
                "输出固定为 2:1 比例 EXR，内部使用 ARGBHalf 线性 HDR 渲染，避免高光截断。",
                MessageType.Info);

            DrawResolvedSourceInfo();

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("投影参数", EditorStyles.boldLabel);
            m_OutputWidth = EditorGUILayout.IntPopup("输出宽度", m_OutputWidth, ResolutionLabels, ResolutionValues);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.IntField("输出高度", GetOutputHeight());
            }

            m_RotationDegrees = EditorGUILayout.FloatField("水平旋转角度", m_RotationDegrees);
            m_FlipHorizontal = EditorGUILayout.Toggle("水平翻转", m_FlipHorizontal);
            m_FlipVertical = EditorGUILayout.Toggle("垂直翻转", m_FlipVertical);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("输出", EditorStyles.boldLabel);
            m_OutputName = EditorGUILayout.TextField("资源名", m_OutputName);

            using (new EditorGUILayout.HorizontalScope())
            {
                m_OutputFolder = EditorGUILayout.TextField("输出目录", m_OutputFolder);
                if (GUILayout.Button("选择", GUILayout.Width(56f)))
                {
                    PickOutputFolder();
                }
            }

            EditorGUILayout.Space(12f);
            using (new EditorGUI.DisabledScope(!CanExport()))
            {
                if (GUILayout.Button("转换并导出 EXR", GUILayout.Height(34f)))
                {
                    ExportLatLongExr();
                }
            }

            if (!string.IsNullOrEmpty(m_Status))
            {
                EditorGUILayout.Space(8f);
                EditorGUILayout.HelpBox(m_Status, MessageType.None);
            }
        }

        /// <summary>
        /// 根据当前选择自动填充输入源。
        /// 选中 Cubemap 时优先写入 Cubemap；
        /// 选中带 Reflection Probe 组件的对象时自动写入 Reflection Probe。
        /// </summary>
        private void UseCurrentSelection()
        {
            if (Selection.activeObject is Cubemap cubemap)
            {
                m_SourceCubemap = cubemap;
                return;
            }

            if (Selection.activeGameObject == null)
            {
                return;
            }

            ReflectionProbe reflectionProbe = Selection.activeGameObject.GetComponent<ReflectionProbe>();
            if (reflectionProbe != null)
            {
                m_SourceProbe = reflectionProbe;
            }
        }

        private void DrawResolvedSourceInfo()
        {
            if (TryResolveSourceCubemap(out Cubemap sourceCubemap, out string sourceLabel, out string errorMessage))
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("当前来源", sourceLabel);
                EditorGUILayout.LabelField("Cubemap 尺寸", sourceCubemap.width + " x " + sourceCubemap.height);
                return;
            }

            if (!string.IsNullOrEmpty(errorMessage))
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.HelpBox(errorMessage, MessageType.Warning);
            }
        }

        private void PickOutputFolder()
        {
            string selected = EditorUtility.OpenFolderPanel("选择输出目录", Application.dataPath, string.Empty);
            if (string.IsNullOrEmpty(selected))
            {
                return;
            }

            string projectPath = Directory.GetParent(Application.dataPath)?.FullName?.Replace("\\", "/");
            string normalizedSelected = selected.Replace("\\", "/");
            if (!string.IsNullOrEmpty(projectPath) && normalizedSelected.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
            {
                m_OutputFolder = normalizedSelected.Substring(projectPath.Length + 1);
            }
            else
            {
                EditorUtility.DisplayDialog("路径无效", "请选择当前 Unity 项目内的 Assets 目录。", "确定");
            }
        }

        private bool CanExport()
        {
            return TryResolveSourceCubemap(out _, out _, out _) && AssetDatabase.IsValidFolder(m_OutputFolder);
        }

        private int GetOutputHeight()
        {
            return Mathf.Max(1, m_OutputWidth / 2);
        }

        private void ExportLatLongExr()
        {
            if (!ValidateExportInputs(out Cubemap sourceCubemap, out string sourceLabel, out string assetPath))
            {
                return;
            }

            RenderTexture temporaryRt = null;
            Texture2D outputTexture = null;
            try
            {
                Material conversionMaterial = GetOrCreateConversionMaterial();
                if (conversionMaterial == null)
                {
                    throw new InvalidOperationException("未找到 Cubemap 转经纬图 Shader。请确认 Shader 资源已存在且编译成功。");
                }

                conversionMaterial.SetTexture("_Cube", sourceCubemap);
                conversionMaterial.SetFloat("_RotationDegrees", m_RotationDegrees);
                conversionMaterial.SetFloat("_FlipHorizontal", m_FlipHorizontal ? 1f : 0f);
                conversionMaterial.SetFloat("_FlipVertical", m_FlipVertical ? 1f : 0f);

                temporaryRt = RenderTexture.GetTemporary(
                    m_OutputWidth,
                    GetOutputHeight(),
                    0,
                    RenderTextureFormat.ARGBHalf,
                    RenderTextureReadWrite.Linear);
                temporaryRt.wrapMode = TextureWrapMode.Clamp;
                temporaryRt.filterMode = FilterMode.Bilinear;

                Graphics.Blit(null, temporaryRt, conversionMaterial);
                outputTexture = ReadRenderTextureToHdrTexture(temporaryRt, assetPath);

                byte[] exrBytes = outputTexture.EncodeToEXR(Texture2D.EXRFlags.CompressZIP);
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                if (string.IsNullOrEmpty(projectRoot))
                {
                    throw new InvalidOperationException("无法定位 Unity 项目根目录。");
                }

                string absolutePath = Path.Combine(projectRoot, assetPath).Replace("\\", "/");
                File.WriteAllBytes(absolutePath, exrBytes);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                ConfigureImportedExr(assetPath);

                m_Status = string.Format(
                    "导出完成：{0}\n来源：{1}\n输出尺寸：{2} x {3}",
                    assetPath,
                    sourceLabel,
                    m_OutputWidth,
                    GetOutputHeight());

                Texture2D exportedTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                Selection.activeObject = exportedTexture;
                EditorUtility.DisplayDialog("导出成功", "经纬图 EXR 已保存到：\n" + assetPath, "确定");
            }
            catch (Exception ex)
            {
                m_Status = "导出失败：" + ex.Message;
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("导出失败", ex.Message, "确定");
            }
            finally
            {
                EditorUtility.ClearProgressBar();

                if (temporaryRt != null)
                {
                    RenderTexture.ReleaseTemporary(temporaryRt);
                }

                if (outputTexture != null)
                {
                    DestroyImmediate(outputTexture);
                }
            }
        }

        private bool ValidateExportInputs(out Cubemap sourceCubemap, out string sourceLabel, out string assetPath)
        {
            sourceCubemap = null;
            sourceLabel = string.Empty;
            assetPath = string.Empty;

            if (!TryResolveSourceCubemap(out sourceCubemap, out sourceLabel, out string errorMessage))
            {
                EditorUtility.DisplayDialog("缺少输入", errorMessage, "确定");
                return false;
            }

            if (!AssetDatabase.IsValidFolder(m_OutputFolder))
            {
                EditorUtility.DisplayDialog("输出目录无效", "请选择 Assets 下的有效输出目录。", "确定");
                return false;
            }

            string safeName = SanitizeAssetName(string.IsNullOrWhiteSpace(m_OutputName) ? sourceCubemap.name + "_LatLong" : m_OutputName);
            assetPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(m_OutputFolder, safeName + ".exr").Replace("\\", "/"));
            return true;
        }

        /// <summary>
        /// 解析当前真正用于导出的 Cubemap。
        /// 工具优先使用直接指定的 Cubemap，其次尝试从 Reflection Probe 的自定义贴图或当前贴图中读取。
        /// </summary>
        private bool TryResolveSourceCubemap(out Cubemap sourceCubemap, out string sourceLabel, out string errorMessage)
        {
            sourceCubemap = null;
            sourceLabel = string.Empty;
            errorMessage = string.Empty;

            if (m_SourceCubemap != null)
            {
                sourceCubemap = m_SourceCubemap;
                sourceLabel = "直接指定 Cubemap: " + m_SourceCubemap.name;
                return true;
            }

            if (m_SourceProbe == null)
            {
                errorMessage = "请先指定 Cubemap，或者指定一个可读取出 Cubemap 贴图的 Reflection Probe。";
                return false;
            }

            Texture probeTexture = m_SourceProbe.customBakedTexture;
            if (probeTexture == null)
            {
                probeTexture = m_SourceProbe.texture;
            }

            sourceCubemap = probeTexture as Cubemap;
            if (sourceCubemap == null)
            {
                errorMessage = probeTexture == null
                    ? "当前 Reflection Probe 没有可用贴图。请先给它指定 Custom Baked Texture，或确保它已经生成可读取的 Cubemap。"
                    : "当前 Reflection Probe 的贴图类型不是 Cubemap，无法直接导出为经纬图。";
                return false;
            }

            sourceLabel = "Reflection Probe: " + m_SourceProbe.name;
            return true;
        }

        private Material GetOrCreateConversionMaterial()
        {
            if (m_ConversionMaterial != null)
            {
                return m_ConversionMaterial;
            }

            Shader shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                return null;
            }

            m_ConversionMaterial = new Material(shader);
            m_ConversionMaterial.hideFlags = HideFlags.HideAndDontSave;
            return m_ConversionMaterial;
        }

        /// <summary>
        /// 将 HDR RenderTexture 读回为线性半浮点 Texture2D。
        /// 这里使用 RGBAHalf，保证 EXR 导出前仍保留 HDR 动态范围。
        /// </summary>
        private Texture2D ReadRenderTextureToHdrTexture(RenderTexture sourceRt, string assetPath)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D outputTexture = new Texture2D(sourceRt.width, sourceRt.height, TextureFormat.RGBAHalf, false, true);

            try
            {
                EditorUtility.DisplayProgressBar("导出经纬图 EXR", "读取并写入 HDR 贴图数据", 0.85f);
                RenderTexture.active = sourceRt;
                outputTexture.ReadPixels(new Rect(0f, 0f, sourceRt.width, sourceRt.height), 0, 0);
                outputTexture.Apply(false, false);
                outputTexture.name = Path.GetFileNameWithoutExtension(assetPath);
            }
            finally
            {
                RenderTexture.active = previous;
            }

            return outputTexture;
        }

        /// <summary>
        /// 统一设置导入后的 EXR 为线性 2D 纹理，避免被当成 sRGB 贴图处理。
        /// </summary>
        private void ConfigureImportedExr(string assetPath)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                return;
            }

            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.SaveAndReimport();
        }

        private static string SanitizeAssetName(string assetName)
        {
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                assetName = assetName.Replace(invalidChar, '_');
            }

            return assetName.Trim();
        }
    }
}
