using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// ================================================================
// 反射探针横排图转经纬 EXR 工具
//
// 文件名称: ReflectionProbeStripToLatLongExrWindow.cs
// 版本: 1.0
//
// 描述: 将 Unity 反射探针导出的 6 面横排图（宽 = 6x，高 = x）转换为
//       2:1 比例的经纬展开 EXR 图（宽 = 2x，高 = x）。
//       默认按 Unity 常用 Cubemap 面顺序读取：+X、-X、+Y、-Y、+Z、-Z。
//
// 修改历史:
// 2026-07-09 - 初始版本
// ================================================================

namespace AzumisEditorTools.ReflectionProbeLatLongExporter
{
    public class ReflectionProbeStripToLatLongExrWindow : EditorWindow
    {
        private const string MenuPath = "Tools/Azumi/纹理生成与修改/反射探针横排图转经纬 EXR";
        private const string PrefPrefix = "Azumi_ReflectionProbeStripToLatLong_";

        private Texture2D m_SourceStripTexture;
        private string m_OutputFolder = "Assets";
        private string m_OutputName = "ReflectionProbe_LatLong";
        private int m_StartFaceBlock = 0;
        private string m_Status = string.Empty;

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            ReflectionProbeStripToLatLongExrWindow window = GetWindow<ReflectionProbeStripToLatLongExrWindow>("Probe Strip -> EXR");
            window.minSize = new Vector2(460f, 260f);
            window.Show();
        }

        private void OnEnable()
        {
            m_OutputFolder = EditorPrefs.GetString(PrefPrefix + "OutputFolder", m_OutputFolder);
            m_OutputName = EditorPrefs.GetString(PrefPrefix + "OutputName", m_OutputName);
        }

        private void OnDisable()
        {
            EditorPrefs.SetString(PrefPrefix + "OutputFolder", m_OutputFolder);
            EditorPrefs.SetString(PrefPrefix + "OutputName", m_OutputName);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("输入", EditorStyles.boldLabel);
            m_SourceStripTexture = (Texture2D)EditorGUILayout.ObjectField("6 面横排图", m_SourceStripTexture, typeof(Texture2D), false);

            EditorGUILayout.HelpBox(
                "输入贴图需要是横向排列的 Cubemap 面图。\n" +
                "支持 宽 = 6x、高 = x，也支持类似 8x : x 这种横向包含额外空白块/填充块的情况。\n" +
                "工具会从横向连续 6 格中读取 Cubemap 面，按经纬展开采样，输出为 宽 = 2x、高 = x 的线性 HDR EXR。\n" +
                "默认面顺序：+X、-X、+Y、-Y、+Z、-Z。",
                MessageType.Info);

            if (m_SourceStripTexture != null && m_SourceStripTexture.height > 0 && m_SourceStripTexture.width % m_SourceStripTexture.height == 0)
            {
                int blockCount = m_SourceStripTexture.width / m_SourceStripTexture.height;
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("横向块数", blockCount.ToString());
                if (blockCount >= 6)
                {
                    m_StartFaceBlock = EditorGUILayout.IntSlider("6 面起始块索引", m_StartFaceBlock, 0, blockCount - 6);
                }
            }

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

            if (m_SourceStripTexture != null)
            {
                int faceSize = m_SourceStripTexture.height;
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("预计输出尺寸", string.Format("{0} x {1}", faceSize * 2, faceSize));
            }

            EditorGUILayout.Space(12f);
            using (new EditorGUI.DisabledScope(m_SourceStripTexture == null))
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

        private void ExportLatLongExr()
        {
            if (!ValidateInputs(out int faceSize, out int blockCount, out string assetPath))
            {
                return;
            }

            Texture2D readableTexture = null;
            Texture2D outputTexture = null;
            try
            {
                readableTexture = CopyTextureToReadableHdr(m_SourceStripTexture);
                outputTexture = ConvertStripToLatLong(readableTexture, faceSize, blockCount, m_StartFaceBlock);

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

                m_Status = "导出完成：" + assetPath;
                EditorUtility.DisplayDialog("导出成功", "EXR 已保存到：\n" + assetPath, "确定");
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
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

                if (readableTexture != null)
                {
                    DestroyImmediate(readableTexture);
                }

                if (outputTexture != null)
                {
                    DestroyImmediate(outputTexture);
                }
            }
        }

        private bool ValidateInputs(out int faceSize, out int blockCount, out string assetPath)
        {
            faceSize = 0;
            blockCount = 0;
            assetPath = string.Empty;

            if (m_SourceStripTexture == null)
            {
                EditorUtility.DisplayDialog("缺少输入", "请先指定反射探针 6 面横排图。", "确定");
                return false;
            }

            if (!AssetDatabase.IsValidFolder(m_OutputFolder))
            {
                EditorUtility.DisplayDialog("输出目录无效", "请选择 Assets 下的有效输出目录。", "确定");
                return false;
            }

            faceSize = m_SourceStripTexture.height;
            if (faceSize <= 0 || m_SourceStripTexture.width % faceSize != 0)
            {
                EditorUtility.DisplayDialog("尺寸不合法", "输入贴图必须满足：宽 = Nx，高 = x。", "确定");
                return false;
            }

            blockCount = m_SourceStripTexture.width / faceSize;
            if (blockCount < 6)
            {
                EditorUtility.DisplayDialog("横向块数不足", "输入贴图横向至少需要包含连续 6 个方形块。", "确定");
                return false;
            }

            if (m_StartFaceBlock < 0 || m_StartFaceBlock > blockCount - 6)
            {
                EditorUtility.DisplayDialog("起始块索引无效", "请把 6 面起始块索引调整到有效范围内。", "确定");
                return false;
            }

            string safeName = SanitizeAssetName(string.IsNullOrWhiteSpace(m_OutputName) ? m_SourceStripTexture.name + "_LatLong" : m_OutputName);
            assetPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(m_OutputFolder, safeName + ".exr").Replace("\\", "/"));
            return true;
        }

        /// <summary>
        /// 将输入纹理复制为可读的 HDR 纹理。
        /// 这里统一使用 RGBAHalf + Linear，避免在 EXR/HDR 贴图上丢失高亮信息。
        /// </summary>
        private Texture2D CopyTextureToReadableHdr(Texture2D source)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            Texture2D readable = new Texture2D(source.width, source.height, TextureFormat.RGBAHalf, false, true);

            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                readable.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
                readable.Apply(false, false);
                readable.name = source.name + "_ReadableHDR";
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
            }

            return readable;
        }

        /// <summary>
        /// 将横排的 Cubemap 贴图转成经纬展开图。
        /// 输出比例固定为 2:1，对应常见全景/HDRI 贴图格式。
        /// </summary>
        private Texture2D ConvertStripToLatLong(Texture2D sourceStrip, int faceSize, int blockCount, int startFaceBlock)
        {
            int outputWidth = faceSize * 2;
            int outputHeight = faceSize;
            Texture2D output = new Texture2D(outputWidth, outputHeight, TextureFormat.RGBAHalf, false, true);
            Color[] sourcePixels = sourceStrip.GetPixels();
            Color[] outputPixels = new Color[outputWidth * outputHeight];

            for (int y = 0; y < outputHeight; y++)
            {
                float progress = outputHeight > 1 ? y / (float)(outputHeight - 1) : 1f;
                if ((y & 15) == 0 && EditorUtility.DisplayCancelableProgressBar("转换反射探针 EXR", "正在生成经纬展开贴图...", progress))
                {
                    throw new OperationCanceledException("用户取消了 EXR 导出。");
                }

                float v = (y + 0.5f) / outputHeight;
                float latitude = (v - 0.5f) * Mathf.PI;
                float cosLatitude = Mathf.Cos(latitude);
                float sinLatitude = Mathf.Sin(latitude);

                for (int x = 0; x < outputWidth; x++)
                {
                    float u = (x + 0.5f) / outputWidth;
                    float longitude = (u - 0.5f) * Mathf.PI * 2f;

                    Vector3 direction = new Vector3(
                        Mathf.Sin(longitude) * cosLatitude,
                        sinLatitude,
                        Mathf.Cos(longitude) * cosLatitude);

                    Color color = SampleStripByDirection(sourcePixels, sourceStrip.width, sourceStrip.height, faceSize, blockCount, startFaceBlock, direction);
                    outputPixels[x + y * outputWidth] = color;
                }
            }

            output.SetPixels(outputPixels);
            output.Apply(false, false);
            output.name = m_OutputName;
            return output;
        }

        /// <summary>
        /// 先把方向映射到 Cubemap 面和面内 UV，再从横排图中做双线性采样。
        /// </summary>
        private Color SampleStripByDirection(Color[] sourcePixels, int textureWidth, int textureHeight, int faceSize, int blockCount, int startFaceBlock, Vector3 direction)
        {
            if (!DirectionToFaceUv(direction, out CubemapFace face, out float u, out float v))
            {
                return Color.black;
            }

            return SampleFaceBilinear(sourcePixels, textureWidth, textureHeight, faceSize, blockCount, startFaceBlock, face, u, v);
        }

        private Color SampleFaceBilinear(Color[] sourcePixels, int textureWidth, int textureHeight, int faceSize, int blockCount, int startFaceBlock, CubemapFace face, float u, float v)
        {
            int faceIndex = GetFaceIndex(face);
            int stripFaceIndex = Mathf.Clamp(startFaceBlock + faceIndex, 0, blockCount - 1);
            int startX = stripFaceIndex * faceSize;

            float pixelX = Mathf.Clamp01(u) * (faceSize - 1);
            float pixelY = Mathf.Clamp01(v) * (faceSize - 1);

            int x0 = Mathf.FloorToInt(pixelX);
            int y0 = Mathf.FloorToInt(pixelY);
            int x1 = Mathf.Min(x0 + 1, faceSize - 1);
            int y1 = Mathf.Min(y0 + 1, faceSize - 1);

            float tx = pixelX - x0;
            float ty = pixelY - y0;

            Color c00 = GetPixel(sourcePixels, textureWidth, textureHeight, startX + x0, y0);
            Color c10 = GetPixel(sourcePixels, textureWidth, textureHeight, startX + x1, y0);
            Color c01 = GetPixel(sourcePixels, textureWidth, textureHeight, startX + x0, y1);
            Color c11 = GetPixel(sourcePixels, textureWidth, textureHeight, startX + x1, y1);

            Color cx0 = Color.Lerp(c00, c10, tx);
            Color cx1 = Color.Lerp(c01, c11, tx);
            return Color.Lerp(cx0, cx1, ty);
        }

        private Color GetPixel(Color[] pixels, int textureWidth, int textureHeight, int x, int y)
        {
            int clampedX = Mathf.Clamp(x, 0, textureWidth - 1);
            int clampedY = Mathf.Clamp(y, 0, textureHeight - 1);
            return pixels[clampedX + clampedY * textureWidth];
        }

        /// <summary>
        /// 方向转 Cubemap 面内 UV，映射规则与项目里其他 Reflection Probe 工具保持一致。
        /// </summary>
        private static bool DirectionToFaceUv(Vector3 direction, out CubemapFace face, out float u, out float v)
        {
            face = CubemapFace.PositiveX;
            u = 0.5f;
            v = 0.5f;

            if (direction.sqrMagnitude <= 0.000001f)
            {
                return false;
            }

            direction.Normalize();
            float absX = Mathf.Abs(direction.x);
            float absY = Mathf.Abs(direction.y);
            float absZ = Mathf.Abs(direction.z);
            float uc;
            float vc;
            float majorAxis;

            if (absX >= absY && absX >= absZ)
            {
                majorAxis = absX;
                if (direction.x >= 0f)
                {
                    face = CubemapFace.PositiveX;
                    uc = -direction.z;
                    vc = -direction.y;
                }
                else
                {
                    face = CubemapFace.NegativeX;
                    uc = direction.z;
                    vc = -direction.y;
                }
            }
            else if (absY >= absX && absY >= absZ)
            {
                majorAxis = absY;
                if (direction.y >= 0f)
                {
                    face = CubemapFace.PositiveY;
                    uc = direction.x;
                    vc = direction.z;
                }
                else
                {
                    face = CubemapFace.NegativeY;
                    uc = direction.x;
                    vc = -direction.z;
                }
            }
            else
            {
                majorAxis = absZ;
                if (direction.z >= 0f)
                {
                    face = CubemapFace.PositiveZ;
                    uc = direction.x;
                    vc = -direction.y;
                }
                else
                {
                    face = CubemapFace.NegativeZ;
                    uc = -direction.x;
                    vc = -direction.y;
                }
            }

            if (majorAxis <= 0.000001f)
            {
                return false;
            }

            u = 0.5f * (uc / majorAxis + 1f);
            v = 0.5f * (vc / majorAxis + 1f);
            return true;
        }

        private static int GetFaceIndex(CubemapFace face)
        {
            switch (face)
            {
                case CubemapFace.PositiveX:
                    return 0;
                case CubemapFace.NegativeX:
                    return 1;
                case CubemapFace.PositiveY:
                    return 2;
                case CubemapFace.NegativeY:
                    return 3;
                case CubemapFace.PositiveZ:
                    return 4;
                case CubemapFace.NegativeZ:
                    return 5;
                default:
                    return 0;
            }
        }

        private void ConfigureImportedExr(string assetPath)
        {
            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                return;
            }

            importer.textureType = TextureImporterType.Default;
            importer.textureShape = TextureImporterShape.Texture2D;
            importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.mipmapEnabled = false;
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
