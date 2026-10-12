using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AzumisEditorTools.ReflectionProbeCubemapPainter
{
    public class ReflectionProbeCubemapPainterWindow : EditorWindow
    {
        private struct PaintHit
        {
            public bool Hit;
            public Vector3 Position;
            public Vector3 Normal;
            public float Distance;
            public string SourceName;
        }

        private class MeshPaintSource
        {
            public string Name;
            public Matrix4x4 LocalToWorld;
            public Matrix4x4 NormalMatrix;
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public int[] Triangles;
        }

        private const string MenuPath = "Tools/Azumi/纹理生成与修改/反射探针 Cubemap 表面绘制";
        private const string PrefPrefix = "Azumi_ReflectionProbeCubemapPainter_";

        private GameObject m_TargetObject;
        private ReflectionProbe m_TargetProbe;
        private Cubemap m_TargetCubemap;
        private Camera m_ViewCamera;
        private Color m_BrushColor = Color.white;
        private Color m_EraseColor = Color.black;
        private int m_BrushSize = 10;
        private float m_BrushOpacity = 0.65f;
        private float m_BrushHardness = 0.55f;
        private bool m_PaintEnabled;
        private bool m_EraseMode;
        private bool m_FlipCubemapDirection;
        private bool m_AutoApplyEveryDab = true;
        private int m_NewCubemapSize = 256;
        private string m_OutputFolder = "Assets";
        private string m_OutputName = "Painted_ReflectionProbe";
        private string m_Status = string.Empty;

        private readonly List<MeshPaintSource> m_MeshSources = new List<MeshPaintSource>();
        private GameObject m_CachedObject;
        private bool m_CacheDirty = true;
        private PaintHit m_LastHit;
        private bool m_StrokeUndoRegistered;
        private bool m_HasDirtyPixels;

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            ReflectionProbeCubemapPainterWindow window = GetWindow<ReflectionProbeCubemapPainterWindow>("Probe Cubemap Painter");
            window.minSize = new Vector2(480f, 520f);
            window.Show();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += DuringSceneGUI;
            m_BrushSize = EditorPrefs.GetInt(PrefPrefix + "BrushSize", m_BrushSize);
            m_BrushOpacity = EditorPrefs.GetFloat(PrefPrefix + "BrushOpacity", m_BrushOpacity);
            m_BrushHardness = EditorPrefs.GetFloat(PrefPrefix + "BrushHardness", m_BrushHardness);
            m_FlipCubemapDirection = EditorPrefs.GetBool(PrefPrefix + "FlipCubemapDirection", m_FlipCubemapDirection);
            m_AutoApplyEveryDab = EditorPrefs.GetBool(PrefPrefix + "AutoApplyEveryDab", m_AutoApplyEveryDab);
            m_NewCubemapSize = EditorPrefs.GetInt(PrefPrefix + "NewCubemapSize", m_NewCubemapSize);
            m_OutputFolder = EditorPrefs.GetString(PrefPrefix + "OutputFolder", m_OutputFolder);
            m_OutputName = EditorPrefs.GetString(PrefPrefix + "OutputName", m_OutputName);
            m_EraseColor = StringToColor(EditorPrefs.GetString(PrefPrefix + "EraseColor", "0,0,0,1"), m_EraseColor);
            m_BrushColor = StringToColor(EditorPrefs.GetString(PrefPrefix + "BrushColor", "1,1,1,1"), m_BrushColor);
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= DuringSceneGUI;
            ApplyDirtyPixels();
            EditorPrefs.SetInt(PrefPrefix + "BrushSize", m_BrushSize);
            EditorPrefs.SetFloat(PrefPrefix + "BrushOpacity", m_BrushOpacity);
            EditorPrefs.SetFloat(PrefPrefix + "BrushHardness", m_BrushHardness);
            EditorPrefs.SetBool(PrefPrefix + "FlipCubemapDirection", m_FlipCubemapDirection);
            EditorPrefs.SetBool(PrefPrefix + "AutoApplyEveryDab", m_AutoApplyEveryDab);
            EditorPrefs.SetInt(PrefPrefix + "NewCubemapSize", m_NewCubemapSize);
            EditorPrefs.SetString(PrefPrefix + "OutputFolder", m_OutputFolder);
            EditorPrefs.SetString(PrefPrefix + "OutputName", m_OutputName);
            EditorPrefs.SetString(PrefPrefix + "EraseColor", ColorToString(m_EraseColor));
            EditorPrefs.SetString(PrefPrefix + "BrushColor", ColorToString(m_BrushColor));
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("目标", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            m_TargetObject = (GameObject)EditorGUILayout.ObjectField("绘制模型", m_TargetObject, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
            {
                m_CacheDirty = true;
            }

            m_TargetProbe = (ReflectionProbe)EditorGUILayout.ObjectField("Reflection Probe", m_TargetProbe, typeof(ReflectionProbe), true);
            m_TargetCubemap = (Cubemap)EditorGUILayout.ObjectField("可编辑 Cubemap", m_TargetCubemap, typeof(Cubemap), false);
            m_ViewCamera = (Camera)EditorGUILayout.ObjectField("指定视角相机(可空)", m_ViewCamera, typeof(Camera), true);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("使用当前选择对象"))
                {
                    UseCurrentSelection();
                }

                if (GUILayout.Button("从 Probe 读取 Cubemap"))
                {
                    LoadCubemapFromProbe();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("重建模型缓存"))
                {
                    RebuildMeshCache();
                }

                if (GUILayout.Button("创建可编辑 Cubemap"))
                {
                    CreateEditableCubemap();
                }
            }

            if (GUILayout.Button("从当前 Probe/Cubemap 复制为可编辑资产"))
            {
                DuplicateCurrentCubemapAsEditable();
            }

            using (new EditorGUI.DisabledScope(m_TargetProbe == null || m_TargetCubemap == null))
            {
                if (GUILayout.Button("把当前 Cubemap 绑定到 Probe"))
                {
                    BindCubemapToProbe();
                }
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("新建 Cubemap", EditorStyles.boldLabel);
            m_NewCubemapSize = EditorGUILayout.IntPopup("尺寸", m_NewCubemapSize, new[] { "64", "128", "256", "512", "1024" }, new[] { 64, 128, 256, 512, 1024 });
            m_OutputName = EditorGUILayout.TextField("资源名", m_OutputName);
            using (new EditorGUILayout.HorizontalScope())
            {
                m_OutputFolder = EditorGUILayout.TextField("输出目录", m_OutputFolder);
                if (GUILayout.Button("选择", GUILayout.Width(56f)))
                {
                    PickOutputFolder();
                }
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("笔刷", EditorStyles.boldLabel);
            m_PaintEnabled = EditorGUILayout.ToggleLeft("启用 Scene View 绘制", m_PaintEnabled);
            m_EraseMode = EditorGUILayout.ToggleLeft("擦除模式", m_EraseMode);
            using (new EditorGUI.DisabledScope(m_EraseMode))
            {
                m_BrushColor = EditorGUILayout.ColorField("绘制颜色", m_BrushColor);
            }
            using (new EditorGUI.DisabledScope(!m_EraseMode))
            {
                m_EraseColor = EditorGUILayout.ColorField("擦除颜色", m_EraseColor);
            }
            m_BrushSize = EditorGUILayout.IntSlider("笔刷大小( Cubemap 像素)", m_BrushSize, 1, 128);
            m_BrushOpacity = EditorGUILayout.Slider("不透明度", m_BrushOpacity, 0.01f, 1f);
            m_BrushHardness = EditorGUILayout.Slider("硬度", m_BrushHardness, 0f, 1f);
            m_FlipCubemapDirection = EditorGUILayout.Toggle("反转 Cubemap 方向", m_FlipCubemapDirection);
            m_AutoApplyEveryDab = EditorGUILayout.Toggle("每笔实时 Apply", m_AutoApplyEveryDab);

            EditorGUILayout.HelpBox("在 Scene View 中左键拖拽即可绘制。工具会用当前视角射线命中模型，再用 hitPoint -> camera 的视线和模型世界法线计算反射方向，并把颜色刷进 Cubemap。", MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!m_HasDirtyPixels))
                {
                    if (GUILayout.Button("Apply 未提交像素"))
                    {
                        ApplyDirtyPixels();
                    }
                }

                if (GUILayout.Button("保存 Assets"))
                {
                    ApplyDirtyPixels();
                    AssetDatabase.SaveAssets();
                }
            }

            DrawStatus();
        }

        private void DrawStatus()
        {
            EditorGUILayout.Space(8f);
            string cacheInfo = m_MeshSources.Count > 0 ? "模型缓存：" + m_MeshSources.Count + " 个 Mesh" : "模型缓存：未构建";
            EditorGUILayout.LabelField(cacheInfo, EditorStyles.miniLabel);
            if (m_LastHit.Hit)
            {
                EditorGUILayout.LabelField("最近命中：" + m_LastHit.SourceName, EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(m_Status))
            {
                EditorGUILayout.HelpBox(m_Status, MessageType.None);
            }
        }

        private void DuringSceneGUI(SceneView sceneView)
        {
            if (!m_PaintEnabled)
            {
                return;
            }

            Event current = Event.current;
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            HandleUtility.AddDefaultControl(controlId);

            if (m_TargetObject == null || m_TargetCubemap == null)
            {
                Handles.BeginGUI();
                GUILayout.BeginArea(new Rect(12f, 12f, 360f, 48f), EditorStyles.helpBox);
                GUILayout.Label("Probe Cubemap Painter: 请指定绘制模型和可编辑 Cubemap。");
                GUILayout.EndArea();
                Handles.EndGUI();
                return;
            }

            if (m_CacheDirty || m_CachedObject != m_TargetObject)
            {
                RebuildMeshCache();
            }

            Ray ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
            Camera camera = m_ViewCamera != null ? m_ViewCamera : sceneView.camera;
            m_LastHit = RaycastTargetMeshes(ray);
            DrawSceneBrushPreview(camera);

            if (current.alt || current.button != 0)
            {
                return;
            }

            if (current.type == EventType.MouseDown)
            {
                m_StrokeUndoRegistered = false;
                PaintAtLastHit(camera);
                current.Use();
            }
            else if (current.type == EventType.MouseDrag)
            {
                PaintAtLastHit(camera);
                current.Use();
            }
            else if (current.type == EventType.MouseUp)
            {
                ApplyDirtyPixels();
                m_StrokeUndoRegistered = false;
                current.Use();
            }
        }

        private void DrawSceneBrushPreview(Camera camera)
        {
            if (!m_LastHit.Hit)
            {
                return;
            }

            Handles.color = m_EraseMode ? new Color(1f, 0.35f, 0.15f, 0.9f) : new Color(m_BrushColor.r, m_BrushColor.g, m_BrushColor.b, 0.9f);
            float handleSize = HandleUtility.GetHandleSize(m_LastHit.Position);
            float radius = Mathf.Max(0.02f, handleSize * Mathf.Lerp(0.035f, 0.16f, m_BrushSize / 128f));
            Handles.DrawWireDisc(m_LastHit.Position, m_LastHit.Normal, radius);
            Handles.DrawLine(m_LastHit.Position, m_LastHit.Position + m_LastHit.Normal * radius * 0.8f);

            if (camera != null)
            {
                Vector3 viewDir = (camera.transform.position - m_LastHit.Position).normalized;
                Vector3 reflectionDir = Vector3.Reflect(-viewDir, m_LastHit.Normal).normalized;
                Handles.color = new Color(0.1f, 0.65f, 1f, 0.9f);
                Handles.ArrowHandleCap(0, m_LastHit.Position, Quaternion.LookRotation(reflectionDir), radius * 1.2f, EventType.Repaint);
            }
        }

        private void PaintAtLastHit(Camera camera)
        {
            if (!m_LastHit.Hit || camera == null || m_TargetCubemap == null)
            {
                return;
            }

            Vector3 viewDir = (camera.transform.position - m_LastHit.Position).normalized;
            Vector3 reflectionDir = Vector3.Reflect(-viewDir, m_LastHit.Normal).normalized;
            if (m_FlipCubemapDirection)
            {
                reflectionDir = -reflectionDir;
            }

            if (!DirectionToFaceUv(reflectionDir, out CubemapFace face, out float u, out float v))
            {
                return;
            }

            if (!m_StrokeUndoRegistered)
            {
                Undo.RegisterCompleteObjectUndo(m_TargetCubemap, "Paint reflection probe cubemap");
                m_StrokeUndoRegistered = true;
            }

            try
            {
                PaintCubemapFace(face, u, v);
                m_HasDirtyPixels = true;
                if (m_AutoApplyEveryDab)
                {
                    ApplyDirtyPixels();
                }

                m_Status = string.Format("绘制到 {0}  u:{1:F3}  v:{2:F3}", face, u, v);
                Repaint();
                SceneView.RepaintAll();
            }
            catch (Exception ex)
            {
                m_Status = "绘制失败：" + ex.Message + "\n如果 Cubemap 是导入资源，请先用“创建可编辑 Cubemap”生成 .cubemap 资产。";
                Debug.LogException(ex);
            }
        }

        private void PaintCubemapFace(CubemapFace face, float u, float v)
        {
            int size = m_TargetCubemap.width;
            int centerX = Mathf.RoundToInt(Mathf.Clamp01(u) * (size - 1));
            int centerY = Mathf.RoundToInt(Mathf.Clamp01(v) * (size - 1));
            int radius = Mathf.Max(1, m_BrushSize);
            Color targetColor = m_EraseMode ? m_EraseColor : m_BrushColor;
            float hardRadius = radius * Mathf.Clamp01(m_BrushHardness);

            for (int y = centerY - radius; y <= centerY + radius; y++)
            {
                if (y < 0 || y >= size)
                {
                    continue;
                }

                for (int x = centerX - radius; x <= centerX + radius; x++)
                {
                    if (x < 0 || x >= size)
                    {
                        continue;
                    }

                    float distance = Vector2.Distance(new Vector2(x, y), new Vector2(centerX, centerY));
                    if (distance > radius)
                    {
                        continue;
                    }

                    float falloff = distance <= hardRadius ? 1f : Mathf.InverseLerp(radius, hardRadius, distance);
                    float alpha = Mathf.Clamp01(m_BrushOpacity * falloff);
                    Color current = m_TargetCubemap.GetPixel(face, x, y);
                    Color blended = Color.Lerp(current, targetColor, alpha);
                    blended.a = Mathf.Lerp(current.a, targetColor.a, alpha);
                    m_TargetCubemap.SetPixel(face, x, y, blended);
                }
            }
        }

        private void ApplyDirtyPixels()
        {
            if (!m_HasDirtyPixels || m_TargetCubemap == null)
            {
                return;
            }

            m_TargetCubemap.Apply(true, false);
            EditorUtility.SetDirty(m_TargetCubemap);
            if (m_TargetProbe != null && m_TargetProbe.customBakedTexture == m_TargetCubemap)
            {
                EditorUtility.SetDirty(m_TargetProbe);
            }

            m_HasDirtyPixels = false;
        }

        private void UseCurrentSelection()
        {
            if (Selection.activeGameObject == null)
            {
                return;
            }

            m_TargetObject = Selection.activeGameObject;
            m_TargetProbe = m_TargetObject.GetComponentInChildren<ReflectionProbe>(true);
            if (m_TargetProbe != null)
            {
                LoadCubemapFromProbe();
            }

            m_CacheDirty = true;
        }

        private void LoadCubemapFromProbe()
        {
            if (m_TargetProbe == null)
            {
                m_Status = "没有指定 Reflection Probe。";
                return;
            }

            Cubemap cubemap = GetProbeCubemap(m_TargetProbe);
            if (cubemap == null)
            {
                m_Status = "Probe 当前没有可读取到的 Cubemap。可以先创建可编辑 Cubemap。";
                return;
            }

            m_TargetCubemap = cubemap;
            m_Status = "已从 Probe 读取 Cubemap：" + cubemap.name;
        }

        private void CreateEditableCubemap()
        {
            if (!AssetDatabase.IsValidFolder(m_OutputFolder))
            {
                EditorUtility.DisplayDialog("输出目录无效", "请选择 Assets 下的有效输出目录。", "确定");
                return;
            }

            string safeName = SanitizeAssetName(string.IsNullOrWhiteSpace(m_OutputName) ? "Painted_ReflectionProbe" : m_OutputName);
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(m_OutputFolder, safeName + ".cubemap").Replace("\\", "/"));
            Cubemap cubemap = new Cubemap(m_NewCubemapSize, TextureFormat.RGBA32, true);
            cubemap.name = Path.GetFileNameWithoutExtension(assetPath);
            FillCubemap(cubemap, m_EraseColor);
            AssetDatabase.CreateAsset(cubemap, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath);

            m_TargetCubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(assetPath);
            BindCubemapToProbe();

            m_Status = "已创建可编辑 Cubemap：" + assetPath;
        }

        private void FillCubemap(Cubemap cubemap, Color color)
        {
            Color[] pixels = new Color[cubemap.width * cubemap.width];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }

            cubemap.SetPixels(pixels, CubemapFace.PositiveX);
            cubemap.SetPixels(pixels, CubemapFace.NegativeX);
            cubemap.SetPixels(pixels, CubemapFace.PositiveY);
            cubemap.SetPixels(pixels, CubemapFace.NegativeY);
            cubemap.SetPixels(pixels, CubemapFace.PositiveZ);
            cubemap.SetPixels(pixels, CubemapFace.NegativeZ);
            cubemap.Apply(true, false);
        }

        private void DuplicateCurrentCubemapAsEditable()
        {
            if (!AssetDatabase.IsValidFolder(m_OutputFolder))
            {
                EditorUtility.DisplayDialog("输出目录无效", "请选择 Assets 下的有效输出目录。", "确定");
                return;
            }

            Cubemap source = m_TargetCubemap;
            if (source == null && m_TargetProbe != null)
            {
                source = GetProbeCubemap(m_TargetProbe);
            }

            if (source == null)
            {
                CreateEditableCubemap();
                return;
            }

            int size = Mathf.Max(16, source.width);
            string safeName = SanitizeAssetName(string.IsNullOrWhiteSpace(m_OutputName) ? source.name + "_Editable" : m_OutputName);
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(m_OutputFolder, safeName + ".cubemap").Replace("\\", "/"));
            Cubemap cubemap = new Cubemap(size, TextureFormat.RGBA32, true);
            cubemap.name = Path.GetFileNameWithoutExtension(assetPath);

            try
            {
                cubemap.SetPixels(source.GetPixels(CubemapFace.PositiveX), CubemapFace.PositiveX);
                cubemap.SetPixels(source.GetPixels(CubemapFace.NegativeX), CubemapFace.NegativeX);
                cubemap.SetPixels(source.GetPixels(CubemapFace.PositiveY), CubemapFace.PositiveY);
                cubemap.SetPixels(source.GetPixels(CubemapFace.NegativeY), CubemapFace.NegativeY);
                cubemap.SetPixels(source.GetPixels(CubemapFace.PositiveZ), CubemapFace.PositiveZ);
                cubemap.SetPixels(source.GetPixels(CubemapFace.NegativeZ), CubemapFace.NegativeZ);
                cubemap.Apply(true, false);
                m_Status = "已复制当前 Cubemap。";
            }
            catch (Exception ex)
            {
                FillCubemap(cubemap, m_EraseColor);
                m_Status = "当前 Cubemap 不可读，已创建同尺寸空白可编辑资产：" + ex.Message;
            }

            AssetDatabase.CreateAsset(cubemap, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath);
            m_TargetCubemap = AssetDatabase.LoadAssetAtPath<Cubemap>(assetPath);
            BindCubemapToProbe();
        }

        private void BindCubemapToProbe()
        {
            if (m_TargetProbe == null || m_TargetCubemap == null)
            {
                return;
            }

            Undo.RecordObject(m_TargetProbe, "Assign editable reflection cubemap");
            m_TargetProbe.mode = UnityEngine.Rendering.ReflectionProbeMode.Custom;
            m_TargetProbe.customBakedTexture = m_TargetCubemap;
            EditorUtility.SetDirty(m_TargetProbe);
        }

        private static Cubemap GetProbeCubemap(ReflectionProbe probe)
        {
            if (probe == null)
            {
                return null;
            }

            Cubemap cubemap = probe.customBakedTexture as Cubemap;
            if (cubemap != null)
            {
                return cubemap;
            }

            string[] propertyNames = { "texture", "bakedTexture", "realtimeTexture" };
            for (int i = 0; i < propertyNames.Length; i++)
            {
                System.Reflection.PropertyInfo property = typeof(ReflectionProbe).GetProperty(propertyNames[i]);
                if (property == null)
                {
                    continue;
                }

                cubemap = property.GetValue(probe, null) as Cubemap;
                if (cubemap != null)
                {
                    return cubemap;
                }
            }

            return null;
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

        private void RebuildMeshCache()
        {
            m_MeshSources.Clear();
            m_CachedObject = m_TargetObject;
            m_CacheDirty = false;

            if (m_TargetObject == null)
            {
                m_Status = "没有指定绘制模型。";
                return;
            }

            foreach (MeshFilter meshFilter in m_TargetObject.GetComponentsInChildren<MeshFilter>(true))
            {
                AddMeshSource(meshFilter.sharedMesh, meshFilter.transform.localToWorldMatrix, meshFilter.name);
            }

            foreach (SkinnedMeshRenderer skinnedMeshRenderer in m_TargetObject.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                AddMeshSource(skinnedMeshRenderer.sharedMesh, skinnedMeshRenderer.transform.localToWorldMatrix, skinnedMeshRenderer.name);
            }

            m_Status = "已缓存 " + m_MeshSources.Count + " 个 Mesh。";
        }

        private void AddMeshSource(Mesh mesh, Matrix4x4 localToWorld, string sourceName)
        {
            if (mesh == null)
            {
                return;
            }

            if (!mesh.isReadable)
            {
                Debug.LogWarning(sourceName + " 的 Mesh 未开启 Read/Write，Probe Cubemap Painter 无法命中它。", mesh);
                return;
            }

            List<int> triangles = new List<int>();
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                triangles.AddRange(mesh.GetTriangles(subMesh));
            }

            if (triangles.Count == 0)
            {
                return;
            }

            m_MeshSources.Add(new MeshPaintSource
            {
                Name = sourceName,
                LocalToWorld = localToWorld,
                NormalMatrix = localToWorld.inverse.transpose,
                Vertices = mesh.vertices,
                Normals = mesh.normals,
                Triangles = triangles.ToArray()
            });
        }

        private PaintHit RaycastTargetMeshes(Ray ray)
        {
            PaintHit closest = new PaintHit { Hit = false, Distance = float.MaxValue };
            for (int sourceIndex = 0; sourceIndex < m_MeshSources.Count; sourceIndex++)
            {
                MeshPaintSource source = m_MeshSources[sourceIndex];
                for (int i = 0; i < source.Triangles.Length; i += 3)
                {
                    int i0 = source.Triangles[i];
                    int i1 = source.Triangles[i + 1];
                    int i2 = source.Triangles[i + 2];
                    Vector3 p0 = source.LocalToWorld.MultiplyPoint3x4(source.Vertices[i0]);
                    Vector3 p1 = source.LocalToWorld.MultiplyPoint3x4(source.Vertices[i1]);
                    Vector3 p2 = source.LocalToWorld.MultiplyPoint3x4(source.Vertices[i2]);

                    if (!RayIntersectsTriangle(ray, p0, p1, p2, out float distance, out float baryY, out float baryZ))
                    {
                        continue;
                    }

                    if (distance >= closest.Distance)
                    {
                        continue;
                    }

                    float baryX = 1f - baryY - baryZ;
                    Vector3 fallbackNormal = Vector3.Cross(p1 - p0, p2 - p0).normalized;
                    Vector3 normal = InterpolateNormal(source, i0, i1, i2, baryX, baryY, baryZ, fallbackNormal);
                    closest = new PaintHit
                    {
                        Hit = true,
                        Distance = distance,
                        Position = ray.origin + ray.direction * distance,
                        Normal = normal,
                        SourceName = source.Name
                    };
                }
            }

            return closest;
        }

        private Vector3 InterpolateNormal(MeshPaintSource source, int i0, int i1, int i2, float baryX, float baryY, float baryZ, Vector3 fallbackNormal)
        {
            if (source.Normals == null || source.Normals.Length == 0)
            {
                return fallbackNormal;
            }

            Vector3 n0 = source.NormalMatrix.MultiplyVector(source.Normals[i0]);
            Vector3 n1 = source.NormalMatrix.MultiplyVector(source.Normals[i1]);
            Vector3 n2 = source.NormalMatrix.MultiplyVector(source.Normals[i2]);
            Vector3 normal = (n0 * baryX + n1 * baryY + n2 * baryZ).normalized;
            return normal.sqrMagnitude > 0.000001f ? normal : fallbackNormal;
        }

        private static bool RayIntersectsTriangle(Ray ray, Vector3 v0, Vector3 v1, Vector3 v2, out float distance, out float baryY, out float baryZ)
        {
            distance = 0f;
            baryY = 0f;
            baryZ = 0f;
            const float epsilon = 0.0000001f;

            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 pvec = Vector3.Cross(ray.direction, edge2);
            float determinant = Vector3.Dot(edge1, pvec);
            if (Mathf.Abs(determinant) < epsilon)
            {
                return false;
            }

            float invDeterminant = 1f / determinant;
            Vector3 tvec = ray.origin - v0;
            baryY = Vector3.Dot(tvec, pvec) * invDeterminant;
            if (baryY < 0f || baryY > 1f)
            {
                return false;
            }

            Vector3 qvec = Vector3.Cross(tvec, edge1);
            baryZ = Vector3.Dot(ray.direction, qvec) * invDeterminant;
            if (baryZ < 0f || baryY + baryZ > 1f)
            {
                return false;
            }

            distance = Vector3.Dot(edge2, qvec) * invDeterminant;
            return distance > epsilon;
        }

        private static bool DirectionToFaceUv(Vector3 direction, out CubemapFace face, out float u, out float v)
        {
            face = CubemapFace.PositiveX;
            u = 0.5f;
            v = 0.5f;

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

        private static string SanitizeAssetName(string assetName)
        {
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                assetName = assetName.Replace(invalidChar, '_');
            }

            return assetName.Trim();
        }

        private static string ColorToString(Color color)
        {
            return color.r + "," + color.g + "," + color.b + "," + color.a;
        }

        private static Color StringToColor(string value, Color fallback)
        {
            string[] parts = value.Split(',');
            if (parts.Length != 4)
            {
                return fallback;
            }

            if (!float.TryParse(parts[0], out float r) ||
                !float.TryParse(parts[1], out float g) ||
                !float.TryParse(parts[2], out float b) ||
                !float.TryParse(parts[3], out float a))
            {
                return fallback;
            }

            return new Color(r, g, b, a);
        }
    }
}
