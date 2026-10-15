using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using UnityEditor;
using UnityEngine;

namespace Nyades.BlenderRoundTrip
{
    public class FTLivePreview : EditorWindow
    {
        [SerializeField] GameObject avatar;
        [SerializeField] RuntimeAnimatorController fxOverride;
        [SerializeField] bool streamCurrent, buildNDMF = true;
        [SerializeField] int oscPort = 9000, blenderPort = 9003, fps = 60;
        [SerializeField] bool showUnityPreview = true;
        [SerializeField] int vrcftNotifyPort = 9001;
        [SerializeField] string oscProfile = "";
        [SerializeField] string manifestPath = "", parameterFilter = "FT";
        string status = "Start Blender's Live FT Preview, then select the Unity avatar.", error = "";
        FTAnimatorPreview preview;
        FTAvatarView avatarView;
        UdpClient input, output;
        GameObject source;
        bool running;
        double lastTick, lastUI;
        int sequence, packetsIn, packetsOut;
        string session;
        Vector2 scroll, windowScroll;
        readonly HashSet<string> unknownParameters = new HashSet<string>();
        readonly List<StreamMesh> meshes = new List<StreamMesh>();
        sealed class StreamMesh
        {
            public SkinnedMeshRenderer renderer; public string id, path, name;
            public Mesh cachedMesh; public float[] fullWeights; public Frame[] frames;
            public void Prepare(string session)
            {
                var mesh = renderer.sharedMesh;
                if (cachedMesh == mesh && fullWeights != null && fullWeights.Length == mesh.blendShapeCount) return;
                cachedMesh = mesh; fullWeights = new float[mesh.blendShapeCount];
                frames = new Frame[(mesh.blendShapeCount + 63) / 64];
                for (int first = 0; first < mesh.blendShapeCount; first += 64)
                {
                    var shapes = new Shape[Math.Min(64, mesh.blendShapeCount - first)];
                    for (int offset = 0; offset < shapes.Length; offset++)
                    {
                        int i = first + offset; float full = 0;
                        for (int j = 0; j < mesh.GetBlendShapeFrameCount(i); j++) full = Mathf.Max(full, mesh.GetBlendShapeFrameWeight(i, j));
                        fullWeights[i] = full > 0 ? full : 100;
                        shapes[offset] = new Shape { name = mesh.GetBlendShapeName(i) };
                    }
                    frames[first / 64] = new Frame { session = session, id = id, path = path, name = name, shapes = shapes };
                }
            }
        }
        [Serializable] public class Shape { public string name; public float value; }
        [Serializable] public class Frame
        { public int version = 1; public string session; public int sequence; public string id, path, name; public Shape[] shapes; }
        string Pref => "Nyades.FTLivePreview." + Application.dataPath + ".";
        public static FTLivePreview Open(GameObject root = null, string manifest = null)
        {
            var window = GetWindow<FTLivePreview>("FT Live Preview");
            if (root != null) window.avatar = root;
            if (manifest != null) window.manifestPath = manifest;
            return window;
        }
        [MenuItem("Nyades/FT Live Preview")]
        static void Menu() => Open();
        void OnEnable()
        {
            oscPort = EditorPrefs.GetInt(Pref + "OSC", oscPort); blenderPort = EditorPrefs.GetInt(Pref + "Blender", blenderPort);
            EditorApplication.update += Tick; EditorApplication.playModeStateChanged += PlayState; AssemblyReloadEvents.beforeAssemblyReload += Stop;
        }
        void OnDisable()
        {
            Stop(); EditorPrefs.SetInt(Pref + "OSC", oscPort); EditorPrefs.SetInt(Pref + "Blender", blenderPort);
            EditorApplication.update -= Tick; EditorApplication.playModeStateChanged -= PlayState; AssemblyReloadEvents.beforeAssemblyReload -= Stop;
        }
        void PlayState(PlayModeStateChange _) => Stop();
        void OnGUI()
        {
            windowScroll = EditorGUILayout.BeginScrollView(windowScroll);
            EditorGUILayout.LabelField("Live FT: Unity + Blender", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(running))
            {
                avatar = (GameObject)EditorGUILayout.ObjectField("Avatar root", avatar, typeof(GameObject), true);
                if (GUILayout.Button("Use selected avatar")) avatar = Selection.activeGameObject;
                streamCurrent = EditorGUILayout.ToggleLeft(new GUIContent("Stream current avatar (Av3Emulator / Gesture Manager)", "Read the selected avatar's actual weights. The emulator receives OSC; this tool does not bind its input port."), streamCurrent);
                if (!streamCurrent)
                {
                    fxOverride = (RuntimeAnimatorController)EditorGUILayout.ObjectField("FX override (optional)", fxOverride, typeof(RuntimeAnimatorController), false);
                    buildNDMF = EditorGUILayout.ToggleLeft(new GUIContent("Build Modular Avatar / NDMF preview copy", "Compile merged FX controllers on a temporary copy. The source avatar is retained."), buildNDMF);
                    oscPort = EditorGUILayout.IntField("VRCFT OSC input port", oscPort);
                }
                blenderPort = EditorGUILayout.IntField("Blender OSC port", blenderPort);
                fps = EditorGUILayout.IntSlider("Updates per second", fps, 10, 60);
                EditorGUILayout.BeginHorizontal(); manifestPath = EditorGUILayout.TextField("Round Trip JSON", manifestPath);
                if (GUILayout.Button("Browse", GUILayout.Width(65))) { var path = EditorUtility.OpenFilePanel("Round Trip correspondence JSON", "", "json"); if (path != "") manifestPath = path; }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.HelpBox(streamCurrent
                ? "Run Av3Emulator or Gesture Manager, select its local runtime avatar, and enable OSC there. This window forwards the resulting blendshape weights. It also streams manually adjusted weights in Edit Mode."
                : "Evaluate the FX Animator in Edit Mode on an isolated copy. Send OSC /avatar/parameters/<name> to localhost input port. Use emulator streaming when you need the full VRChat runtime, IK or avatar dynamics.", MessageType.Info);
            if (GUILayout.Button(running ? "Stop Live FT Preview" : "Start Live FT Preview"))
            {
                if (running) Stop(); else try { StartPreview(); } catch (Exception e) { Stop(); error = e.ToString(); status = e.Message; }
            }
            if (preview != null)
            {
                showUnityPreview = EditorGUILayout.ToggleLeft("Show Unity avatar preview", showUnityPreview);
                if (showUnityPreview && avatarView != null)
                {
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Frame Face")) avatarView.Frame(source, true);
                    if (GUILayout.Button("Frame Avatar")) avatarView.Frame(source, false);
                    EditorGUILayout.EndHorizontal();
                    var rect = GUILayoutUtility.GetRect(200, 300, GUILayout.ExpandWidth(true));
                    avatarView.Draw(rect);
                    EditorGUILayout.LabelField("Drag: rotate | Wheel: zoom | Shift-drag / middle mouse: pan", EditorStyles.miniLabel);
                }
                if (GUILayout.Button("Enable Eye / Lip Tracking")) preview.EnableTracking();
                parameterFilter = EditorGUILayout.TextField("Parameter filter", parameterFilter);
                scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(100), GUILayout.MaxHeight(300));
                foreach (var entry in preview.Parameters.OrderBy(p => p.Key).Where(p => string.IsNullOrEmpty(parameterFilter) || p.Key.IndexOf(parameterFilter, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    float value = preview.GetParameter(entry.Key); EditorGUI.BeginChangeCheck();
                    if (entry.Value == AnimatorControllerParameterType.Bool || entry.Value == AnimatorControllerParameterType.Trigger) value = EditorGUILayout.Toggle(entry.Key, value != 0) ? 1 : 0;
                    else if (entry.Value == AnimatorControllerParameterType.Int) value = EditorGUILayout.IntField(entry.Key, (int)value);
                    else value = EditorGUILayout.FloatField(entry.Key, value);
                    if (EditorGUI.EndChangeCheck()) preview.SetParameter(entry.Key, value);
                }
                EditorGUILayout.EndScrollView();
                var profiles = OSCProfiles();
                if (profiles.Length > 0)
                {
                    int index = Array.IndexOf(profiles, oscProfile); if (index < 0) index = 0;
                    index = EditorGUILayout.Popup("VRChat OSC profile", index, profiles.Select(Path.GetFileName).ToArray()); oscProfile = profiles[index];
                }
                EditorGUILayout.BeginHorizontal(); oscProfile = EditorGUILayout.TextField("OSC user folder", oscProfile);
                if (GUILayout.Button("Browse", GUILayout.Width(65))) { var path = EditorUtility.OpenFolderPanel("VRChat OSC usr_... folder", OSCRoot(), ""); if (path != "") oscProfile = path; }
                EditorGUILayout.EndHorizontal();
                vrcftNotifyPort = EditorGUILayout.IntField("VRCFT notification port", vrcftNotifyPort);
                using (new EditorGUI.DisabledScope(!Directory.Exists(oscProfile)))
                    if (GUILayout.Button("Prepare / Notify VRCFT (legacy OSC)"))
                        try { PrepareVRCFT(oscProfile, vrcftNotifyPort); } catch (Exception e) { error = e.ToString(); status = e.Message; }
                if (GUILayout.Button("Export VRCFT OSC parameter JSON")) ExportConfig();
            }
            EditorGUILayout.HelpBox(status, string.IsNullOrEmpty(error) ? MessageType.Info : MessageType.Error);
            if (unknownParameters.Count > 0) EditorGUILayout.LabelField("Unknown OSC parameters: " + string.Join(", ", unknownParameters.Take(5)), EditorStyles.wordWrappedMiniLabel);
            if (!string.IsNullOrEmpty(error) && GUILayout.Button("Copy Error")) EditorGUIUtility.systemCopyBuffer = error;
            PackageUpdates.Draw();
            EditorGUILayout.EndScrollView();
        }
        static string HierarchyPath(Transform transform, Transform root)
        {
            var parts = new List<int>();
            while (transform != root && transform != null) { parts.Add(transform.GetSiblingIndex()); transform = transform.parent; }
            parts.Reverse(); return string.Join("/", parts);
        }
        void StartPreview()
        {
            if (avatar == null || EditorUtility.IsPersistent(avatar) || !avatar.scene.IsValid()) throw new Exception("Select an avatar in a scene.");
            if (blenderPort < 1024 || blenderPort > 65535 || (!streamCurrent && (oscPort < 1024 || oscPort > 65535 || oscPort == blenderPort))) throw new Exception("Choose distinct OSC input/output ports from 1024 to 65535.");
            error = ""; unknownParameters.Clear(); packetsIn = packetsOut = sequence = 0; session = Guid.NewGuid().ToString("N");
            if (streamCurrent) source = avatar;
            else
            {
                avatarView = new FTAvatarView();
                preview = new FTAnimatorPreview(avatar, fxOverride, buildNDMF, avatarView.Scene);
                preview.EnableTracking(); source = preview.Root;
                avatarView.Frame(source, true);
            }
            BlenderRoundTrip.Payload manifest = null;
            if (!string.IsNullOrWhiteSpace(manifestPath))
            {
                if (!File.Exists(manifestPath)) throw new Exception("Round Trip JSON was not found. Choose its path or clear the field to match unique mesh names.");
                manifest = JsonUtility.FromJson<BlenderRoundTrip.Payload>(File.ReadAllText(manifestPath));
                if (manifest?.meshes == null) throw new Exception("Choose the Round Trip correspondence JSON, not an OSC parameter config.");
            }
            meshes.Clear();
            foreach (var renderer in source.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0))
            {
                string path = HierarchyPath(renderer.transform, source.transform);
                var item = manifest?.meshes.FirstOrDefault(m => m.path == path && m.name == renderer.name);
                if (item == null && manifest != null)
                {
                    var candidates = manifest.meshes.Where(m => m.name == renderer.name).ToArray(); if (candidates.Length == 1) item = candidates[0];
                }
                meshes.Add(new StreamMesh { renderer = renderer, id = item?.id ?? "", path = path, name = renderer.name });
            }
            if (meshes.Count == 0) throw new Exception("The selected avatar has no skinned meshes with blendshapes.");
            output = new UdpClient(); output.Connect(IPAddress.Loopback, blenderPort);
            if (!streamCurrent)
            {
                input = new UdpClient(new IPEndPoint(IPAddress.Loopback, oscPort)); input.Client.Blocking = false;
            }
            lastTick = EditorApplication.timeSinceStartup; running = true;
            status = "Started; Blender output: 127.0.0.1:" + blenderPort + (streamCurrent ? "; reading the selected avatar" : "; VRCFT input: 127.0.0.1:" + oscPort);
        }
        public static float NormalizedWeight(SkinnedMeshRenderer renderer, int index)
        {
            var mesh = renderer.sharedMesh; float full = 0;
            for (int i = 0; i < mesh.GetBlendShapeFrameCount(index); i++) full = Mathf.Max(full, mesh.GetBlendShapeFrameWeight(index, i));
            if (full <= 0) full = 100;
            return renderer.GetBlendShapeWeight(index) / full;
        }
        void Tick()
        {
            if (!running) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - lastTick < 1.0 / fps) return;
            try
            {
                if (source == null) throw new Exception("The preview source was removed. Select the avatar and restart.");
                if (input != null)
                    for (int count = 0; count < 256 && input.Available > 0; count++)
                    {
                        IPEndPoint endpoint = null; byte[] bytes = input.Receive(ref endpoint);
                        try
                        {
                            foreach (var message in FTOsc.Decode(bytes))
                            {
                                const string prefix = "/avatar/parameters/";
                                if (!message.address.StartsWith(prefix, StringComparison.Ordinal) || message.arguments.Length != 1 || !(message.arguments[0] is IConvertible value)) continue;
                                string name = message.address.Substring(prefix.Length); float number = Convert.ToSingle(value);
                                if (!preview.SetParameter(name, number) && unknownParameters.Count < 100) unknownParameters.Add(name);
                                packetsIn++;
                            }
                        }
                        catch (FormatException) { }
                    }
                preview?.Tick(Mathf.Clamp((float)(now - lastTick), 0, .1f));
                lastTick = now;
                if (sequence == int.MaxValue) { session = Guid.NewGuid().ToString("N"); sequence = 0; } else sequence++;
                foreach (var entry in meshes)
                {
                    if (entry.renderer == null || entry.renderer.sharedMesh == null) continue;
                    var mesh = entry.renderer.sharedMesh;
                    entry.Prepare(session);
                    for (int batch = 0; batch < entry.frames.Length; batch++)
                    {
                        var frame = entry.frames[batch]; frame.sequence = sequence; frame.session = session;
                        for (int j = 0; j < frame.shapes.Length; j++)
                        { int i = batch * 64 + j; frame.shapes[j].value = entry.renderer.GetBlendShapeWeight(i) / entry.fullWeights[i]; }
                        var bytes = FTOsc.Encode("/nyades/ft/frame", JsonUtility.ToJson(frame)); output.Send(bytes, bytes.Length); packetsOut++;
                    }
                }
                if (now - lastUI >= .2)
                {
                    status = (streamCurrent ? "Streaming avatar" : "Edit Mode FX preview") + "; OSC values: " + packetsIn + "; Blender packets: " + packetsOut + "; meshes: " + meshes.Count;
                    lastUI = now;
                    if (avatarView == null || !showUnityPreview) Repaint();
                }
                if (avatarView != null && showUnityPreview) Repaint();
            }
            catch (Exception e) { Stop(); error = e.ToString(); status = e.Message; }
        }
        public void Stop()
        {
            running = false; input?.Dispose(); output?.Dispose(); input = output = null;
            preview?.Dispose(); preview = null; source = null; meshes.Clear();
            avatarView?.Dispose(); avatarView = null;
            status = "Stopped. Blender restores its original sliders after one second or when stopped there.";
        }
        [Serializable] sealed class Config { public string id, name; public ConfigParameter[] parameters; }
        [Serializable] sealed class ConfigParameter { public string name; public ConfigAddress input; }
        [Serializable] sealed class ConfigAddress { public string address, type; }
        static string OSCRoot() => Path.Combine(Directory.GetParent(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)).FullName, "LocalLow/VRChat/VRChat/OSC");
        static string[] OSCProfiles()
        {
            string root = OSCRoot();
            return Directory.Exists(root) ? Directory.GetDirectories(root, "usr_*").OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray() : new string[0];
        }
        Config MakeConfig(string id)
        {
            // Expression Bool bits may intentionally feed float direct-blend inputs.
            // VRCFT must see the expression type, not the internal Animator type.
            var types = preview.OSCInputTypes.Count > 0 ? preview.OSCInputTypes : preview.Parameters.ToDictionary(p => p.Key, p => p.Value == AnimatorControllerParameterType.Bool || p.Value == AnimatorControllerParameterType.Trigger ? "Bool" : p.Value == AnimatorControllerParameterType.Int ? "Int" : "Float");
            return new Config { id = id, name = avatar.name + " FT Preview", parameters = types.Select(p => new ConfigParameter { name = p.Key, input = new ConfigAddress { address = "/avatar/parameters/" + p.Key, type = p.Value } }).ToArray() };
        }
        public void PrepareVRCFT(string userFolder, int notificationPort = 9001)
        {
            if (preview == null || !running) throw new Exception("Start Edit Mode FX preview first.");
            if (!Directory.Exists(userFolder) || !Path.GetFileName(Path.GetFullPath(userFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).StartsWith("usr_", StringComparison.Ordinal)) throw new Exception("Choose the VRChat OSC usr_... user folder.");
            if (notificationPort < 1024 || notificationPort > 65535) throw new Exception("Choose a valid VRCFT notification port.");
            string id = "avtr_NyadesFTPreview_" + Hash128.Compute(Application.dataPath + GlobalObjectId.GetGlobalObjectIdSlow(avatar).ToString());
            string directory = Path.Combine(Path.GetFullPath(userFolder), "Avatars"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, id + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(MakeConfig(id), true));
            using (var notification = new UdpClient())
            {
                var bytes = FTOsc.Encode("/avatar/change", id); notification.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, notificationPort));
            }
            error = ""; status = "Prepared OSC config and notified VRCFT on " + notificationPort + ". Input port: " + oscPort + ".\n" + path;
        }
        void ExportConfig()
        {
            string path = EditorUtility.SaveFilePanel("Export OSC avatar parameter config", Path.GetFullPath("Assets/Nyades/BlenderRoundTripData"), "avtr_NyadesFTPreview", "json");
            if (string.IsNullOrEmpty(path)) return;
            var config = MakeConfig("avtr_NyadesFTPreview");
            File.WriteAllText(path, JsonUtility.ToJson(config, true)); status = "Exported OSC parameter config: " + path;
        }
    }
}
