// ================================================================
// 材质烘焙成纹理工具
// 
// 文件名称: MaterialToTextureBakerWindow.cs
// 版本: 1.0
// 
// 描述:
// 提供一个可视化的 Editor 窗口，用于把指定 Material 的渲染结果“离线烘焙”为一张纹理资源（PNG）。
// 工具内部使用 RenderTexture + Graphics.Blit 将材质在一个全屏 Quad 上执行一次像素着色，然后读取像素并导出。
// 
// 使用方式:
// 1. 打开菜单 Tools/Azumi/纹理生成与修改/材质烘焙成纹理。
// 2. 指定一个目标材质（Project 中的 .mat 资源）。
// 3. 设置纹理尺寸、PassIndex（通常为 0）、以及导入设置（sRGB/MipMap）。
// 4. 点击“生成预览”检查结果；确认无误后点击“导出 PNG 纹理”保存为项目资源。
// 
// 注意事项:
// 1. 本工具会以 UV(0~1) 的方式烘焙材质；若 Shader 依赖 Mesh 法线/切线/世界坐标等信息，结果可能与场景中不同。
// 2. PassIndex 对应 Shader Pass 的索引；多数常规 Shader 使用 0 即可。若不确定，先从 0 开始尝试。
// 3. 导出后会自动设置导入器（TextureImporter）的 sRGB 与 MipMap 参数；其它导入设置请按项目需求自行调整。
// 
// 修改历史:
// 2026-07-06 - 初始版本
// ================================================================

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AzumisEditorTools
{
    public class MaterialToTextureBakerWindow : EditorWindow
    {
        private const string m_WindowTitle = "材质烘焙成纹理";

        [SerializeField]
        private Material m_TargetMaterial;

        private Vector2Int m_TextureSize = new Vector2Int(1024, 1024);
        private int m_PassIndex;
        private bool m_IsSRGB = true;
        private bool m_IsGenerateMipMap;
        private bool m_IsAlphaIsTransparency;

        private Vector2 m_ScrollPosition;
        private Texture2D m_PreviewTexture;
        private string m_LastExportFolder = "Assets";

        [MenuItem("Tools/Azumi/纹理生成与修改/材质烘焙成纹理")]
        public static void ShowWindow()
        {
            MaterialToTextureBakerWindow window = GetWindow<MaterialToTextureBakerWindow>(m_WindowTitle);
            window.minSize = new Vector2(520f, 540f);
            window.Show();
        }

        private void OnDisable()
        {
            CleanupPreviewTexture();
        }

        private void OnGUI()
        {
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);

            EditorGUILayout.LabelField("目标材质", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            m_TargetMaterial = (Material)EditorGUILayout.ObjectField("材质资源", m_TargetMaterial, typeof(Material), false);
            EditorGUILayout.HelpBox(
                "将材质在一个 0~1 UV 的全屏 Quad 上执行一次 Blit，并把输出读回到 Texture2D。",
                MessageType.Info);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("烘焙参数", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            m_TextureSize = EditorGUILayout.Vector2IntField("纹理尺寸", m_TextureSize);
            m_TextureSize.x = Mathf.Max(1, m_TextureSize.x);
            m_TextureSize.y = Mathf.Max(1, m_TextureSize.y);

            m_PassIndex = EditorGUILayout.IntField("PassIndex", m_PassIndex);
            m_PassIndex = Mathf.Max(0, m_PassIndex);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("导入设置（导出后自动应用）", EditorStyles.miniBoldLabel);
            m_IsSRGB = EditorGUILayout.Toggle("sRGB（颜色纹理）", m_IsSRGB);
            m_IsGenerateMipMap = EditorGUILayout.Toggle("生成 MipMap", m_IsGenerateMipMap);
            m_IsAlphaIsTransparency = EditorGUILayout.Toggle("Alpha Is Transparency", m_IsAlphaIsTransparency);

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("操作", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            using (new EditorGUI.DisabledScope(m_TargetMaterial == null))
            {
                if (GUILayout.Button("生成预览", GUILayout.Height(28f)))
                {
                    GeneratePreview();
                }

                if (GUILayout.Button("导出 PNG 纹理", GUILayout.Height(28f)))
                {
                    ExportPngTexture();
                }
            }

            if (m_TargetMaterial == null)
            {
                EditorGUILayout.HelpBox("请先指定一个目标材质。", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(8f);
            DrawPreviewArea();

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// 生成并缓存一张仅用于窗口预览的 Texture2D。
        /// </summary>
        private void GeneratePreview()
        {
            CleanupPreviewTexture();

            if (!TryBakeToTexture2D(out Texture2D bakedTexture))
            {
                return;
            }

            bakedTexture.hideFlags = HideFlags.HideAndDontSave;
            m_PreviewTexture = bakedTexture;
            Repaint();
        }

        /// <summary>
        /// 将烘焙结果导出为 PNG，并刷新为项目内可引用的 Texture2D 资源。
        /// </summary>
        private void ExportPngTexture()
        {
            if (!TryBakeToTexture2D(out Texture2D bakedTexture))
            {
                return;
            }

            try
            {
                string defaultFileName = (m_TargetMaterial != null ? m_TargetMaterial.name : "MaterialBake") + ".png";
                string folder = string.IsNullOrEmpty(m_LastExportFolder) || !Directory.Exists(m_LastExportFolder) ? "Assets" : m_LastExportFolder;
                string savePath = EditorUtility.SaveFilePanel("保存 PNG 纹理", folder, defaultFileName, "png");
                if (string.IsNullOrEmpty(savePath))
                {
                    return;
                }

                m_LastExportFolder = Path.GetDirectoryName(savePath);

                byte[] pngBytes = bakedTexture.EncodeToPNG();
                if (pngBytes == null || pngBytes.Length == 0)
                {
                    EditorUtility.DisplayDialog("失败", "PNG 编码失败，请检查烘焙结果。", "确定");
                    return;
                }

                File.WriteAllBytes(savePath, pngBytes);
                AssetDatabase.Refresh();

                string assetPath = ConvertFullPathToAssetPath(savePath);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    ApplyImporterSettings(assetPath);
                }

                EditorUtility.DisplayDialog("完成", "导出成功：\n" + savePath, "确定");
            }
            finally
            {
                DestroyImmediate(bakedTexture);
            }
        }

        /// <summary>
        /// 实际烘焙：把 Material 的输出写入 RenderTexture，再 ReadPixels 到 Texture2D。
        /// </summary>
        private bool TryBakeToTexture2D(out Texture2D bakedTexture)
        {
            bakedTexture = null;

            if (m_TargetMaterial == null)
            {
                EditorUtility.DisplayDialog("提示", "请先指定目标材质。", "确定");
                return false;
            }

            int width = Mathf.Max(1, m_TextureSize.x);
            int height = Mathf.Max(1, m_TextureSize.y);

            RenderTexture tempRt = null;
            RenderTexture previousActive = RenderTexture.active;

            try
            {
                RenderTextureReadWrite readWrite = m_IsSRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
                tempRt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, readWrite);
                tempRt.filterMode = FilterMode.Bilinear;
                tempRt.wrapMode = TextureWrapMode.Clamp;

                Graphics.Blit(null, tempRt, m_TargetMaterial, m_PassIndex);

                bakedTexture = new Texture2D(width, height, TextureFormat.RGBA32, m_IsGenerateMipMap, !m_IsSRGB);
                bakedTexture.wrapMode = TextureWrapMode.Clamp;

                RenderTexture.active = tempRt;
                bakedTexture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                bakedTexture.Apply(m_IsGenerateMipMap, false);

                return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("失败", "烘焙失败，详情请查看 Console。", "确定");
                if (bakedTexture != null)
                {
                    DestroyImmediate(bakedTexture);
                    bakedTexture = null;
                }
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (tempRt != null)
                {
                    RenderTexture.ReleaseTemporary(tempRt);
                }
            }
        }

        /// <summary>
        /// 在窗口中绘制预览贴图。
        /// </summary>
        private void DrawPreviewArea()
        {
            EditorGUILayout.LabelField("预览", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (m_PreviewTexture == null)
            {
                EditorGUILayout.HelpBox("点击“生成预览”后在此处显示烘焙结果。", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            float maxPreviewSize = Mathf.Min(position.width - 40f, 420f);
            float aspect = m_PreviewTexture.height > 0 ? (float)m_PreviewTexture.width / m_PreviewTexture.height : 1f;
            float previewWidth = maxPreviewSize;
            float previewHeight = maxPreviewSize;
            if (aspect >= 1f)
            {
                previewHeight = maxPreviewSize / aspect;
            }
            else
            {
                previewWidth = maxPreviewSize * aspect;
            }

            Rect previewRect = GUILayoutUtility.GetRect(previewWidth, previewHeight, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(previewRect, new Color(0.12f, 0.12f, 0.12f, 1f));
            GUI.DrawTexture(previewRect, m_PreviewTexture, ScaleMode.ScaleToFit, true);

            EditorGUILayout.LabelField("分辨率", m_PreviewTexture.width + " x " + m_PreviewTexture.height);
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// 将导出的文件绝对路径转换为 Unity 可识别的 Assets 路径。
        /// 如果导出路径不在工程 Assets 下，则返回空字符串（此时无法自动设置导入器）。
        /// </summary>
        private static string ConvertFullPathToAssetPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return string.Empty;
            }

            string normalized = fullPath.Replace('\\', '/');
            int index = normalized.LastIndexOf("/Assets/", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                if (normalized.EndsWith("/Assets", StringComparison.OrdinalIgnoreCase))
                {
                    return "Assets";
                }

                return string.Empty;
            }

            return normalized.Substring(index + 1);
        }

        /// <summary>
        /// 对导出的纹理资源应用推荐的导入设置，确保与本工具选项一致。
        /// </summary>
        private void ApplyImporterSettings(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                return;
            }

            importer.textureType = TextureImporterType.Default;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = m_IsGenerateMipMap;
            importer.sRGBTexture = m_IsSRGB;
            importer.alphaIsTransparency = m_IsAlphaIsTransparency;
            importer.SaveAndReimport();
        }

        /// <summary>
        /// 清理窗口预览用的临时贴图，避免内存泄漏。
        /// </summary>
        private void CleanupPreviewTexture()
        {
            if (m_PreviewTexture != null)
            {
                DestroyImmediate(m_PreviewTexture);
                m_PreviewTexture = null;
            }
        }
    }
}

