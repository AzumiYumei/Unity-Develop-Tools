using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Linq;

// ================================================================
// Ramp图生成器
// 
// 文件名称: RampGeneratorWindow.cs
// 版本: 1.4
// 
// 描述: 提供一个可视化的Editor窗口，用于生成具有不同渐变模式（常数/平滑）的Ramp Texture。
// 
// 修改历史:
// 2026-03-18 - 初始版本
// 2026-03-24 - UI及提示文字汉化
// 2026-03-24 - 移除面板渐变模式选项，直接使用渐变颜色变量的自带模式
// 2026-04-02 - 支持参考 Ramp 图平铺预览与平铺方向调整
// 2026-04-02 - 参考图下方增加生成渐变预览，且两张图尺寸强制一致
// ================================================================

namespace RampTexMaker
{
    public class RampGeneratorWindow : EditorWindow
    {
        public enum RampDirection
        {
            Horizontal_LeftToRight, // X轴 0 -> 1
            Horizontal_RightToLeft, // X轴 1 -> 0
            Vertical_BottomToTop,   // Y轴 0 -> 1
            Vertical_TopToBottom    // Y轴 1 -> 0
        }

        private RampDirection m_RampDirection = RampDirection.Horizontal_LeftToRight;
        private Vector2Int m_TextureSize = new Vector2Int(512, 1); // 默认 512x1
        [SerializeField]
        private Gradient m_Gradient = new Gradient();
        private string m_LastExportPath = "Assets";

        private enum ReferenceRampTilingDirection
        {
            Horizontal,
            Vertical
        }

        [SerializeField]
        private Texture2D m_ReferenceRampTexture;
        private string m_ReferenceRampTexturePath = string.Empty;
        private ReferenceRampTilingDirection m_ReferenceTilingDirection = ReferenceRampTilingDirection.Horizontal;
        private bool m_ReferenceFlip = false;
        private float m_ReferencePreviewHeight = 56f;
        private Texture2D m_GeneratedPreviewTexture;
        private int m_GeneratedPreviewHash;
        private Material m_TargetMaterial;
        private string m_TargetShaderPropertyName = "_RampTex";
        private bool m_AutoSyncBoundTextureResource = true;

        private const string PREF_KEY_PREFIX = "RampTexMaker_";

        [MenuItem("Tools/Azumi/纹理生成与修改/Ramp Tex 生成器")]
        public static void ShowWindow()
        {
            RampGeneratorWindow window = GetWindow<RampGeneratorWindow>("Ramp Tex 生成器");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }

        private void OnEnable()
        {
            LoadSettings();
        }

        private void OnDisable()
        {
            SaveSettings();
            CleanupGeneratedPreviewTexture();
        }

        private void LoadSettings()
        {
            m_RampDirection = (RampDirection)EditorPrefs.GetInt(PREF_KEY_PREFIX + "RampDirection", (int)RampDirection.Horizontal_LeftToRight);
            m_TextureSize.x = EditorPrefs.GetInt(PREF_KEY_PREFIX + "Width", 512);
            m_TextureSize.y = EditorPrefs.GetInt(PREF_KEY_PREFIX + "Height", 1);
            m_LastExportPath = EditorPrefs.GetString(PREF_KEY_PREFIX + "LastPath", "Assets");
            m_ReferenceRampTexturePath = EditorPrefs.GetString(PREF_KEY_PREFIX + "ReferenceRampPath", string.Empty);
            m_ReferenceTilingDirection = (ReferenceRampTilingDirection)EditorPrefs.GetInt(PREF_KEY_PREFIX + "ReferenceTilingDirection", (int)ReferenceRampTilingDirection.Horizontal);
            m_ReferenceFlip = EditorPrefs.GetBool(PREF_KEY_PREFIX + "ReferenceFlip", false);
            m_ReferencePreviewHeight = EditorPrefs.GetFloat(PREF_KEY_PREFIX + "ReferencePreviewHeight", 56f);
            string targetMaterialPath = EditorPrefs.GetString(PREF_KEY_PREFIX + "TargetMaterialPath", string.Empty);
            m_TargetShaderPropertyName = EditorPrefs.GetString(PREF_KEY_PREFIX + "TargetShaderPropertyName", "_RampTex");
            m_AutoSyncBoundTextureResource = EditorPrefs.GetBool(PREF_KEY_PREFIX + "AutoSyncBoundTextureResource", true);

            // Load Gradient
            string gradientJson = EditorPrefs.GetString(PREF_KEY_PREFIX + "Gradient", "");
            if (!string.IsNullOrEmpty(gradientJson))
            {
                // Gradient cannot be serialized directly to JSON easily in all Unity versions without wrapper
                // So we use a custom simple serialization for keys
                try 
                {
                    JsonUtility.FromJsonOverwrite(gradientJson, this);
                }
                catch {}
            }
            
            // If gradient is still "empty" or default (check if it has keys), initialize it
            if (m_Gradient == null || m_Gradient.colorKeys.Length == 0)
            {
                m_Gradient = new Gradient();
                m_Gradient.colorKeys = new GradientColorKey[]
                {
                    new GradientColorKey(Color.black, 0.0f),
                    new GradientColorKey(Color.white, 1.0f)
                };
                m_Gradient.alphaKeys = new GradientAlphaKey[]
                {
                    new GradientAlphaKey(1.0f, 0.0f),
                    new GradientAlphaKey(1.0f, 1.0f)
                };
            }

            if (!string.IsNullOrEmpty(m_ReferenceRampTexturePath))
            {
                m_ReferenceRampTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(m_ReferenceRampTexturePath);
                SyncTextureSizeToReferenceIfNeeded();
            }

            if (!string.IsNullOrEmpty(targetMaterialPath))
            {
                m_TargetMaterial = AssetDatabase.LoadAssetAtPath<Material>(targetMaterialPath);
            }
        }

        private void SaveSettings()
        {
            EditorPrefs.SetInt(PREF_KEY_PREFIX + "RampDirection", (int)m_RampDirection);
            EditorPrefs.SetInt(PREF_KEY_PREFIX + "Width", m_TextureSize.x);
            EditorPrefs.SetInt(PREF_KEY_PREFIX + "Height", m_TextureSize.y);
            EditorPrefs.SetString(PREF_KEY_PREFIX + "LastPath", m_LastExportPath);
            EditorPrefs.SetString(PREF_KEY_PREFIX + "ReferenceRampPath", m_ReferenceRampTexturePath);
            EditorPrefs.SetInt(PREF_KEY_PREFIX + "ReferenceTilingDirection", (int)m_ReferenceTilingDirection);
            EditorPrefs.SetBool(PREF_KEY_PREFIX + "ReferenceFlip", m_ReferenceFlip);
            EditorPrefs.SetFloat(PREF_KEY_PREFIX + "ReferencePreviewHeight", m_ReferencePreviewHeight);
            string targetMaterialPath = m_TargetMaterial != null ? AssetDatabase.GetAssetPath(m_TargetMaterial) : string.Empty;
            EditorPrefs.SetString(PREF_KEY_PREFIX + "TargetMaterialPath", targetMaterialPath);
            EditorPrefs.SetString(PREF_KEY_PREFIX + "TargetShaderPropertyName", m_TargetShaderPropertyName);
            EditorPrefs.SetBool(PREF_KEY_PREFIX + "AutoSyncBoundTextureResource", m_AutoSyncBoundTextureResource);
            
            // Save Gradient
            // We use the window instance itself to serialize the m_Gradient field
            string json = JsonUtility.ToJson(this);
            EditorPrefs.SetString(PREF_KEY_PREFIX + "Gradient", json);
        }

        private void OnGUI()
        {
            GUILayout.Label("渐变纹理设置", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            // 方向选择
            string[] dirOptions = { "水平 (左 -> 右)", "水平 (右 -> 左)", "垂直 (下 -> 上)", "垂直 (上 -> 下)" };
            m_RampDirection = (RampDirection)EditorGUILayout.Popup("渐变方向", (int)m_RampDirection, dirOptions);

            // 尺寸设置
            bool hasReferenceTexture = m_ReferenceRampTexture != null;
            using (new EditorGUI.DisabledScope(hasReferenceTexture))
            {
                m_TextureSize = EditorGUILayout.Vector2IntField(hasReferenceTexture ? "纹理尺寸(跟随参考图)" : "纹理尺寸", m_TextureSize);
                m_TextureSize.x = Mathf.Max(1, m_TextureSize.x);
                m_TextureSize.y = Mathf.Max(1, m_TextureSize.y);
            }
            if (hasReferenceTexture)
            {
                SyncTextureSizeToReferenceIfNeeded();
            }

            // 渐变编辑器
            EditorGUILayout.Space();
            GUILayout.Label("渐变编辑器", EditorStyles.boldLabel);
            m_Gradient = EditorGUILayout.GradientField("渐变颜色", m_Gradient);

            EditorGUILayout.Space();
            GUILayout.Label("参考 Ramp 图", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            m_ReferenceRampTexture = (Texture2D)EditorGUILayout.ObjectField("参考 Ramp Texture", m_ReferenceRampTexture, typeof(Texture2D), false);
            if (EditorGUI.EndChangeCheck())
            {
                m_ReferenceRampTexturePath = m_ReferenceRampTexture != null ? AssetDatabase.GetAssetPath(m_ReferenceRampTexture) : string.Empty;
                SyncTextureSizeToReferenceIfNeeded();
                SaveSettings();
            }

            m_ReferenceTilingDirection = (ReferenceRampTilingDirection)EditorGUILayout.EnumPopup("平铺朝向", m_ReferenceTilingDirection);
            m_ReferenceFlip = EditorGUILayout.ToggleLeft("反转方向", m_ReferenceFlip);
            m_ReferencePreviewHeight = EditorGUILayout.Slider("预览高度", m_ReferencePreviewHeight, 24f, 140f);

            DrawReferenceRampPreview();

            if (EditorGUI.EndChangeCheck())
            {
                SaveSettings();
                if (m_AutoSyncBoundTextureResource)
                {
                    TryOverwriteBoundTextureResource();
                }
            }

            EditorGUILayout.Space();
            GUILayout.Label("材质绑定", EditorStyles.boldLabel);
            m_TargetMaterial = (Material)EditorGUILayout.ObjectField("目标材质资源", m_TargetMaterial, typeof(Material), false);
            m_TargetShaderPropertyName = EditorGUILayout.TextField("材质属性名", m_TargetShaderPropertyName);
            m_TargetShaderPropertyName = string.IsNullOrWhiteSpace(m_TargetShaderPropertyName) ? "_RampTex" : m_TargetShaderPropertyName.Trim();
            m_AutoSyncBoundTextureResource = EditorGUILayout.Toggle("自动覆盖绑定纹理资源", m_AutoSyncBoundTextureResource);
            if (m_TargetMaterial != null && !m_TargetMaterial.HasProperty(m_TargetShaderPropertyName))
            {
                EditorGUILayout.HelpBox("当前目标材质不包含该属性名，导出后不会自动赋值。", MessageType.Warning);
            }

            EditorGUILayout.Space();

            GUILayout.FlexibleSpace();

            // 导出按钮
            if (GUILayout.Button("导出渐变纹理", GUILayout.Height(30)))
            {
                ExportTexture();
            }
        }

        private void DrawReferenceRampPreview()
        {
            if (m_ReferenceRampTexture == null)
            {
                EditorGUILayout.HelpBox("可选：指定任意 Ramp 图作为参考，工具会把它平铺显示在此处，方便对比颜色。", MessageType.Info);
                return;
            }

            SyncTextureSizeToReferenceIfNeeded();
            Texture2D generated = GetOrUpdateGeneratedPreviewTexture();
            if (generated == null)
            {
                EditorGUILayout.HelpBox("生成预览失败，请检查渐变设置。", MessageType.Warning);
                return;
            }

            if (generated.width != m_ReferenceRampTexture.width || generated.height != m_ReferenceRampTexture.height)
            {
                EditorGUILayout.HelpBox("参考 Ramp 图与生成预览的尺寸不一致。已强制同步纹理尺寸与参考图一致。", MessageType.Warning);
            }

            float height = Mathf.Clamp(m_ReferencePreviewHeight, 24f, 140f);

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("参考 Ramp（上） / 生成 Ramp（下）", EditorStyles.miniBoldLabel);

            Rect referenceRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(height));
            DrawRampPreview(referenceRect, m_ReferenceRampTexture);

            Rect generatedRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(height));
            DrawRampPreview(generatedRect, generated);

            EditorGUILayout.EndVertical();
        }

        private void DrawRampPreview(Rect rect, Texture2D texture)
        {
            if (texture == null || rect.width <= 1f || rect.height <= 1f)
            {
                return;
            }

            EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f, 1f));

            Rect uvRect = new Rect(0f, 0f, 1f, 1f);
            if (m_ReferenceFlip)
            {
                if (m_ReferenceTilingDirection == ReferenceRampTilingDirection.Horizontal)
                {
                    uvRect = new Rect(1f, 0f, -1f, 1f);
                }
                else
                {
                    uvRect = new Rect(0f, 1f, 1f, -1f);
                }
            }

            if (m_ReferenceTilingDirection == ReferenceRampTilingDirection.Horizontal)
            {
                GUI.DrawTextureWithTexCoords(rect, texture, uvRect, true);
                return;
            }

            Matrix4x4 oldMatrix = GUI.matrix;
            Vector2 pivot = rect.center;
            GUIUtility.RotateAroundPivot(90f, pivot);
            Rect rotatedRect = new Rect(pivot.x - rect.height * 0.5f, pivot.y - rect.width * 0.5f, rect.height, rect.width);
            GUI.DrawTextureWithTexCoords(rotatedRect, texture, uvRect, true);
            GUI.matrix = oldMatrix;
        }

        private void SyncTextureSizeToReferenceIfNeeded()
        {
            if (m_ReferenceRampTexture == null)
            {
                return;
            }

            int width = Mathf.Max(1, m_ReferenceRampTexture.width);
            int height = Mathf.Max(1, m_ReferenceRampTexture.height);
            if (m_TextureSize.x != width || m_TextureSize.y != height)
            {
                m_TextureSize = new Vector2Int(width, height);
            }
        }

        private Texture2D GetOrUpdateGeneratedPreviewTexture()
        {
            int currentHash = ComputeGeneratedPreviewHash();
            if (m_GeneratedPreviewTexture != null && m_GeneratedPreviewHash == currentHash)
            {
                return m_GeneratedPreviewTexture;
            }

            CleanupGeneratedPreviewTexture();

            int width = Mathf.Max(1, m_TextureSize.x);
            int height = Mathf.Max(1, m_TextureSize.y);

            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.hideFlags = HideFlags.HideAndDontSave;

            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float t = 0f;
                    switch (m_RampDirection)
                    {
                        case RampDirection.Horizontal_LeftToRight:
                            t = width > 1 ? (float)x / (width - 1) : 0f;
                            break;
                        case RampDirection.Horizontal_RightToLeft:
                            t = width > 1 ? 1f - ((float)x / (width - 1)) : 0f;
                            break;
                        case RampDirection.Vertical_BottomToTop:
                            t = height > 1 ? (float)y / (height - 1) : 0f;
                            break;
                        case RampDirection.Vertical_TopToBottom:
                            t = height > 1 ? 1f - ((float)y / (height - 1)) : 0f;
                            break;
                    }

                    pixels[y * width + x] = EvaluateGradient(t);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            m_GeneratedPreviewTexture = tex;
            m_GeneratedPreviewHash = currentHash;
            return m_GeneratedPreviewTexture;
        }

        private int ComputeGeneratedPreviewHash()
        {
            int hash = 17;
            hash = hash * 31 + m_RampDirection.GetHashCode();
            hash = hash * 31 + m_TextureSize.x.GetHashCode();
            hash = hash * 31 + m_TextureSize.y.GetHashCode();
            hash = hash * 31 + m_ReferenceFlip.GetHashCode();
            hash = hash * 31 + m_ReferenceTilingDirection.GetHashCode();

            if (m_Gradient != null)
            {
                hash = hash * 31 + m_Gradient.mode.GetHashCode();
                for (int i = 0; i < m_Gradient.colorKeys.Length; i++)
                {
                    GradientColorKey ck = m_Gradient.colorKeys[i];
                    hash = hash * 31 + ck.color.GetHashCode();
                    hash = hash * 31 + ck.time.GetHashCode();
                }
                for (int i = 0; i < m_Gradient.alphaKeys.Length; i++)
                {
                    GradientAlphaKey ak = m_Gradient.alphaKeys[i];
                    hash = hash * 31 + ak.alpha.GetHashCode();
                    hash = hash * 31 + ak.time.GetHashCode();
                }
            }

            return hash;
        }

        private void CleanupGeneratedPreviewTexture()
        {
            if (m_GeneratedPreviewTexture != null)
            {
                DestroyImmediate(m_GeneratedPreviewTexture);
                m_GeneratedPreviewTexture = null;
            }
            m_GeneratedPreviewHash = 0;
        }

        /// <summary>
        /// 根据当前模式计算渐变颜色
        /// </summary>
        private Color EvaluateGradient(float t)
        {
            // Gradient自带了Blend和Fixed模式，直接使用Evaluate即可
            return m_Gradient.Evaluate(t);
        }

        /// <summary>
        /// 导出贴图到项目
        /// </summary>
        private void ExportTexture()
        {
            string defaultName = "NewRampTexture.png";
            string folder = string.IsNullOrEmpty(m_LastExportPath) || !Directory.Exists(m_LastExportPath) ? "Assets" : m_LastExportPath;
            
            string path = EditorUtility.SaveFilePanel("保存渐变纹理", folder, defaultName, "png");

            if (string.IsNullOrEmpty(path))
                return;
            
            // 更新最后一次使用的路径
            m_LastExportPath = Path.GetDirectoryName(path);
            SaveSettings(); // 立即保存一次设置，防止Crash丢失

            // 重新生成一张用于导出的贴图，确保尺寸和内容最新
            int width = m_TextureSize.x;
            int height = m_TextureSize.y;
            Texture2D exportTex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            exportTex.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float t = 0f;
                    
                    switch (m_RampDirection)
                    {
                        case RampDirection.Horizontal_LeftToRight:
                            t = width > 1 ? (float)x / (width - 1) : 0f;
                            break;
                        case RampDirection.Horizontal_RightToLeft:
                            t = width > 1 ? 1f - ((float)x / (width - 1)) : 0f;
                            break;
                        case RampDirection.Vertical_BottomToTop:
                            t = height > 1 ? (float)y / (height - 1) : 0f;
                            break;
                        case RampDirection.Vertical_TopToBottom:
                            t = height > 1 ? 1f - ((float)y / (height - 1)) : 0f;
                            break;
                    }

                    pixels[y * width + x] = EvaluateGradient(t);
                }
            }

            exportTex.SetPixels(pixels);
            exportTex.Apply();

            byte[] pngData = exportTex.EncodeToPNG();
            DestroyImmediate(exportTex);

            if (pngData != null)
            {
                File.WriteAllBytes(path, pngData);
                AssetDatabase.Refresh();

                // 尝试将导出的贴图设置为Clamp和不生成Mipmap（适合Ramp图）
                string assetPath = path.Substring(path.IndexOf("Assets"));
                TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (importer != null)
                {
                    importer.wrapMode = TextureWrapMode.Clamp;
                    importer.mipmapEnabled = false;
                    importer.textureType = TextureImporterType.Default;
                    importer.sRGBTexture = true; // 颜色图通常需要sRGB
                    importer.SaveAndReimport();
                }

                Texture2D exportedTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (m_TargetMaterial != null && exportedTexture != null && !string.IsNullOrEmpty(m_TargetShaderPropertyName) && m_TargetMaterial.HasProperty(m_TargetShaderPropertyName))
                {
                    m_TargetMaterial.SetTexture(m_TargetShaderPropertyName, exportedTexture);
                    EditorUtility.SetDirty(m_TargetMaterial);
                    AssetDatabase.SaveAssets();
                }

                EditorUtility.DisplayDialog("成功", "渐变纹理导出成功！", "确定");
            }
        }

        private bool TryOverwriteBoundTextureResource()
        {
            if (m_TargetMaterial == null || string.IsNullOrEmpty(m_TargetShaderPropertyName))
            {
                return false;
            }

            if (!m_TargetMaterial.HasProperty(m_TargetShaderPropertyName))
            {
                return false;
            }

            Texture2D boundTexture = m_TargetMaterial.GetTexture(m_TargetShaderPropertyName) as Texture2D;
            if (boundTexture == null)
            {
                return false;
            }

            string assetPath = AssetDatabase.GetAssetPath(boundTexture);
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets"))
            {
                return false;
            }

            string fullPath = Path.GetFullPath(assetPath);
            if (!File.Exists(fullPath))
            {
                return false;
            }

            int width = m_TextureSize.x;
            int height = m_TextureSize.y;
            Texture2D exportTex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            exportTex.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float t = 0f;
                    switch (m_RampDirection)
                    {
                        case RampDirection.Horizontal_LeftToRight:
                            t = width > 1 ? (float)x / (width - 1) : 0f;
                            break;
                        case RampDirection.Horizontal_RightToLeft:
                            t = width > 1 ? 1f - ((float)x / (width - 1)) : 0f;
                            break;
                        case RampDirection.Vertical_BottomToTop:
                            t = height > 1 ? (float)y / (height - 1) : 0f;
                            break;
                        case RampDirection.Vertical_TopToBottom:
                            t = height > 1 ? 1f - ((float)y / (height - 1)) : 0f;
                            break;
                    }

                    pixels[y * width + x] = EvaluateGradient(t);
                }
            }

            exportTex.SetPixels(pixels);
            exportTex.Apply();
            byte[] pngData = exportTex.EncodeToPNG();
            DestroyImmediate(exportTex);

            if (pngData == null)
            {
                return false;
            }

            File.WriteAllBytes(fullPath, pngData);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return true;
        }
    }
}
