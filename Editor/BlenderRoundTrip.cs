using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Debug = UnityEngine.Debug;

namespace Nyades.BlenderRoundTrip
{
    public class BlenderRoundTrip : EditorWindow
    {
        const string Preferences = "Nyades.BlenderRoundTrip.";
        [SerializeField] GameObject avatar;
        [SerializeField] string blend = "", blender = "", dataFolder = "Assets/Nyades/BlenderRoundTripData";
        [SerializeField] bool importRig = true;
        [SerializeField] bool copyBlendShapeValues = true;
        string status = "Select the avatar root from the Hierarchy.";
        string errorDetails = "";
        bool failed;
        Process process;
        GameObject operationAvatar;
        string operation, destination, manifest, processLog = "";
        readonly object logLock = new object();
        static string Project => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string ProjectPreference => Preferences + Application.dataPath + ".";

        [MenuItem("Nyades/Blender Round Trip")]
        public static void Open() => GetWindow<BlenderRoundTrip>("Blender Round Trip");

        void OnEnable()
        {
            blender = EditorPrefs.GetString(Preferences + "Blender", FindBlender());
            blend = EditorPrefs.GetString(ProjectPreference + "Blend", blend);
            dataFolder = EditorPrefs.GetString(ProjectPreference + "Data", dataFolder);
            copyBlendShapeValues = EditorPrefs.GetBool(ProjectPreference + "CopyBlendShapeValues", copyBlendShapeValues);
            EditorApplication.update += Poll;
        }

        void OnDisable()
        {
            EditorPrefs.SetString(Preferences + "Blender", blender);
            EditorPrefs.SetString(ProjectPreference + "Blend", blend);
            EditorPrefs.SetString(ProjectPreference + "Data", dataFolder);
            EditorPrefs.SetBool(ProjectPreference + "CopyBlendShapeValues", copyBlendShapeValues);
            EditorApplication.update -= Poll;
            if (process != null)
            {
                // A closed window must not leave a late import running against another selection.
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                process.Dispose(); process = null;
            }
        }

        static string FindBlender()
        {
            var steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam/steamapps/common/Blender/blender.exe");
            if (File.Exists(steam)) return steam;
            var foundation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
            return Directory.Exists(foundation) ? Directory.GetFiles(foundation, "blender.exe", SearchOption.AllDirectories).OrderByDescending(p => p).FirstOrDefault() ?? "" : "";
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Unity / Blender mesh and rig round trip", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Select an avatar root. Export meshes and bones, edit the rest rig in Blender Edit Mode, then import. Existing bone objects and their components are retained. Keep existing bone identifiers and parents. Material slots may be added, removed or reordered.", MessageType.Info);
            using (new EditorGUI.DisabledScope(process != null))
            {
                avatar = (GameObject)EditorGUILayout.ObjectField("Avatar root", avatar, typeof(GameObject), true);
                if (GUILayout.Button("Use selected object")) avatar = Selection.activeGameObject;
                FileField("Blender executable", ref blender, "exe");
                FileField("Saved Blender file", ref blend, "blend", true);
                dataFolder = EditorGUILayout.TextField("Import / backup folder", dataFolder);
                importRig = EditorGUILayout.Toggle("Import bone edits", importRig);
                copyBlendShapeValues = EditorGUILayout.ToggleLeft(new GUIContent("Export Unity blendshape values", "Enabled: copy the current Unity blendshape values into Blender. Disabled: set all Blender shape keys to zero."), copyBlendShapeValues);
                if (avatar != null) EditorGUILayout.LabelField("Meshes", Targets(avatar).Length.ToString());
                if (GUILayout.Button("Unity -> New Blender File"))
                {
                    var path = EditorUtility.SaveFilePanel("Export avatar to Blender", File.Exists(blend) ? Path.GetDirectoryName(blend) : Project, avatar == null ? "Avatar" : SafeName(avatar.name) + "_Edit", "blend");
                    if (path != "") Run(() => Begin("pull", path));
                }
                if (GUILayout.Button("Saved Blender File -> Unity")) Run(() => Begin("push", NewIncoming()));
                if (GUILayout.Button("Import Existing FBX"))
                {
                    var path = EditorUtility.OpenFilePanel("Avatar FBX", Project, "fbx");
                    if (path != "") Run(() =>
                    {
                        CheckRoot(avatar);
                        string incoming = NewIncoming();
                        File.Copy(path, Absolute(incoming));
                        string sidecar = Path.ChangeExtension(path, ".json");
                        status = Import(avatar, incoming, File.Exists(sidecar) ? sidecar : null, importBones: importRig);
                    });
                }
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Nyades FT Toolkit (Blender add-on)", EditorStyles.boldLabel);
                if (GUILayout.Button("Open Live FT Preview"))
                {
                    string sidecar = string.IsNullOrEmpty(blend) ? "" : Path.ChangeExtension(blend, ".json");
                    FTLivePreview.Open(avatar, File.Exists(sidecar) ? sidecar : "");
                }
                if (GUILayout.Button(new GUIContent("Open FT Toolkit Add-on", "Reveal the installable ZIP. In Blender: Preferences > Get Extensions > Install from Disk.")))
                    Run(() =>
                    {
                        string archive = PackageUpdates.ToolFile("Editor/Blender/nyades_ft_toolkit.zip");
                        if (!File.Exists(archive)) throw new Exception("The FT Toolkit add-on ZIP is missing. Copy the complete BlenderRoundTrip folder.");
                        EditorUtility.RevealInFinder(archive);
                        status = "Install nyades_ft_toolkit.zip in Blender Preferences > Get Extensions > Install from Disk. Select a mesh, then open the 3D View sidebar (N) > FT Toolkit.";
                    });
            }
            EditorGUILayout.HelpBox(status, failed ? MessageType.Error : MessageType.Info);
            if (failed && GUILayout.Button(new GUIContent("Copy Error", "Copy the complete error, traceback and Blender output.")))
                EditorGUIUtility.systemCopyBuffer = string.IsNullOrEmpty(errorDetails) ? status : errorDetails;
            EditorGUILayout.LabelField("Edit bones in Blender Edit Mode and save with Ctrl+S. Save the Unity scene after import. Ctrl+Z restores meshes and rig.", EditorStyles.wordWrappedMiniLabel);
            PackageUpdates.Draw();
        }

        static void FileField(string label, ref string value, string extension, bool showFolder = false)
        {
            EditorGUILayout.BeginHorizontal(); value = EditorGUILayout.TextField(label, value);
            if (GUILayout.Button("Browse", GUILayout.Width(65)))
            { var p = EditorUtility.OpenFilePanel(label, "", extension); if (p != "") value = p; }
            if (showFolder)
            {
                using (new EditorGUI.DisabledScope(!File.Exists(value)))
                    if (GUILayout.Button(new GUIContent("Open Folder", "Open the containing folder and select the Blender file."), GUILayout.Width(90)))
                        EditorUtility.RevealInFinder(Path.GetFullPath(value));
            }
            EditorGUILayout.EndHorizontal();
        }

        void Run(Action action)
        {
            try { action(); failed = false; errorDetails = ""; }
            catch (Exception e) { status = e.Message; errorDetails = e.ToString(); failed = true; Debug.LogException(e); }
        }

        string NewIncoming()
        {
            CheckRoot(avatar);
            string folder = ValidateDataFolder(dataFolder) + "/" + SafeName(avatar.name) + "/Incoming/" + Stamp();
            Directory.CreateDirectory(Absolute(folder));
            return folder + "/Edited.fbx";
        }

        static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
        void Begin(string mode, string path)
        {
            CheckRoot(avatar);
            if (!File.Exists(blender)) throw new Exception("Choose the Blender executable.");
            if (mode == "push" && !File.Exists(blend)) throw new Exception("Choose a saved .blend file.");
            if (mode == "pull" && File.Exists(blend) && string.Equals(Path.GetFullPath(path), Path.GetFullPath(blend), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Choose a new filename to preserve the edited Blender source.");
            string transport = PackageUpdates.ToolFile("Editor/Blender/roundtrip.py");
            if (!File.Exists(transport)) throw new Exception("The tool's Blender/roundtrip.py is missing. Copy the complete BlenderRoundTrip folder.");
            operationAvatar = avatar; operation = mode; destination = mode == "push" ? Absolute(path) : path;
            manifest = Path.ChangeExtension(destination, ".json");
            if (mode == "pull") ExportSnapshot(avatar, manifest, copyBlendShapeValues);
            string args = "--factory-startup --background --python " + Quote(transport) + " -- --mode " + mode + " --output " + Quote(destination) + " --json " + Quote(manifest);
            if (mode == "push") args += " --source " + Quote(blend);
            processLog = "";
            var next = new Process { StartInfo = new ProcessStartInfo { FileName = blender, Arguments = args, WorkingDirectory = Project, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true } };
            next.OutputDataReceived += ReceiveLog; next.ErrorDataReceived += ReceiveLog;
            try { next.Start(); next.BeginOutputReadLine(); next.BeginErrorReadLine(); process = next; }
            catch { next.Dispose(); throw; }
            status = mode == "pull" ? "Creating editable Blender file..." : "Exporting the saved Blender file...";
        }

        void ReceiveLog(object sender, DataReceivedEventArgs e)
        { if (e.Data != null) lock (logLock) processLog += e.Data + "\n"; }

        void Poll()
        {
            if (process == null || !process.HasExited) return;
            process.WaitForExit(); int code = process.ExitCode; process.Dispose(); process = null;
            Run(() =>
            {
                if (code != 0 || !File.Exists(destination)) throw new Exception("Blender transport failed.\n" + processLog);
                if (operation == "push") status = Import(operationAvatar, AssetPath(destination), File.Exists(manifest) ? manifest : null, importBones: importRig);
                else { blend = destination; status = "Saved editable Blender file: " + destination; }
            });
            Repaint();
        }

        static string Stamp() => DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        static string SafeName(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));
        static string Absolute(string path)
        {
            if (!Path.IsPathRooted(path) && path.Replace('\\', '/').StartsWith("Packages/", StringComparison.Ordinal))
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
                if (package != null) return Path.GetFullPath(Path.Combine(package.resolvedPath, path.Substring(package.assetPath.Length).TrimStart('/', '\\')));
            }
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Project, path));
        }
        static string AssetPath(string path)
        {
            string full = Absolute(path).Replace('\\', '/'), assets = Application.dataPath.Replace('\\', '/') + "/";
            if (!full.StartsWith(assets, StringComparison.OrdinalIgnoreCase)) throw new Exception("The imported FBX must be inside this project's Assets folder.");
            return "Assets/" + full.Substring(assets.Length);
        }
        static string ValidateDataFolder(string path)
        {
            string result = AssetPath(path).TrimEnd('/');
            if (result.StartsWith("Assets/Nyades/BlenderRoundTrip/", StringComparison.OrdinalIgnoreCase) || result == "Assets/Nyades/BlenderRoundTrip")
                throw new Exception("Choose a data folder outside the portable BlenderRoundTrip tool folder.");
            return result;
        }

        static void CheckRoot(GameObject root)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new Exception("Stop Play Mode before syncing.");
            if (root == null || EditorUtility.IsPersistent(root) || !root.scene.IsValid()) throw new Exception("Select the avatar root in a scene or Prefab Mode.");
            if (Targets(root).Length == 0) throw new Exception("The selected root has no supported meshes.");
        }

        class Target
        {
            public Renderer renderer;
            public MeshFilter filter;
            public SkinnedMeshRenderer Skin => renderer as SkinnedMeshRenderer;
            public Mesh Mesh { get => Skin != null ? Skin.sharedMesh : filter.sharedMesh; set { if (Skin != null) Skin.sharedMesh = value; else filter.sharedMesh = value; } }
        }
        static Target[] Targets(GameObject root)
        {
            var skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).Select(r => new Target { renderer = r });
            var meshes = root.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null).Select(f => new Target { renderer = f.GetComponent<MeshRenderer>(), filter = f });
            return skinned.Concat(meshes).OrderBy(t => HierarchyPath(t.renderer.transform, root.transform), StringComparer.Ordinal).ToArray();
        }

        // Sibling indices disambiguate repeated names without changing the actual avatar hierarchy.
        static string HierarchyPath(Transform transform, Transform root)
        {
            if (transform == root) return "";
            var indices = new List<int>();
            while (transform != null && transform != root) { indices.Add(transform.GetSiblingIndex()); transform = transform.parent; }
            if (transform != root) throw new Exception("A mesh bone is outside the selected avatar root.");
            indices.Reverse(); return string.Join("/", indices);
        }
        static Transform Resolve(Transform root, string path)
        {
            if (path == "") return root;
            foreach (string segment in path.Split('/'))
            { int i; if (!int.TryParse(segment, out i) || i < 0 || i >= root.childCount) return null; root = root.GetChild(i); }
            return root;
        }
        static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
        static Vector3 V(float[] v) => new Vector3(v[0], v[1], v[2]);

        [Serializable] public class Payload { public int version = 2; public string avatar; public BoneData[] rig, editedRig; public MeshData[] meshes; }
        [Serializable] public class BoneData { public string id, path, name, parent; public float[] position, rotation, scale, matrix; }
        [Serializable] public class FloatArray { public float[] values; }
        [Serializable] public class IntArray { public int[] values; }
        [Serializable] public class WeightData { public int[] indices; public float[] values; }
        [Serializable] public class ShapeData { public string name; public float weight, value; public FloatArray[] delta; }
        [Serializable] public class MaterialData { public string name, texture; public float[] color; }
        [Serializable] public class MaterialSlotData { public string name, originalMesh; public int originalSlot = -1; public bool empty; }
        [Serializable] public class MeshData
        {
            public string id, path, name; public bool skinned;
            public FloatArray[] vertices, normals, colors; public UVData[] uv;
            public IntArray[] triangles; public string[] bones; public WeightData[] weights;
            public ShapeData[] shapes; public MaterialData[] materials;
            public MaterialSlotData[] editedMaterials;
        }
        [Serializable] public class UVData { public FloatArray[] values; }
        static FloatArray F(Vector3 value) => new FloatArray { values = V(value) };
        static float[] Q(Quaternion value) => new[] { value.x, value.y, value.z, value.w };
        static Quaternion Q(float[] value)
        {
            if (value == null || value.Length != 4 || value.Any(v => float.IsNaN(v) || float.IsInfinity(v))) throw new Exception("Missing bone orientation. Export a new Blender file.");
            var q = new Quaternion(value[0], value[1], value[2], value[3]);
            if (Quaternion.Dot(q, q) < .00001f) throw new Exception("Invalid bone orientation.");
            return q.normalized;
        }
        static float[] MatrixValues(Matrix4x4 matrix) => Enumerable.Range(0, 16).Select(i => matrix[i / 4, i % 4]).ToArray();
        static Matrix4x4 ReadMatrix(float[] values)
        {
            if (values == null || values.Length != 16 || values.Any(v => float.IsNaN(v) || float.IsInfinity(v))) throw new Exception("Invalid bone matrix. Export a fresh Blender file with this version of the tool.");
            var matrix = new Matrix4x4(); for (int i = 0; i < 16; i++) matrix[i / 4, i % 4] = values[i]; return matrix;
        }

        public static void ExportSnapshot(GameObject root, string path, bool copyShapeValues = true)
        {
            CheckRoot(root);
            var targets = Targets(root); var rig = new HashSet<Transform>();
            foreach (var target in targets.Where(t => t.Skin != null))
                foreach (var bone in target.Skin.bones)
                {
                    if (bone == null) throw new Exception("Missing bone on " + target.renderer.name);
                    HierarchyPath(bone, root.transform);
                    if (bone == root.transform) rig.Add(bone);
                    for (var t = bone; t != root.transform; t = t.parent) rig.Add(t);
                    // Include unweighted end bones and accessory bones, while keeping render objects separate.
                    foreach (var child in bone.GetComponentsInChildren<Transform>(true))
                        if (child != root.transform && !targets.Any(r => child == r.renderer.transform || child.IsChildOf(r.renderer.transform))) rig.Add(child);
                }
            var ordered = rig.OrderBy(t => HierarchyPath(t, root.transform), StringComparer.Ordinal).ToArray();
            foreach (var bone in ordered.Where(t => t != root.transform))
                if (bone.localScale.x <= .000001f || bone.localScale.y <= .000001f || bone.localScale.z <= .000001f) throw new Exception("Use positive, non-zero bone scales: " + bone.name);
            var ids = ordered.Select((t, i) => new { t, id = "NRTB_" + i.ToString("D4") }).ToDictionary(x => x.t, x => x.id);
            var bones = ordered.Select(t => new BoneData { id = ids[t], path = HierarchyPath(t, root.transform), name = t.name, parent = t.parent != null && ids.ContainsKey(t.parent) ? ids[t.parent] : null, position = V(root.transform.InverseTransformPoint(t.position)), rotation = Q(Quaternion.Inverse(root.transform.rotation) * t.rotation), scale = V(t == root.transform ? Vector3.one : t.localScale), matrix = MatrixValues(root.transform.worldToLocalMatrix * t.localToWorldMatrix) }).ToArray();
            var items = new List<MeshData>();
            for (int index = 0; index < targets.Length; index++)
            {
                var target = targets[index]; var m = target.Mesh;
                for (int slot = 0; slot < m.subMeshCount; slot++) if (m.GetTopology(slot) != MeshTopology.Triangles) throw new Exception("Only triangle meshes are supported: " + target.renderer.name);
                if (target.renderer.sharedMaterials.Length != m.subMeshCount) throw new Exception("Material count must equal submesh count: " + target.renderer.name);
                if (target.Skin != null)
                {
                    var counts = m.GetBonesPerVertex();
                    try { foreach (var count in counts) if (count > 4) throw new Exception("More than four skin weights per vertex are not supported: " + target.renderer.name); }
                    finally { counts.Dispose(); }
                }
                var matrix = root.transform.worldToLocalMatrix * target.renderer.transform.localToWorldMatrix;
                var shapes = new List<ShapeData>();
                for (int i = 0; i < m.blendShapeCount; i++)
                {
                    if (m.GetBlendShapeFrameCount(i) != 1 || m.GetBlendShapeFrameWeight(i, 0) <= 0) throw new Exception("Only single-frame positive-weight blendshapes are supported: " + m.GetBlendShapeName(i));
                    var delta = new Vector3[m.vertexCount]; m.GetBlendShapeFrameVertices(i, 0, delta, new Vector3[m.vertexCount], new Vector3[m.vertexCount]);
                    shapes.Add(new ShapeData { name = m.GetBlendShapeName(i), weight = m.GetBlendShapeFrameWeight(i, 0), value = copyShapeValues && target.Skin != null ? target.Skin.GetBlendShapeWeight(i) : 0, delta = delta.Select(v => F(matrix.MultiplyVector(v))).ToArray() });
                }
                var channels = new List<UVData>();
                for (int channel = 0; channel < 8; channel++)
                {
                    var values = new List<Vector4>(); m.GetUVs(channel, values);
                    if (values.Count > 0 && m.GetVertexAttributeDimension((VertexAttribute)((int)VertexAttribute.TexCoord0 + channel)) > 2) throw new Exception("Blender supports 2D UV channels only: " + target.renderer.name);
                    channels.Add(new UVData { values = values.Select(v => new FloatArray { values = new[] { v.x, v.y } }).ToArray() });
                }
                items.Add(new MeshData
                {
                    id = "NRTM_" + index.ToString("D4"), path = HierarchyPath(target.renderer.transform, root.transform), name = target.renderer.name, skinned = target.Skin != null,
                    vertices = m.vertices.Select(v => F(matrix.MultiplyPoint3x4(v))).ToArray(), normals = m.normals.Select(v => F(matrix.inverse.transpose.MultiplyVector(v).normalized)).ToArray(),
                    colors = m.colors.Select(c => new FloatArray { values = new[] { c.r, c.g, c.b, c.a } }).ToArray(), uv = channels.ToArray(),
                    triangles = Enumerable.Range(0, m.subMeshCount).Select(i => new IntArray { values = m.GetTriangles(i) }).ToArray(),
                    bones = target.Skin == null ? new string[0] : target.Skin.bones.Select(b => ids[b]).ToArray(),
                    weights = target.Skin == null ? new WeightData[0] : m.boneWeights.Select(w => new WeightData { indices = new[] { w.boneIndex0, w.boneIndex1, w.boneIndex2, w.boneIndex3 }, values = new[] { w.weight0, w.weight1, w.weight2, w.weight3 } }).ToArray(),
                    shapes = shapes.ToArray(), materials = target.renderer.sharedMaterials.Select((mat, slot) => new MaterialData
                    {
                        name = mat == null ? "Empty_" + slot : mat.name,
                        texture = mat == null || mat.mainTexture == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mat.mainTexture)) ? null : Absolute(AssetDatabase.GetAssetPath(mat.mainTexture)),
                        color = mat != null && mat.HasProperty("_Color") ? new[] { mat.color.r, mat.color.g, mat.color.b, mat.color.a } : new[] { 1f, 1f, 1f, 1f }
                    }).ToArray()
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Absolute(path)));
            File.WriteAllText(Absolute(path), JsonUtility.ToJson(new Payload { avatar = root.name, rig = bones, meshes = items.ToArray() }));
        }

        class Replacement { public Target target; public Mesh mesh; public Transform[] bones; public string[] boneIds; public Dictionary<string, float> shapeValues; public Material[] materials; }

        class BoneChange
        {
            public BoneData data; public Transform target; public Matrix4x4 modelMatrix;
            public Vector3 localPosition, localScale; public Quaternion localRotation, modelRotation;
        }
        class RigPlan
        {
            public readonly List<BoneChange> changes = new List<BoneChange>();
            public readonly Dictionary<string, BoneChange> byId = new Dictionary<string, BoneChange>();
            readonly GameObject root;
            public bool Changed => changes.Any(c => c.target != root.transform && (c.target == null || (c.target.localPosition - c.localPosition).sqrMagnitude > 1e-10f || Quaternion.Angle(c.target.localRotation, c.localRotation) > .001f || (c.target.localScale - c.localScale).sqrMagnitude > 1e-10f));
            public RigPlan(GameObject root, Payload payload)
            {
                this.root = root;
                var originals = payload.rig.ToDictionary(b => b.id);
                var edited = payload.editedRig.ToDictionary(b => b.id);
                if (originals.Keys.Any(id => !edited.ContainsKey(id))) throw new Exception("An existing bone was deleted. Keep existing bones to preserve Unity component references.");
                var pending = edited.Values.ToList();
                while (pending.Count > 0)
                {
                    var ready = pending.Where(b => string.IsNullOrEmpty(b.parent) || byId.ContainsKey(b.parent)).ToArray();
                    if (ready.Length == 0) throw new Exception("The edited bone hierarchy contains a cycle or unknown parent.");
                    foreach (var bone in ready)
                    {
                        BoneData original; Transform target = null;
                        if (originals.TryGetValue(bone.id, out original))
                        {
                            target = Resolve(root.transform, original.path);
                            if (target == null || (original.path != "" && target.name != original.name)) throw new Exception("Unity bone hierarchy changed: " + original.name);
                            if (bone.parent != original.parent) throw new Exception("Keep the parent of existing bone " + original.name + ". New bones may use any exported bone as parent.");
                            bone.name = original.name;
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(bone.name) || bone.name.Contains('/')) throw new Exception("Invalid new bone name.");
                            var parent = string.IsNullOrEmpty(bone.parent) ? root.transform : byId[bone.parent].target;
                            if (parent != null)
                            {
                                var matches = Enumerable.Range(0, parent.childCount).Select(i => parent.GetChild(i)).Where(t => t.name == bone.name).ToArray();
                                if (matches.Length > 1) throw new Exception("Ambiguous new bone name: " + bone.name);
                                target = matches.SingleOrDefault();
                                if (target != null && changes.Any(c => c.target == target)) throw new Exception("New bone duplicates an existing exported bone: " + bone.name);
                            }
                        }
                        var desiredMatrix = ReadMatrix(bone.matrix);
                        var parentMatrix = string.IsNullOrEmpty(bone.parent) ? Matrix4x4.identity : byId[bone.parent].modelMatrix;
                        var parentRotation = string.IsNullOrEmpty(bone.parent) ? Quaternion.identity : byId[bone.parent].modelRotation;
                        var modelRotation = Q(bone.rotation);
                        if (bone.scale == null || bone.scale.Length != 3 || bone.scale.Any(v => float.IsNaN(v) || float.IsInfinity(v))) throw new Exception("Missing bone scale. Export a new Blender file.");
                        Vector3 position = parentMatrix.inverse.MultiplyPoint3x4(desiredMatrix.GetColumn(3)), scale = V(bone.scale);
                        Quaternion rotation = Quaternion.Inverse(parentRotation) * modelRotation;
                        if (scale.x <= .000001f || scale.y <= .000001f || scale.z <= .000001f)
                            throw new Exception("Use positive, non-zero bone scales: " + bone.name);
                        // Unity stores local TRS, whereas Blender edit bones store a rest matrix and length.
                        // Construct the actual future matrices recursively, including parent nonuniform scale.
                        var modelMatrix = parentMatrix * Matrix4x4.TRS(position, rotation, scale);
                        if (target == root.transform && MatrixError(modelMatrix, Matrix4x4.identity) > .0001f) throw new Exception("Keep the avatar's helper root bone unchanged; edit bones below it.");
                        var change = new BoneChange { data = bone, target = target, modelMatrix = modelMatrix, modelRotation = modelRotation, localPosition = position, localRotation = rotation, localScale = scale };
                        byId.Add(bone.id, change); changes.Add(change); pending.Remove(bone);
                    }
                }
            }
            public Matrix4x4 ModelMatrix(Transform transform)
            {
                if (transform == root.transform) return Matrix4x4.identity;
                var bone = changes.FirstOrDefault(c => c.target == transform);
                return bone != null ? bone.modelMatrix : ModelMatrix(transform.parent) * Matrix4x4.TRS(transform.localPosition, transform.localRotation, transform.localScale);
            }
            public Dictionary<string, Transform> Apply(GameObject destination, bool undo)
            {
                // Resolve every old path before new objects or modified transforms are applied.
                var targets = changes.ToDictionary(c => c.data.id, c => c.target == null ? null : Resolve(destination.transform, HierarchyPath(c.target, root.transform)));
                foreach (var change in changes)
                {
                    var transform = targets[change.data.id];
                    if (transform == null)
                    {
                        var obj = new GameObject(change.data.name);
                        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(obj, destination.scene);
                        transform = obj.transform; transform.SetParent(string.IsNullOrEmpty(change.data.parent) ? destination.transform : targets[change.data.parent], false);
                        if (undo) Undo.RegisterCreatedObjectUndo(obj, "Add avatar bone");
                        targets[change.data.id] = transform;
                    }
                    if (transform == destination.transform) continue;
                    if (undo) Undo.RecordObject(transform, "Update avatar bone rest pose");
                    transform.localPosition = change.localPosition; transform.localRotation = change.localRotation; transform.localScale = change.localScale;
                    if (undo) { EditorUtility.SetDirty(transform); PrefabUtility.RecordPrefabInstancePropertyModifications(transform); }
                }
                return targets;
            }
        }
        static float MatrixError(Matrix4x4 a, Matrix4x4 b) => Enumerable.Range(0, 16).Max(i => Mathf.Abs(a[i] - b[i]));
        class AvatarChange { public Animator animator; public Avatar avatar; }
        static List<AvatarChange> PrepareHumanoidAvatars(GameObject root, RigPlan rig)
        {
            var result = new List<AvatarChange>();
            if (rig == null || !rig.Changed) return result;
            var preview = EditorSceneManager.NewPreviewScene();
            var copy = Object.Instantiate(root); copy.name = root.name;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(copy, preview);
            try
            {
                rig.Apply(copy, false);
                foreach (var animator in root.GetComponentsInChildren<Animator>(true).Where(a => a.avatar != null && a.avatar.isHuman))
                {
                    var corresponding = Resolve(copy.transform, HierarchyPath(animator.transform, root.transform));
                    var description = animator.avatar.humanDescription;
                    description.skeleton = corresponding.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray();
                    var updated = AvatarBuilder.BuildHumanAvatar(corresponding.gameObject, description);
                    if (updated == null || !updated.isValid || !updated.isHuman)
                    { if (updated != null) Object.DestroyImmediate(updated); throw new Exception("The edited skeleton cannot build a valid Humanoid Avatar. The target avatar has not been changed."); }
                    updated.name = animator.avatar.name + " Round Trip"; result.Add(new AvatarChange { animator = animator, avatar = updated });
                }
                return result;
            }
            catch { foreach (var change in result) Object.DestroyImmediate(change.avatar); throw; }
            finally { Object.DestroyImmediate(copy); EditorSceneManager.ClosePreviewScene(preview); }
        }

        // keepShapes is retained for existing callers; the imported file defines the complete key set.
        public static string Import(GameObject root, string incoming, string manifestPath = null, bool keepShapes = false, bool importBones = true)
        {
            CheckRoot(root); incoming = AssetPath(incoming);
            AssetDatabase.Refresh(); var importer = AssetImporter.GetAtPath(incoming) as ModelImporter;
            if (importer == null) throw new Exception("The exported FBX was not found.");
            importer.isReadable = true; importer.importBlendShapes = true; importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.Generic; importer.importNormals = ModelImporterNormals.Import;
            importer.importBlendShapeNormals = ModelImporterNormals.None; importer.optimizeGameObjects = false;
            importer.preserveHierarchy = true; // Keep NRTM object names even in single-mesh FBX files.
            // Embedded FBX materials supply slot names and a preview material for new slots.
            // Known slots are mapped back to the actual Unity materials, including custom shaders.
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab; importer.maxBonesPerVertex = 4;
            importer.SaveAndReimport();
            Payload payload = null;
            if (!string.IsNullOrEmpty(manifestPath))
            {
                payload = JsonUtility.FromJson<Payload>(File.ReadAllText(Absolute(manifestPath)));
                if (payload == null || (payload.version != 1 && payload.version != 2) || payload.meshes == null || payload.rig == null) throw new Exception("Unsupported round-trip manifest.");
            }
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(incoming);
            if (asset == null) throw new Exception("The FBX contains no model.");
            var source = Object.Instantiate(asset); source.hideFlags = HideFlags.HideAndDontSave;
            var replacements = new List<Replacement>();
            var avatarChanges = new List<AvatarChange>();
            try
            {
                var rig = payload != null && payload.version == 2 && payload.editedRig != null ? new RigPlan(root, payload) : null;
                if (!importBones && rig != null && rig.Changed) throw new Exception("The rest rig changed. Enable Import bone edits or restore the exported rest pose.");
                if (!importBones) rig = null;
                var sources = Targets(source); var targets = Targets(root); var used = new HashSet<Target>();
                if (sources.Length != targets.Length) throw new Exception("Mesh count differs. Keep all exported mesh objects; merging, adding or deleting objects is not supported.");
                foreach (var target in targets)
                {
                    string targetPath = HierarchyPath(target.renderer.transform, root.transform);
                    var item = payload == null ? null : payload.meshes.SingleOrDefault(m => m.path == targetPath && m.skinned == (target.Skin != null));
                    Target match;
                    if (payload != null)
                    {
                        if (item == null || item.name != target.renderer.name) throw new Exception("The avatar hierarchy differs from the exported file: " + target.renderer.name);
                        match = sources.SingleOrDefault(s => s.renderer.name == item.id);
                    }
                    else
                    {
                        var exact = sources.Where(s => AnimationUtility.CalculateTransformPath(s.renderer.transform, source.transform) == AnimationUtility.CalculateTransformPath(target.renderer.transform, root.transform)).ToArray();
                        var candidates = exact.Length == 1 ? exact : sources.Where(s => s.renderer.name == target.renderer.name).ToArray();
                        if (candidates.Length != 1) throw new Exception("Cannot uniquely match mesh " + target.renderer.name + ". Export a new .blend with this tool or preserve FBX hierarchy and unique names.");
                        match = candidates[0];
                    }
                    if (match == null || !used.Add(match) || (match.Skin != null) != (target.Skin != null)) throw new Exception("Missing or incompatible mesh: " + target.renderer.name);
                    var replacement = BuildMesh(root, source, target, match, payload, item, rig);
                    replacements.Add(replacement);
                    replacement.materials = MapMaterials(root, target, match, payload, item);
                }
                // Remap SDK eyelids by name; deleted keys become unassigned (-1).
                var eyeChanges = PrepareEyelids(root, replacements);
                avatarChanges = PrepareHumanoidAvatars(root, rig);
                string data = Directory.GetParent(Directory.GetParent(Directory.GetParent(Absolute(incoming)).FullName).FullName).FullName;
                string backup = AssetPath(Path.Combine(data, "Backups", Stamp()));
                string meshes = AssetPath(Path.Combine(data, "Meshes", Stamp()));
                Directory.CreateDirectory(Absolute(backup)); Directory.CreateDirectory(Absolute(meshes)); AssetDatabase.Refresh();
                Backup(root, backup);
                foreach (var replacement in replacements)
                    AssetDatabase.CreateAsset(replacement.mesh, meshes + "/" + SafeName(replacement.target.renderer.name) + "_" + replacements.IndexOf(replacement) + ".asset");
                foreach (var change in avatarChanges) AssetDatabase.CreateAsset(change.avatar, meshes + "/Humanoid_" + avatarChanges.IndexOf(change) + ".asset");
                Undo.IncrementCurrentGroup(); int undo = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Nyades Blender mesh and rig import");
                foreach (var change in avatarChanges)
                {
                    Undo.RecordObject(change.animator, "Update Humanoid Avatar"); change.animator.avatar = change.avatar;
                    EditorUtility.SetDirty(change.animator); PrefabUtility.RecordPrefabInstancePropertyModifications(change.animator);
                }
                // Assigning a Humanoid Avatar may reset bone transforms to the Animator's cached pose.
                // Apply the imported rest pose afterwards so its positions and rotations survive.
                var importedBones = rig == null ? null : rig.Apply(root, true);
                foreach (var replacement in replacements)
                {
                    var target = replacement.target; Undo.RecordObject(target.renderer, "Import avatar mesh");
                    if (target.filter != null) Undo.RecordObject(target.filter, "Import avatar mesh");
                    target.Mesh = replacement.mesh;
                    target.renderer.sharedMaterials = replacement.materials;
                    if (target.Skin != null)
                    {
                        target.Skin.bones = importedBones == null ? replacement.bones : replacement.boneIds.Select(id => importedBones[id]).ToArray();
                        // localBounds uses renderer-local coordinates; retain a conservative existing bound.
                        var bounds = target.Skin.localBounds; bounds.Encapsulate(replacement.mesh.bounds); target.Skin.localBounds = bounds;
                        for (int i = 0; i < replacement.mesh.blendShapeCount; i++)
                        { float value; target.Skin.SetBlendShapeWeight(i, replacement.shapeValues.TryGetValue(replacement.mesh.GetBlendShapeName(i), out value) ? value : 0); }
                    }
                    EditorUtility.SetDirty(target.renderer); if (target.filter != null) EditorUtility.SetDirty(target.filter);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(target.renderer);
                    if (target.filter != null) PrefabUtility.RecordPrefabInstancePropertyModifications(target.filter);
                }
                foreach (var change in eyeChanges) change();
                Undo.CollapseUndoOperations(undo); EditorSceneManager.MarkSceneDirty(root.scene); AssetDatabase.SaveAssets();
                return "Imported " + replacements.Count + " meshes" + (rig == null ? "" : " and " + rig.changes.Count + " bones (" + rig.changes.Count(c => c.target == null) + " new)") + ". Save the Unity scene.\nBackup: " + backup + "/Before_Import.prefab";
            }
            finally
            {
                Object.DestroyImmediate(source);
                foreach (var r in replacements) if (r.mesh != null && !AssetDatabase.Contains(r.mesh)) Object.DestroyImmediate(r.mesh);
                foreach (var change in avatarChanges) if (change.avatar != null && !AssetDatabase.Contains(change.avatar)) Object.DestroyImmediate(change.avatar);
            }
        }

        static Material[] MapMaterials(GameObject root, Target target, Target from, Payload payload, MeshData item)
        {
            var existing = target.renderer.sharedMaterials;
            var incoming = from.renderer.sharedMaterials;
            var targets = Targets(root);
            var named = targets.SelectMany(t => t.renderer.sharedMaterials).Where(m => m != null).Distinct().GroupBy(m => m.name).ToDictionary(g => g.Key, g => g.ToArray());
            var result = new Material[from.Mesh.subMeshCount];
            if (item?.editedMaterials != null && item.editedMaterials.Length == 0) return result;
            for (int slot = 0; slot < result.Length; slot++)
            {
                var material = slot < incoming.Length ? incoming[slot] : null;
                string name = material == null ? "" : material.name;
                var edited = item?.editedMaterials?.FirstOrDefault(m => m.name == name);
                if (edited != null && edited.empty) { result[slot] = null; continue; }
                string originalMesh = edited?.originalMesh;
                int originalSlot = edited?.originalSlot ?? -1;
                if (edited == null)
                {
                    // Existing .blend files already encode provenance in the material name.
                    var identity = Regex.Match(name, @"^(NRTM_\d+)_Slot_(\d+)_");
                    if (identity.Success && int.TryParse(identity.Groups[2].Value, out originalSlot)) originalMesh = identity.Groups[1].Value;
                }
                if (!string.IsNullOrEmpty(originalMesh) && originalSlot >= 0)
                {
                    var description = payload?.meshes?.FirstOrDefault(m => m.id == originalMesh);
                    var owner = description == null ? null : targets.FirstOrDefault(t => HierarchyPath(t.renderer.transform, root.transform) == description.path && t.renderer.name == description.name);
                    var materials = owner?.renderer.sharedMaterials;
                    if (materials != null && originalSlot < materials.Length) { result[slot] = materials[originalSlot]; continue; }
                }
                if (named.TryGetValue(name, out var candidates) && candidates.Length == 1) result[slot] = candidates[0];
                else if (material != null) result[slot] = material;
                else result[slot] = slot < existing.Length ? existing[slot] : null;
            }
            return result;
        }

        static Replacement BuildMesh(GameObject root, GameObject source, Target target, Target from, Payload payload, MeshData item, RigPlan rig)
        {
            var targetMatrix = rig == null ? root.transform.worldToLocalMatrix * target.renderer.transform.localToWorldMatrix : rig.ModelMatrix(target.renderer.transform);
            var matrix = targetMatrix.inverse * source.transform.worldToLocalMatrix * from.renderer.transform.localToWorldMatrix;
            Transform[] mapped = new Transform[0];
            if (target.Skin != null)
            {
                mapped = from.Skin.bones.Select(b =>
                {
                    if (b == null) throw new Exception("Missing imported bone on " + target.renderer.name);
                    if (rig != null)
                    {
                        BoneChange change;
                        if (!rig.byId.TryGetValue(b.name, out change)) throw new Exception("Unknown imported bone: " + b.name);
                        return change.target;
                    }
                    Transform result;
                    if (payload != null)
                    {
                        var description = payload.rig.SingleOrDefault(d => d.id == b.name);
                        if (description == null) throw new Exception("Unknown imported bone: " + b.name);
                        result = Resolve(root.transform, description.path);
                        if (result == null || (description.path != "" && result.name != description.name)) throw new Exception("Bone hierarchy changed: " + description.name);
                    }
                    else
                    {
                        var candidates = root.GetComponentsInChildren<Transform>(true).Where(t => t.name == b.name).ToArray();
                        if (candidates.Length != 1) throw new Exception("Cannot uniquely map bone: " + b.name + ". Use a file exported by this tool.");
                        result = candidates[0];
                    }
                    if (Vector3.Distance(root.transform.InverseTransformPoint(result.position), source.transform.InverseTransformPoint(b.position)) > .002f)
                        throw new Exception("Armature rest position changed: " + result.name + ". Preserve the original rest pose.");
                    return result;
                }).ToArray();
            }
            Mesh mesh = Object.Instantiate(from.Mesh);
            try
            {
                mesh.name = target.renderer.name + " Round Trip";
                mesh.vertices = from.Mesh.vertices.Select(matrix.MultiplyPoint3x4).ToArray();
                if (from.Mesh.normals.Length > 0) mesh.normals = from.Mesh.normals.Select(n => matrix.inverse.transpose.MultiplyVector(n).normalized).ToArray();
                if (from.Mesh.tangents.Length > 0) mesh.tangents = from.Mesh.tangents.Select(t => { var v = matrix.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized; return new Vector4(v.x, v.y, v.z, t.w * (matrix.determinant < 0 ? -1 : 1)); }).ToArray();
                if (matrix.determinant < 0) for (int s = 0; s < mesh.subMeshCount; s++)
                { var indices = mesh.GetTriangles(s); for (int i = 0; i < indices.Length; i += 3) { int a = indices[i + 1]; indices[i + 1] = indices[i + 2]; indices[i + 2] = a; } mesh.SetTriangles(indices, s); }
                mesh.ClearBlendShapes();
                for (int i = 0; i < from.Mesh.blendShapeCount; i++)
                    for (int frame = 0; frame < from.Mesh.GetBlendShapeFrameCount(i); frame++)
                    {
                        var v = new Vector3[mesh.vertexCount]; var n = new Vector3[v.Length]; var t = new Vector3[v.Length];
                        from.Mesh.GetBlendShapeFrameVertices(i, frame, v, n, t);
                        for (int j = 0; j < v.Length; j++) { v[j] = matrix.MultiplyVector(v[j]); n[j] = matrix.inverse.transpose.MultiplyVector(n[j]); t[j] = matrix.MultiplyVector(t[j]); }
                        string name = from.Mesh.GetBlendShapeName(i);
                        var shape = item == null || item.shapes == null ? null : item.shapes.SingleOrDefault(s => s.name == name);
                        mesh.AddBlendShapeFrame(name, shape == null ? from.Mesh.GetBlendShapeFrameWeight(i, frame) : shape.weight, v, n, t);
                    }
                if (target.Skin != null) mesh.bindposes = rig == null ? mapped.Select(b => b.worldToLocalMatrix * target.renderer.transform.localToWorldMatrix).ToArray() : from.Skin.bones.Select(b => rig.byId[b.name].modelMatrix.inverse * targetMatrix).ToArray();
                mesh.RecalculateBounds();
                var values = new Dictionary<string, float>();
                if (target.Skin != null) for (int i = 0; i < target.Mesh.blendShapeCount; i++) values[target.Mesh.GetBlendShapeName(i)] = target.Skin.GetBlendShapeWeight(i);
                return new Replacement { target = target, mesh = mesh, bones = mapped, boneIds = from.Skin == null ? new string[0] : from.Skin.bones.Select(b => b.name).ToArray(), shapeValues = values };
            }
            catch { Object.DestroyImmediate(mesh); throw; }
        }

        static List<Action> PrepareEyelids(GameObject root, List<Replacement> replacements)
        {
            var result = new List<Action>();
            foreach (var component in root.GetComponentsInChildren<Component>(true).Where(c => c != null && c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
            {
                var serialized = new SerializedObject(component);
                var rendererProperty = serialized.FindProperty("customEyeLookSettings.eyelidsSkinnedMesh");
                var indices = serialized.FindProperty("customEyeLookSettings.eyelidsBlendshapes");
                if (rendererProperty == null || indices == null || !indices.isArray) continue;
                var replacement = replacements.FirstOrDefault(r => r.target.renderer == rendererProperty.objectReferenceValue);
                if (replacement == null) continue;
                var values = new int[indices.arraySize];
                for (int i = 0; i < values.Length; i++)
                {
                    int old = indices.GetArrayElementAtIndex(i).intValue;
                    if (old < 0 || old >= replacement.target.Mesh.blendShapeCount) { values[i] = -1; continue; }
                    string name = replacement.target.Mesh.GetBlendShapeName(old); values[i] = replacement.mesh.GetBlendShapeIndex(name);
                }
                result.Add(() =>
                {
                    Undo.RecordObject(component, "Remap eyelid shapes"); var current = new SerializedObject(component);
                    var property = current.FindProperty("customEyeLookSettings.eyelidsBlendshapes");
                    for (int i = 0; i < values.Length; i++) property.GetArrayElementAtIndex(i).intValue = values[i];
                    current.ApplyModifiedProperties(); PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                });
            }
            return result;
        }

        static void Backup(GameObject root, string folder)
        {
            var copy = Object.Instantiate(root); copy.name = root.name;
            try
            {
                int index = 0;
                foreach (var target in Targets(copy))
                {
                    var mesh = Object.Instantiate(target.Mesh);
                    AssetDatabase.CreateAsset(mesh, folder + "/Mesh_" + index++ + ".asset"); target.Mesh = mesh;
                }
                if (PrefabUtility.SaveAsPrefabAsset(copy, folder + "/Before_Import.prefab") == null) throw new Exception("Could not save the backup prefab; the avatar has not been changed.");
            }
            finally { Object.DestroyImmediate(copy); }
        }
    }
}
