using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace EPT.Editor
{
    /// <summary>
    /// 将 Cubemap 转换为 2:1 的圆柱经纬展开图。
    /// </summary>
    public sealed class CubemapToCylindricalWindow : EditorWindow
    {
        private sealed class SourceImporterBackup
        {
            public string m_AssetPath = string.Empty;
            public string m_ActivePlatformName = string.Empty;
            public TextureImporterSettings m_TextureSettings;
            public TextureImporterPlatformSettings m_DefaultPlatformSettings;
            public TextureImporterPlatformSettings m_ActivePlatformSettings;
            public TextureImporterCompression m_TextureCompression;
            public bool m_CrunchedCompression;
        }

        private const string ConverterShaderName = "Hidden/EPT/Editor/CubemapToCylindrical";
        private const string DefaultTexturePlatformName = "DefaultTexturePlatform";

        private enum OutputFormat
        {
            [InspectorName("EXR（HDR）")]
            Exr,
            [InspectorName("PNG（LDR）")]
            Png
        }

        [SerializeField] private Cubemap m_SourceCubemap;
        [SerializeField] private ReflectionProbe m_SourceProbe;
        [SerializeField] private int m_OutputWidth = 2048;
        [SerializeField] private int m_OutputHeight = 1024;
        [SerializeField] private float m_HorizontalRotation;
        [SerializeField] private float m_MipLevel;
        [SerializeField] private bool m_ApplyHdrDecode = true;
        [SerializeField] private bool m_UseFullPrecisionHdr = true;
        [SerializeField] private bool m_TemporarilyReimportSource = true;
        [SerializeField] private bool m_UseFullSourceResolution = true;
        [SerializeField] private OutputFormat m_OutputFormat = OutputFormat.Exr;

        [MenuItem("Tools/纹理工具/Cubemap 转圆柱展开图", false, 120)]
        private static void OpenWindow()
        {
            CubemapToCylindricalWindow window = GetWindow<CubemapToCylindricalWindow>();
            window.titleContent = new GUIContent("Cubemap 转换");
            window.minSize = new Vector2(390f, 330f);

            Cubemap selectedCubemap = Selection.activeObject as Cubemap;
            if (selectedCubemap != null)
            {
                window.m_SourceCubemap = selectedCubemap;
                window.SetSizeFromSource();
            }
            else if (Selection.activeGameObject != null)
            {
                ReflectionProbe selectedProbe = Selection.activeGameObject.GetComponent<ReflectionProbe>();
                if (selectedProbe != null)
                {
                    window.m_SourceProbe = selectedProbe;
                    window.TryAssignProbeCubemap(false);
                }
            }

            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Cubemap 转圆柱经纬展开图", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "输出为标准 2:1 经纬展开图：图片中心朝向 Cubemap 的 +Z，右侧朝向 +X，顶部朝向 +Y。",
                MessageType.Info);

            EditorGUILayout.Space(6f);
            EditorGUI.BeginChangeCheck();
            m_SourceProbe = (ReflectionProbe)EditorGUILayout.ObjectField(
                new GUIContent("反射探针", "可选。指定后会读取探针已经烘焙的 Cubemap。"),
                m_SourceProbe,
                typeof(ReflectionProbe),
                true);
            if (EditorGUI.EndChangeCheck() && m_SourceProbe != null)
            {
                TryAssignProbeCubemap(true);
            }

            m_SourceCubemap = (Cubemap)EditorGUILayout.ObjectField(
                new GUIContent("源 Cubemap", "需要转换的 Cubemap 资源。"),
                m_SourceCubemap,
                typeof(Cubemap),
                false);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("输出设置", EditorStyles.boldLabel);
            m_OutputWidth = Mathf.Clamp(EditorGUILayout.IntField("宽度", m_OutputWidth), 1, 16384);
            m_OutputHeight = Mathf.Clamp(EditorGUILayout.IntField("高度", m_OutputHeight), 1, 16384);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("设为标准 2:1", GUILayout.Width(110f)))
                {
                    m_OutputHeight = Mathf.Max(1, m_OutputWidth / 2);
                }

                if (GUILayout.Button("按源尺寸设置", GUILayout.Width(110f)))
                {
                    SetSizeFromSource();
                }
            }

            m_HorizontalRotation = EditorGUILayout.Slider(
                new GUIContent("水平旋转", "绕 Y 轴旋转展开图，单位为度。"),
                m_HorizontalRotation,
                -180f,
                180f);

            using (new EditorGUI.DisabledScope(m_SourceCubemap == null || m_SourceCubemap.mipmapCount <= 1))
            {
                float maxMipLevel = m_SourceCubemap == null ? 0f : Mathf.Max(0, m_SourceCubemap.mipmapCount - 1);
                m_MipLevel = EditorGUILayout.Slider(
                    new GUIContent("Mip 层级", "0 为原始清晰度，更高层级可得到更模糊的环境图。"),
                    Mathf.Clamp(m_MipLevel, 0f, maxMipLevel),
                    0f,
                    maxMipLevel);
            }

            m_OutputFormat = (OutputFormat)EditorGUILayout.EnumPopup(
                new GUIContent("输出格式", "EXR 保留 HDR；PNG 为普通 LDR 图片。"),
                m_OutputFormat);
            m_ApplyHdrDecode = EditorGUILayout.Toggle(
                new GUIContent("应用反射探针 HDR 解码", "读取反射探针的 HDR Decode Values，并在采样后执行 Unity DecodeHDR。烘焙反射探针通常应保持开启。"),
                m_ApplyHdrDecode);

            using (new EditorGUI.DisabledScope(m_OutputFormat != OutputFormat.Exr))
            {
                m_UseFullPrecisionHdr = EditorGUILayout.Toggle(
                    new GUIContent("32-bit Float 无压缩 EXR", "使用 ARGBFloat、RGBAFloat 和 EXR OutputAsFloat，全流程不使用 Half 精度和 EXR 压缩。"),
                    m_UseFullPrecisionHdr);
                m_TemporarilyReimportSource = EditorGUILayout.Toggle(
                    new GUIContent("临时无压缩重导入源图", "转换前把源 Cubemap 临时重导入为全尺寸 RGBAFloat/Linear，转换完成后恢复原 Importer 设置。"),
                    m_TemporarilyReimportSource);
                m_UseFullSourceResolution = EditorGUILayout.Toggle(
                    new GUIContent("按源完整分辨率输出", "重导入源图后，按实际 Cubemap 面宽自动输出为 4N × 2N，避免继续使用平台缩小后的尺寸。"),
                    m_UseFullSourceResolution);
            }

            if (m_OutputFormat == OutputFormat.Exr && m_UseFullPrecisionHdr)
            {
                EditorGUILayout.HelpBox("全精度未压缩 EXR 文件会非常大；4K × 2K RGBAFloat 通常约为 128 MB。", MessageType.Info);
            }

            if (m_OutputFormat == OutputFormat.Png)
            {
                EditorGUILayout.HelpBox("PNG 会把 HDR 高亮截断到 0~1；需要保留反射探针高动态范围时请使用 EXR。", MessageType.Warning);
            }

            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(m_SourceCubemap == null))
            {
                if (GUILayout.Button("转换并保存", GUILayout.Height(34f)))
                {
                    ConvertAndSave();
                }
            }

            EditorGUILayout.Space(8f);
        }

        private void TryAssignProbeCubemap(bool showWarning)
        {
            Texture probeTexture = m_SourceProbe == null ? null : m_SourceProbe.bakedTexture;
            if (probeTexture == null && m_SourceProbe != null)
            {
                probeTexture = m_SourceProbe.customBakedTexture;
            }

            if (probeTexture == null && m_SourceProbe != null)
            {
                probeTexture = m_SourceProbe.texture;
            }

            Cubemap bakedCubemap = probeTexture as Cubemap;
            if (bakedCubemap != null)
            {
                m_SourceCubemap = bakedCubemap;
                SetSizeFromSource();
                return;
            }

            if (showWarning)
            {
                Debug.LogWarning("所选反射探针没有可用的烘焙 Cubemap，请先烘焙探针，或直接指定源 Cubemap 资源。");
            }
        }

        private void SetSizeFromSource()
        {
            if (m_SourceCubemap == null)
            {
                return;
            }

            // 经纬展开图的常用尺寸为单个 Cubemap 面宽的 4 倍和 2 倍。
            m_OutputWidth = Mathf.Clamp(m_SourceCubemap.width * 4, 1, 16384);
            m_OutputHeight = Mathf.Clamp(m_SourceCubemap.width * 2, 1, 16384);
        }

        private void ConvertAndSave()
        {
            if (m_SourceCubemap == null)
            {
                EditorUtility.DisplayDialog("转换失败", "请先指定源 Cubemap。", "确定");
                return;
            }

            Shader converterShader = Shader.Find(ConverterShaderName);
            if (converterShader == null)
            {
                EditorUtility.DisplayDialog("转换失败", "找不到 Cubemap 转换 Shader，请检查资源是否已经正确导入。", "确定");
                return;
            }

            string extension = m_OutputFormat == OutputFormat.Exr ? "exr" : "png";
            string defaultName = string.Concat(m_SourceCubemap.name, "_Cylindrical");
            string outputPath = EditorUtility.SaveFilePanelInProject(
                "保存圆柱展开图",
                defaultName,
                extension,
                "请选择输出位置");

            if (string.IsNullOrEmpty(outputPath))
            {
                return;
            }

            Material converterMaterial = null;
            RenderTexture renderTexture = null;
            Texture2D outputTexture = null;
            RenderTexture previousActive = RenderTexture.active;
            SourceImporterBackup sourceImporterBackup = null;

            try
            {
                bool exportHdr = m_OutputFormat == OutputFormat.Exr;
                bool useFullPrecision = exportHdr && m_UseFullPrecisionHdr;
                Cubemap conversionCubemap = m_SourceCubemap;
                if (useFullPrecision && m_TemporarilyReimportSource)
                {
                    sourceImporterBackup = ApplyFullPrecisionSourceImport(m_SourceCubemap);
                    if (sourceImporterBackup != null)
                    {
                        conversionCubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(sourceImporterBackup.m_AssetPath);
                        if (conversionCubemap == null)
                        {
                            throw new InvalidOperationException("源 Cubemap 以全精度重新导入后加载失败。");
                        }

                        if (conversionCubemap.format != TextureFormat.RGBAFloat)
                        {
                            throw new InvalidOperationException(
                                "源 Cubemap 未能以 RGBAFloat 导入，当前实际格式为 " + conversionCubemap.format +
                                "。为避免静默产生精度损失，已停止转换。");
                        }
                    }
                }

                int renderWidth = m_OutputWidth;
                int renderHeight = m_OutputHeight;
                if (useFullPrecision && m_UseFullSourceResolution)
                {
                    // 必须使用重导入后的实际面尺寸，不能继续沿用 Android 等平台缩小后的预览尺寸。
                    renderWidth = Mathf.Clamp(conversionCubemap.width * 4, 1, 16384);
                    renderHeight = Mathf.Clamp(conversionCubemap.width * 2, 1, 16384);
                }

                converterMaterial = new Material(converterShader)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                converterMaterial.SetTexture("_Cubemap", conversionCubemap);
                converterMaterial.SetFloat("_Rotation", m_HorizontalRotation * Mathf.Deg2Rad);
                converterMaterial.SetFloat("_MipLevel", Mathf.Clamp(m_MipLevel, 0f, conversionCubemap.mipmapCount - 1));
                Vector4 hdrDecodeValues = m_ApplyHdrDecode
                    ? ResolveHdrDecodeValues(conversionCubemap)
                    : new Vector4(1f, 1f, 0f, 0f);
                converterMaterial.SetVector("_Cubemap_HDR", hdrDecodeValues);

                RenderTextureFormat renderFormat = useFullPrecision
                    ? RenderTextureFormat.ARGBFloat
                    : exportHdr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
                if (useFullPrecision && !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
                {
                    throw new NotSupportedException("当前显卡不支持 ARGBFloat RenderTexture，无法执行 32-bit Float 转换。");
                }

                RenderTextureReadWrite readWrite = exportHdr ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.Default;
                renderTexture = new RenderTexture(renderWidth, renderHeight, 0, renderFormat, readWrite)
                {
                    name = "CubemapToCylindrical_Temp",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                renderTexture.Create();

                Graphics.Blit(null, renderTexture, converterMaterial);

                TextureFormat textureFormat = useFullPrecision
                    ? TextureFormat.RGBAFloat
                    : exportHdr ? TextureFormat.RGBAHalf : TextureFormat.RGBA32;
                outputTexture = new Texture2D(renderWidth, renderHeight, textureFormat, false, exportHdr)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };

                RenderTexture.active = renderTexture;
                outputTexture.ReadPixels(new Rect(0f, 0f, renderWidth, renderHeight), 0, 0, false);
                outputTexture.Apply(false, false);

                byte[] fileBytes;
                if (useFullPrecision)
                {
                    // 只指定 OutputAsFloat，不启用 ZIP/PIZ/RLE，输出未压缩的 32-bit Float EXR。
                    fileBytes = outputTexture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat);
                }
                else
                {
                    fileBytes = exportHdr
                        ? outputTexture.EncodeToEXR(Texture2D.EXRFlags.CompressZIP)
                        : outputTexture.EncodeToPNG();
                }

                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                if (string.IsNullOrEmpty(projectRoot))
                {
                    throw new InvalidOperationException("无法定位 Unity 项目根目录。");
                }

                string absoluteOutputPath = Path.GetFullPath(Path.Combine(projectRoot, outputPath));
                File.WriteAllBytes(absoluteOutputPath, fileBytes);
                AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceUpdate);
                ConfigureOutputImporter(outputPath, exportHdr, useFullPrecision, renderWidth, renderHeight);

                UnityEngine.Object outputAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(outputPath);
                Texture2D outputTextureAsset = outputAsset as Texture2D;
                if (useFullPrecision && outputTextureAsset != null && outputTextureAsset.format != TextureFormat.RGBAFloat)
                {
                    Debug.LogWarningFormat(
                        "EXR 文件本身是未压缩 32-bit Float，但当前平台把 Unity 内的纹理导入为 {0}；请检查平台纹理格式支持。",
                        outputTextureAsset.format);
                }

                Selection.activeObject = outputAsset;
                EditorGUIUtility.PingObject(outputAsset);
                Debug.LogFormat(
                    "Cubemap 圆柱展开图已保存：{0}，源面尺寸：{1}，输出尺寸：{2} × {3}，HDR Decode Values：{4}",
                    outputPath,
                    conversionCubemap.width,
                    renderWidth,
                    renderHeight,
                    hdrDecodeValues);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("转换失败", exception.Message, "确定");
            }
            finally
            {
                RenderTexture.active = previousActive;

                if (renderTexture != null)
                {
                    renderTexture.Release();
                    DestroyImmediate(renderTexture);
                }

                if (outputTexture != null)
                {
                    DestroyImmediate(outputTexture);
                }

                if (converterMaterial != null)
                {
                    DestroyImmediate(converterMaterial);
                }

                if (sourceImporterBackup != null)
                {
                    try
                    {
                        RestoreSourceImporter(sourceImporterBackup);
                        m_SourceCubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(sourceImporterBackup.m_AssetPath);
                    }
                    catch (Exception restoreException)
                    {
                        Debug.LogError("恢复源 Cubemap Importer 失败，请检查该纹理的 Import Settings。");
                        Debug.LogException(restoreException);
                    }
                }
            }
        }

        private static SourceImporterBackup ApplyFullPrecisionSourceImport(Cubemap sourceCubemap)
        {
            string assetPath = AssetDatabase.GetAssetPath(sourceCubemap);
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (string.IsNullOrEmpty(assetPath) || importer == null)
            {
                Debug.LogWarning("源 Cubemap 不是可重新导入的纹理资产，将使用当前 GPU 中的纹理格式进行转换。");
                return null;
            }

            string activePlatformName = BuildPipeline.GetBuildTargetName(EditorUserBuildSettings.activeBuildTarget);
            SourceImporterBackup backup = new SourceImporterBackup
            {
                m_AssetPath = assetPath,
                m_ActivePlatformName = activePlatformName,
                m_TextureSettings = new TextureImporterSettings(),
                m_DefaultPlatformSettings = importer.GetPlatformTextureSettings(DefaultTexturePlatformName),
                m_ActivePlatformSettings = string.IsNullOrEmpty(activePlatformName)
                    ? null
                    : importer.GetPlatformTextureSettings(activePlatformName),
                m_TextureCompression = importer.textureCompression,
                m_CrunchedCompression = importer.crunchedCompression
            };
            importer.ReadTextureSettings(backup.m_TextureSettings);

            TextureImporterSettings fullPrecisionSettings = new TextureImporterSettings();
            importer.ReadTextureSettings(fullPrecisionSettings);
            fullPrecisionSettings.sRGBTexture = false;
#pragma warning disable CS0618
            fullPrecisionSettings.textureFormat = TextureImporterFormat.RGBAFloat;
#pragma warning restore CS0618
            try
            {
                importer.SetTextureSettings(fullPrecisionSettings);
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.crunchedCompression = false;

                TextureImporterPlatformSettings defaultPlatformSettings =
                    importer.GetPlatformTextureSettings(DefaultTexturePlatformName);
                ConfigureFullPrecisionPlatform(defaultPlatformSettings, false);
                importer.SetPlatformTextureSettings(defaultPlatformSettings);

                if (!string.IsNullOrEmpty(activePlatformName))
                {
                    TextureImporterPlatformSettings activePlatformSettings =
                        importer.GetPlatformTextureSettings(activePlatformName);
                    ConfigureFullPrecisionPlatform(activePlatformSettings, true);
                    importer.SetPlatformTextureSettings(activePlatformSettings);
                }

                importer.SaveAndReimport();
                return backup;
            }
            catch
            {
                RestoreSourceImporter(backup);
                throw;
            }
        }

        private static void ConfigureFullPrecisionPlatform(
            TextureImporterPlatformSettings platformSettings,
            bool overridePlatform)
        {
            platformSettings.overridden = overridePlatform;
            platformSettings.maxTextureSize = 16384;
            platformSettings.format = TextureImporterFormat.RGBAFloat;
            platformSettings.textureCompression = TextureImporterCompression.Uncompressed;
            platformSettings.crunchedCompression = false;
        }

        private static void RestoreSourceImporter(SourceImporterBackup backup)
        {
            TextureImporter importer = AssetImporter.GetAtPath(backup.m_AssetPath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogError("无法恢复源 Cubemap Importer：" + backup.m_AssetPath);
                return;
            }

            importer.SetTextureSettings(backup.m_TextureSettings);
            importer.textureCompression = backup.m_TextureCompression;
            importer.crunchedCompression = backup.m_CrunchedCompression;
            importer.SetPlatformTextureSettings(backup.m_DefaultPlatformSettings);
            if (!string.IsNullOrEmpty(backup.m_ActivePlatformName) && backup.m_ActivePlatformSettings != null)
            {
                importer.SetPlatformTextureSettings(backup.m_ActivePlatformSettings);
            }

            importer.SaveAndReimport();
        }

        private Vector4 ResolveHdrDecodeValues(Cubemap sourceCubemap)
        {
            if (m_SourceProbe != null && IsProbeUsingCubemap(m_SourceProbe, sourceCubemap))
            {
                return ValidateHdrDecodeValues(m_SourceProbe.textureHDRDecodeValues);
            }

            // 直接指定 Cubemap 时，临时构造 Custom Probe，让 Unity 返回该纹理对应的解码参数。
            GameObject temporaryObject = new GameObject("CubemapHdrDecodeProbe")
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            try
            {
                ReflectionProbe temporaryProbe = temporaryObject.AddComponent<ReflectionProbe>();
                temporaryProbe.hideFlags = HideFlags.HideAndDontSave;
                temporaryProbe.mode = ReflectionProbeMode.Custom;
                temporaryProbe.hdr = true;
                temporaryProbe.customBakedTexture = sourceCubemap;
                return ValidateHdrDecodeValues(temporaryProbe.textureHDRDecodeValues);
            }
            finally
            {
                DestroyImmediate(temporaryObject);
            }
        }

        private static bool IsProbeUsingCubemap(ReflectionProbe probe, Cubemap cubemap)
        {
            return probe.bakedTexture == cubemap ||
                   probe.customBakedTexture == cubemap ||
                   probe.texture == cubemap;
        }

        private static Vector4 ValidateHdrDecodeValues(Vector4 decodeValues)
        {
            if (decodeValues.x > 0f && decodeValues.y > 0f)
            {
                return decodeValues;
            }

            Vector4 defaultDecodeValues = ReflectionProbe.defaultTextureHDRDecodeValues;
            if (defaultDecodeValues.x > 0f && defaultDecodeValues.y > 0f)
            {
                return defaultDecodeValues;
            }

            // 单位解码参数不会修改已经采用原生浮点格式的 HDR Cubemap。
            return new Vector4(1f, 1f, 0f, 0f);
        }

        private static void ConfigureOutputImporter(
            string outputPath,
            bool isHdr,
            bool useFullPrecision,
            int width,
            int height)
        {
            TextureImporter importer = AssetImporter.GetAtPath(outputPath) as TextureImporter;
            if (importer == null)
            {
                return;
            }

            importer.textureType = TextureImporterType.Default;
#pragma warning disable CS0618
            importer.generateCubemap = TextureImporterGenerateCubemap.None;
#pragma warning restore CS0618
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.wrapModeU = TextureWrapMode.Repeat;
            importer.wrapModeV = TextureWrapMode.Clamp;
            importer.mipmapEnabled = false;
            importer.sRGBTexture = !isHdr;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;

            if (useFullPrecision)
            {
                TextureImporterSettings textureSettings = new TextureImporterSettings();
                importer.ReadTextureSettings(textureSettings);
#pragma warning disable CS0618
                textureSettings.textureFormat = TextureImporterFormat.RGBAFloat;
#pragma warning restore CS0618
                importer.SetTextureSettings(textureSettings);

                string activePlatformName = BuildPipeline.GetBuildTargetName(EditorUserBuildSettings.activeBuildTarget);
                if (!string.IsNullOrEmpty(activePlatformName))
                {
                    TextureImporterPlatformSettings platformSettings =
                        importer.GetPlatformTextureSettings(activePlatformName);
                    ConfigureFullPrecisionPlatform(platformSettings, true);
                    importer.SetPlatformTextureSettings(platformSettings);
                }
            }

            // 避免导出大图后，Unity 再次导入时被默认的最大尺寸缩小。
            int requiredSize = Mathf.NextPowerOfTwo(Mathf.Max(width, height));
            importer.maxTextureSize = Mathf.Clamp(requiredSize, 32, 16384);
            importer.SaveAndReimport();
        }
    }
}
