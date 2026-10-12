using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;

// ================================================================
// 纹理通道打包器 (Texture Channel Packer)
// 
// 文件名称: TextureChannelPackerWindow.cs
// 版本: 1.5
// 
// 描述: 提供一个Editor窗口，使用Compute Shader提取纹理通道或将多张纹理的不同通道混合打包成一张新纹理。
//       自动处理不同尺寸纹理的插值（以最大尺寸为准）。
// 
// 修改历史:
// 2026-03-20 - 初始版本
// 2026-03-24 - UI及提示文字汉化
// 2026-07-07 - 修复导出颜色偏暗问题，并根据源纹理色彩空间自动做导出转换
// 2026-07-07 - 调整 Alpha 通道提取逻辑，按原始 Alpha 数值导出
// 2026-07-20 - 新增 1-R/1-G/1-B/1-A 反相通道选择
// ================================================================

namespace TextureChannelPacker
{
    public class TextureChannelPackerWindow : EditorWindow
    {
        public enum Channel
        {
            R, G, B, A, Black, White, OneMinusR, OneMinusG, OneMinusB, OneMinusA
        }

        public enum PackerMode
        {
            ExtractChannel, // 提取单通道
            PackChannels    // 打包多通道
        }

        private PackerMode m_CurrentMode = PackerMode.PackChannels;

        // 提取模式变量
        private Texture2D m_ExtractSource;
        private Channel m_ExtractChannel = Channel.R;

        // 打包模式变量
        private Texture2D m_TexR, m_TexG, m_TexB, m_TexA;
        private Channel m_SourceChanR = Channel.R;
        private Channel m_SourceChanG = Channel.R;
        private Channel m_SourceChanB = Channel.R;
        private Channel m_SourceChanA = Channel.R;

        private ComputeShader m_ComputeShader;
        private string m_LastExportPath = "Assets";
        private const string PREF_KEY_PREFIX = "TexChannelPacker_";
        private static readonly Channel[] s_ExtractChannelOptions =
        {
            Channel.R,
            Channel.G,
            Channel.B,
            Channel.A,
            Channel.OneMinusR,
            Channel.OneMinusG,
            Channel.OneMinusB,
            Channel.OneMinusA
        };
        private static readonly Channel[] s_PackChannelOptions =
        {
            Channel.R,
            Channel.G,
            Channel.B,
            Channel.A,
            Channel.OneMinusR,
            Channel.OneMinusG,
            Channel.OneMinusB,
            Channel.OneMinusA,
            Channel.Black,
            Channel.White
        };

        [MenuItem("Tools/Azumi/纹理生成与修改/纹理通道分离与合并")]
        public static void ShowWindow()
        {
            var window = GetWindow<TextureChannelPackerWindow>("纹理通道分离与合并");
            window.minSize = new Vector2(400, 500);
            window.Show();
        }

        private void OnEnable()
        {
            LoadSettings();
            LoadComputeShader();
        }

        private void OnDisable()
        {
            SaveSettings();
        }

        private void LoadComputeShader()
        {
            // 尝试在插件目录下寻找Compute Shader
            string[] guids = AssetDatabase.FindAssets("TextureChannelPackerCS t:ComputeShader");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                m_ComputeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            }
        }

        private void LoadSettings()
        {
            m_CurrentMode = (PackerMode)EditorPrefs.GetInt(PREF_KEY_PREFIX + "Mode", (int)PackerMode.PackChannels);
            m_LastExportPath = EditorPrefs.GetString(PREF_KEY_PREFIX + "LastPath", "Assets");

            m_ExtractChannel = (Channel)EditorPrefs.GetInt(PREF_KEY_PREFIX + "ExtChan", (int)Channel.R);

            m_SourceChanR = (Channel)EditorPrefs.GetInt(PREF_KEY_PREFIX + "SrcR", (int)Channel.R);
            m_SourceChanG = (Channel)EditorPrefs.GetInt(PREF_KEY_PREFIX + "SrcG", (int)Channel.R);
            m_SourceChanB = (Channel)EditorPrefs.GetInt(PREF_KEY_PREFIX + "SrcB", (int)Channel.R);
            m_SourceChanA = (Channel)EditorPrefs.GetInt(PREF_KEY_PREFIX + "SrcA", (int)Channel.R);
        }

        private void SaveSettings()
        {
            EditorPrefs.SetInt(PREF_KEY_PREFIX + "Mode", (int)m_CurrentMode);
            EditorPrefs.SetString(PREF_KEY_PREFIX + "LastPath", m_LastExportPath);

            EditorPrefs.SetInt(PREF_KEY_PREFIX + "ExtChan", (int)m_ExtractChannel);

            EditorPrefs.SetInt(PREF_KEY_PREFIX + "SrcR", (int)m_SourceChanR);
            EditorPrefs.SetInt(PREF_KEY_PREFIX + "SrcG", (int)m_SourceChanG);
            EditorPrefs.SetInt(PREF_KEY_PREFIX + "SrcB", (int)m_SourceChanB);
            EditorPrefs.SetInt(PREF_KEY_PREFIX + "SrcA", (int)m_SourceChanA);
        }

        private void OnGUI()
        {
            if (m_ComputeShader == null)
            {
                EditorGUILayout.HelpBox("未找到Compute Shader！请确保项目中存在 'TextureChannelPackerCS.compute'。", MessageType.Error);
                if (GUILayout.Button("重试加载 Compute Shader"))
                {
                    LoadComputeShader();
                }
                return;
            }

            GUILayout.Label("纹理通道打包器", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            string[] modeOptions = { "提取单通道", "打包多通道" };
            m_CurrentMode = (PackerMode)EditorGUILayout.Popup("模式", (int)m_CurrentMode, modeOptions);
            EditorGUILayout.Space();

            if (m_CurrentMode == PackerMode.ExtractChannel)
            {
                DrawExtractUI();
            }
            else
            {
                DrawPackUI();
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("处理并导出", GUILayout.Height(40)))
            {
                if (m_CurrentMode == PackerMode.ExtractChannel)
                {
                    ExecuteExtraction();
                }
                else
                {
                    ExecutePacking();
                }
            }
        }

        private void DrawExtractUI()
        {
            GUILayout.Label("提取单通道为灰度图", EditorStyles.boldLabel);
            m_ExtractSource = (Texture2D)EditorGUILayout.ObjectField("源纹理", m_ExtractSource, typeof(Texture2D), false);

            // 提取模式允许直接选择原始通道或其反相结果。
            m_ExtractChannel = DrawChannelPopup("要提取的通道", m_ExtractChannel, s_ExtractChannelOptions);
        }

        private void DrawPackUI()
        {
            GUILayout.Label("打包多通道为新纹理", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("输出纹理大小将与最大的输入纹理匹配。缺失或较小的纹理将通过GPU自动插值。", MessageType.Info);

            DrawChannelRow("目标 R 通道", ref m_TexR, ref m_SourceChanR);
            DrawChannelRow("目标 G 通道", ref m_TexG, ref m_SourceChanG);
            DrawChannelRow("目标 B 通道", ref m_TexB, ref m_SourceChanB);
            DrawChannelRow("目标 A 通道", ref m_TexA, ref m_SourceChanA);
        }

        private void DrawChannelRow(string label, ref Texture2D tex, ref Channel chan)
        {
            EditorGUILayout.BeginHorizontal();
            tex = (Texture2D)EditorGUILayout.ObjectField(label, tex, typeof(Texture2D), false);
            chan = DrawChannelPopup(GUIContent.none, chan, s_PackChannelOptions, GUILayout.Width(90));
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// 绘制通道下拉框，并使用自定义名称显示反相通道。
        /// </summary>
        private Channel DrawChannelPopup(string label, Channel currentChannel, Channel[] options, params GUILayoutOption[] layoutOptions)
        {
            return DrawChannelPopup(new GUIContent(label), currentChannel, options, layoutOptions);
        }

        /// <summary>
        /// 绘制通道下拉框，并在找不到当前值时回退到首个可用选项。
        /// </summary>
        private Channel DrawChannelPopup(GUIContent label, Channel currentChannel, Channel[] options, params GUILayoutOption[] layoutOptions)
        {
            int currentIndex = System.Array.IndexOf(options, currentChannel);
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            string[] displayOptions = options.Select(GetChannelDisplayName).ToArray();
            currentIndex = EditorGUILayout.Popup(label, currentIndex, displayOptions, layoutOptions);
            return options[currentIndex];
        }

        /// <summary>
        /// 将通道枚举转换为更直观的显示文本，便于区分普通通道与反相通道。
        /// </summary>
        private static string GetChannelDisplayName(Channel channel)
        {
            switch (channel)
            {
                case Channel.R:
                    return "R";
                case Channel.G:
                    return "G";
                case Channel.B:
                    return "B";
                case Channel.A:
                    return "A";
                case Channel.Black:
                    return "Black";
                case Channel.White:
                    return "White";
                case Channel.OneMinusR:
                    return "1-R";
                case Channel.OneMinusG:
                    return "1-G";
                case Channel.OneMinusB:
                    return "1-B";
                case Channel.OneMinusA:
                    return "1-A";
                default:
                    return channel.ToString();
            }
        }

        private void ExecuteExtraction()
        {
            if (m_ExtractSource == null)
            {
                EditorUtility.DisplayDialog("错误", "请指定源纹理。", "确定");
                return;
            }

            int width = m_ExtractSource.width;
            int height = m_ExtractSource.height;

            RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            rt.enableRandomWrite = true;
            rt.Create();

            int kernel = m_ComputeShader.FindKernel("ExtractChannel");

            m_ComputeShader.SetTexture(kernel, "SourceTexture", m_ExtractSource);
            m_ComputeShader.SetTexture(kernel, "ExtractedResult", rt);
            m_ComputeShader.SetInt("SourceChannelIndex", (int)m_ExtractChannel);
            m_ComputeShader.SetInt("OutputWidth", width);
            m_ComputeShader.SetInt("OutputHeight", height);

            int threadGroupsX = Mathf.CeilToInt(width / 8.0f);
            int threadGroupsY = Mathf.CeilToInt(height / 8.0f);

            m_ComputeShader.Dispatch(kernel, threadGroupsX, threadGroupsY, 1);

            SaveRenderTextureToPNG(rt, "Extracted_" + m_ExtractSource.name + "_" + m_ExtractChannel.ToString(), ShouldSaveExtractedTextureAsSrgb());

            rt.Release();
            DestroyImmediate(rt);
        }

        private void ExecutePacking()
        {
            // 找出最大尺寸
            int maxWidth = 0;
            int maxHeight = 0;

            Texture2D[] texs = { m_TexR, m_TexG, m_TexB, m_TexA };
            foreach (var t in texs)
            {
                if (t != null)
                {
                    maxWidth = Mathf.Max(maxWidth, t.width);
                    maxHeight = Mathf.Max(maxHeight, t.height);
                }
            }

            if (maxWidth == 0 || maxHeight == 0)
            {
                EditorUtility.DisplayDialog("错误", "请至少指定一张纹理。", "确定");
                return;
            }

            RenderTexture rt = new RenderTexture(maxWidth, maxHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            rt.enableRandomWrite = true;
            rt.Create();

            int kernel = m_ComputeShader.FindKernel("PackChannels");

            // 辅助函数：如果纹理为空，则创建一个1x1的黑色纹理代替，防止CS报错
            Texture2D GetValidTexture(Texture2D original)
            {
                if (original != null) return original;
                Texture2D dummy = new Texture2D(1, 1);
                dummy.SetPixel(0, 0, Color.black);
                dummy.Apply();
                return dummy;
            }

            Texture2D validR = GetValidTexture(m_TexR);
            Texture2D validG = GetValidTexture(m_TexG);
            Texture2D validB = GetValidTexture(m_TexB);
            Texture2D validA = GetValidTexture(m_TexA);

            m_ComputeShader.SetTexture(kernel, "TexR", validR);
            m_ComputeShader.SetTexture(kernel, "TexG", validG);
            m_ComputeShader.SetTexture(kernel, "TexB", validB);
            m_ComputeShader.SetTexture(kernel, "TexA", validA);
            m_ComputeShader.SetTexture(kernel, "PackedResult", rt);

            m_ComputeShader.SetInt("SourceChannelForR", (int)m_SourceChanR);
            m_ComputeShader.SetInt("SourceChannelForG", (int)m_SourceChanG);
            m_ComputeShader.SetInt("SourceChannelForB", (int)m_SourceChanB);
            m_ComputeShader.SetInt("SourceChannelForA", (int)m_SourceChanA);

            m_ComputeShader.SetInt("OutputWidth", maxWidth);
            m_ComputeShader.SetInt("OutputHeight", maxHeight);

            int threadGroupsX = Mathf.CeilToInt(maxWidth / 8.0f);
            int threadGroupsY = Mathf.CeilToInt(maxHeight / 8.0f);

            m_ComputeShader.Dispatch(kernel, threadGroupsX, threadGroupsY, 1);

            SaveRenderTextureToPNG(rt, "PackedTexture", ShouldSavePackedTextureAsSrgb());

            // 清理Dummy纹理
            if (m_TexR == null) DestroyImmediate(validR);
            if (m_TexG == null) DestroyImmediate(validG);
            if (m_TexB == null) DestroyImmediate(validB);
            if (m_TexA == null) DestroyImmediate(validA);

            rt.Release();
            DestroyImmediate(rt);
        }

        /// <summary>
        /// 判断纹理导入设置是否启用了 sRGB。
        /// 用于在导出 PNG 时决定是否需要把线性颜色转换回 Gamma，避免颜色纹理发暗。
        /// </summary>
        private bool IsSrgbTexture(Texture2D texture)
        {
            if (texture == null)
            {
                return false;
            }

            string assetPath = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            return importer != null && importer.sRGBTexture;
        }

        /// <summary>
        /// 提取模式下的导出色彩空间判断。
        /// 提取结果默认沿用源纹理的 sRGB 设置。
        /// Alpha 通道提取会在 Compute Shader 中直接读取原始 Alpha 值，这里不再额外做提亮处理，
        /// 避免出现二次 Gamma 导致的结果偏差。
        /// </summary>
        private bool ShouldSaveExtractedTextureAsSrgb()
        {
            return IsSrgbTexture(m_ExtractSource);
        }

        /// <summary>
        /// 打包结果是否应按颜色纹理导出。
        /// 这里优先参考 RGB 三个输入槽，只要任意一个为 sRGB 颜色纹理，就在保存时执行一次线性到 Gamma 的转换。
        /// </summary>
        private bool ShouldSavePackedTextureAsSrgb()
        {
            return IsSrgbTexture(m_TexR) || IsSrgbTexture(m_TexG) || IsSrgbTexture(m_TexB);
        }

        /// <summary>
        /// 将 RenderTexture 导出为 PNG。
        /// 当输出目标是颜色纹理时，会在保存前把线性结果转换回 Gamma，以保证导入后的观感与原图更接近。
        /// </summary>
        private void SaveRenderTextureToPNG(RenderTexture rt, string defaultName, bool convertLinearToSrgb)
        {
            string folder = string.IsNullOrEmpty(m_LastExportPath) || !Directory.Exists(m_LastExportPath) ? "Assets" : m_LastExportPath;
            string path = EditorUtility.SaveFilePanel("保存纹理", folder, defaultName, "png");

            if (string.IsNullOrEmpty(path)) return;

            m_LastExportPath = Path.GetDirectoryName(path);
            SaveSettings();

            // 将RT读取到Texture2D
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;

            if (convertLinearToSrgb)
            {
                Color[] pixels = tex.GetPixels();
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = pixels[i].gamma;
                }

                tex.SetPixels(pixels);
                tex.Apply();
            }

            byte[] bytes = tex.EncodeToPNG();
            DestroyImmediate(tex);

            File.WriteAllBytes(path, bytes);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("成功", "纹理处理并保存成功！", "确定");
        }
    }
}
