using System.IO;
using UnityEditor;
using UnityEngine;

namespace AzumisEditorTools.SdfGenerator
{
    public class SdfTextureGeneratorWindow : EditorWindow
    {
        private const string MenuPath = "Tools/Azumi/纹理生成与修改/SDF 生成器";
        private const string PrefKeyOutputFolder = "Azumi_SdfGenerator_OutputFolder";
        private const string PrefKeyThreshold = "Azumi_SdfGenerator_Threshold";
        private const string PrefKeyUseGreenChannel = "Azumi_SdfGenerator_UseGreenChannel";
        private const string PrefKeySpreadPixels = "Azumi_SdfGenerator_SpreadPixels";
        private const string PrefKeyInvert = "Azumi_SdfGenerator_Invert";

        private Texture2D m_SourceTexture;
        private string m_OutputFolder = "Assets";
        private int m_Threshold = 100;
        private bool m_UseGreenChannel = true;
        private float m_SpreadPixels = 24f;
        private bool m_Invert;
        private Texture2D m_SdfPreviewTexture;
        private ComputeShader m_ComputeShader;
        private string m_ComputeShaderSearchName = "SdfTextureGeneratorCS";

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            SdfTextureGeneratorWindow window = GetWindow<SdfTextureGeneratorWindow>("SDF 生成器");
            window.minSize = new Vector2(460f, 320f);
            window.Show();
        }

        private void OnEnable()
        {
            m_OutputFolder = EditorPrefs.GetString(PrefKeyOutputFolder, "Assets");
            m_Threshold = EditorPrefs.GetInt(PrefKeyThreshold, 100);
            m_UseGreenChannel = EditorPrefs.GetBool(PrefKeyUseGreenChannel, true);
            m_SpreadPixels = EditorPrefs.GetFloat(PrefKeySpreadPixels, 24f);
            m_Invert = EditorPrefs.GetBool(PrefKeyInvert, false);
            LoadComputeShader();
        }

        private void OnDisable()
        {
            EditorPrefs.SetString(PrefKeyOutputFolder, m_OutputFolder);
            EditorPrefs.SetInt(PrefKeyThreshold, m_Threshold);
            EditorPrefs.SetBool(PrefKeyUseGreenChannel, m_UseGreenChannel);
            EditorPrefs.SetFloat(PrefKeySpreadPixels, m_SpreadPixels);
            EditorPrefs.SetBool(PrefKeyInvert, m_Invert);
            if (m_SdfPreviewTexture != null)
            {
                DestroyImmediate(m_SdfPreviewTexture);
                m_SdfPreviewTexture = null;
            }
        }

        private void OnGUI()
        {
            if (m_ComputeShader == null)
            {
                EditorGUILayout.HelpBox("未找到 Compute Shader：SdfTextureGeneratorCS.compute", MessageType.Error);
                if (GUILayout.Button("重试加载 Compute Shader"))
                {
                    LoadComputeShader();
                }
            }

            EditorGUILayout.LabelField("输入设置", EditorStyles.boldLabel);
            m_SourceTexture = (Texture2D)EditorGUILayout.ObjectField("源纹理", m_SourceTexture, typeof(Texture2D), false);
            m_Threshold = EditorGUILayout.IntSlider("白色阈值", m_Threshold, 0, 255);
            m_UseGreenChannel = EditorGUILayout.Toggle("使用G通道判定", m_UseGreenChannel);
            m_SpreadPixels = EditorGUILayout.Slider("SDF扩散像素", m_SpreadPixels, 1f, 256f);
            m_Invert = EditorGUILayout.Toggle("反转输出", m_Invert);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("输出设置", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            m_OutputFolder = EditorGUILayout.TextField("输出目录", m_OutputFolder);
            if (GUILayout.Button("选择", GUILayout.Width(56f)))
            {
                string selected = EditorUtility.OpenFolderPanel("选择输出目录", Application.dataPath, string.Empty);
                if (!string.IsNullOrEmpty(selected))
                {
                    string projectPath = Directory.GetParent(Application.dataPath)?.FullName?.Replace("\\", "/");
                    string normalizedSelected = selected.Replace("\\", "/");
                    if (!string.IsNullOrEmpty(projectPath) && normalizedSelected.StartsWith(projectPath))
                    {
                        m_OutputFolder = normalizedSelected.Substring(projectPath.Length + 1);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("路径无效", "请选择项目目录内的路径。", "确定");
                    }
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10f);
            using (new EditorGUI.DisabledScope(m_SourceTexture == null || m_ComputeShader == null))
            {
                if (GUILayout.Button("预览生成 SDF", GUILayout.Height(30f)))
                {
                    GenerateSdfTexture(false);
                }

                if (GUILayout.Button("生成并导出 SDF", GUILayout.Height(30f)))
                {
                    GenerateSdfTexture(true);
                }
            }

            EditorGUILayout.Space(10f);
            DrawPreview();
        }

        private void LoadComputeShader()
        {
            string[] guids = AssetDatabase.FindAssets(m_ComputeShaderSearchName + " t:ComputeShader");
            if (guids != null && guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                m_ComputeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            }
        }

        private void DrawPreview()
        {
            if (m_SourceTexture == null && m_SdfPreviewTexture == null)
            {
                return;
            }

            EditorGUILayout.LabelField("预览", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal("box");
            DrawPreviewTextureBlock("源纹理", m_SourceTexture);
            DrawPreviewTextureBlock("SDF 预览", m_SdfPreviewTexture);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawPreviewTextureBlock(string title, Texture2D texture)
        {
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
            Rect rect = GUILayoutUtility.GetRect(180f, 180f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f, 1f));
            if (texture != null)
            {
                GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, false);
            }
            EditorGUILayout.EndVertical();
        }

        private void GenerateSdfTexture(bool exportToDisk)
        {
            if (m_SourceTexture == null)
            {
                EditorUtility.DisplayDialog("提示", "请先选择源纹理。", "确定");
                return;
            }

            if (m_ComputeShader == null)
            {
                EditorUtility.DisplayDialog("错误", "未加载到 Compute Shader。", "确定");
                return;
            }

            string sourcePath = AssetDatabase.GetAssetPath(m_SourceTexture);
            if (string.IsNullOrEmpty(sourcePath))
            {
                EditorUtility.DisplayDialog("错误", "无法获取源纹理路径。", "确定");
                return;
            }

            int width = m_SourceTexture.width;
            int height = m_SourceTexture.height;

            RenderTexture insideA = null;
            RenderTexture insideB = null;
            RenderTexture outsideA = null;
            RenderTexture outsideB = null;
            RenderTexture sdfOutput = null;

            try
            {
                insideA = CreateSeedRenderTexture(width, height);
                insideB = CreateSeedRenderTexture(width, height);
                outsideA = CreateSeedRenderTexture(width, height);
                outsideB = CreateSeedRenderTexture(width, height);
                sdfOutput = CreateOutputRenderTexture(width, height);

                DispatchInitSeeds(width, height, m_SourceTexture, insideA, outsideA);
                RunJumpFlood(width, height, ref insideA, ref insideB);
                RunJumpFlood(width, height, ref outsideA, ref outsideB);
                DispatchCompose(width, height, insideA, outsideA, sdfOutput);

                Texture2D sdfTexture = ReadRenderTextureToTexture2D(sdfOutput, m_SourceTexture.name + "_SDF_Preview");
                if (m_SdfPreviewTexture != null)
                {
                    DestroyImmediate(m_SdfPreviewTexture);
                }
                m_SdfPreviewTexture = sdfTexture;
                Repaint();

                if (!exportToDisk)
                {
                    return;
                }

                if (!AssetDatabase.IsValidFolder(m_OutputFolder))
                {
                    EditorUtility.DisplayDialog("错误", "输出目录不存在，请选择有效的 Assets 下目录。", "确定");
                    return;
                }

                string sourceName = Path.GetFileNameWithoutExtension(sourcePath);
                string savePath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(m_OutputFolder, sourceName + "_SDF.png").Replace("\\", "/"));
                byte[] png = sdfTexture.EncodeToPNG();
                File.WriteAllBytes(savePath, png);
                AssetDatabase.ImportAsset(savePath);
                AssetDatabase.Refresh();
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<Texture2D>(savePath);
                EditorUtility.DisplayDialog("完成", "SDF 纹理已生成：\n" + savePath, "确定");
            }
            finally
            {
                ReleaseRenderTexture(ref insideA);
                ReleaseRenderTexture(ref insideB);
                ReleaseRenderTexture(ref outsideA);
                ReleaseRenderTexture(ref outsideB);
                ReleaseRenderTexture(ref sdfOutput);
            }
        }

        private void DispatchInitSeeds(int width, int height, Texture2D sourceTexture, RenderTexture insideSeed, RenderTexture outsideSeed)
        {
            int kernel = m_ComputeShader.FindKernel("InitSeeds");
            m_ComputeShader.SetInt("TextureWidth", width);
            m_ComputeShader.SetInt("TextureHeight", height);
            m_ComputeShader.SetInt("Threshold", m_Threshold);
            m_ComputeShader.SetInt("UseGreenChannel", m_UseGreenChannel ? 1 : 0);
            m_ComputeShader.SetTexture(kernel, "SourceTexture", sourceTexture);
            m_ComputeShader.SetTexture(kernel, "SeedInsideTex", insideSeed);
            m_ComputeShader.SetTexture(kernel, "SeedOutsideTex", outsideSeed);
            DispatchBySize(kernel, width, height);
        }

        private void RunJumpFlood(int width, int height, ref RenderTexture seedA, ref RenderTexture seedB)
        {
            int kernel = m_ComputeShader.FindKernel("JumpFlood");
            int step = GetInitialJumpStep(width, height);
            while (step >= 1)
            {
                m_ComputeShader.SetInt("TextureWidth", width);
                m_ComputeShader.SetInt("TextureHeight", height);
                m_ComputeShader.SetInt("JumpStep", step);
                m_ComputeShader.SetTexture(kernel, "SourceSeedTex", seedA);
                m_ComputeShader.SetTexture(kernel, "DestSeedTex", seedB);
                DispatchBySize(kernel, width, height);

                RenderTexture temp = seedA;
                seedA = seedB;
                seedB = temp;

                step >>= 1;
            }
        }

        private void DispatchCompose(int width, int height, RenderTexture finalInsideSeed, RenderTexture finalOutsideSeed, RenderTexture output)
        {
            int kernel = m_ComputeShader.FindKernel("ComposeSdf");
            m_ComputeShader.SetInt("TextureWidth", width);
            m_ComputeShader.SetInt("TextureHeight", height);
            m_ComputeShader.SetFloat("SdfSpreadPixels", Mathf.Max(1f, m_SpreadPixels));
            m_ComputeShader.SetInt("Invert", m_Invert ? 1 : 0);
            m_ComputeShader.SetTexture(kernel, "FinalSeedInsideTex", finalInsideSeed);
            m_ComputeShader.SetTexture(kernel, "FinalSeedOutsideTex", finalOutsideSeed);
            m_ComputeShader.SetTexture(kernel, "SdfOutputTex", output);
            DispatchBySize(kernel, width, height);
        }

        private void DispatchBySize(int kernel, int width, int height)
        {
            int threadGroupX = Mathf.CeilToInt(width / 8.0f);
            int threadGroupY = Mathf.CeilToInt(height / 8.0f);
            m_ComputeShader.Dispatch(kernel, threadGroupX, threadGroupY, 1);
        }

        private int GetInitialJumpStep(int width, int height)
        {
            int maxDim = Mathf.Max(width, height);
            int step = 1;
            while ((step << 1) < maxDim)
            {
                step <<= 1;
            }
            return Mathf.Max(1, step);
        }

        private RenderTexture CreateSeedRenderTexture(int width, int height)
        {
            RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            rt.enableRandomWrite = true;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.filterMode = FilterMode.Point;
            rt.Create();
            return rt;
        }

        private RenderTexture CreateOutputRenderTexture(int width, int height)
        {
            RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            rt.enableRandomWrite = true;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.filterMode = FilterMode.Bilinear;
            rt.Create();
            return rt;
        }

        private void ReleaseRenderTexture(ref RenderTexture rt)
        {
            if (rt == null)
            {
                return;
            }

            if (rt.IsCreated())
            {
                rt.Release();
            }
            DestroyImmediate(rt);
            rt = null;
        }

        private Texture2D ReadRenderTextureToTexture2D(RenderTexture rt, string textureName)
        {
            RenderTexture old = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true)
            {
                name = textureName
            };
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            tex.Apply(false, false);
            RenderTexture.active = old;
            return tex;
        }
    }
}
