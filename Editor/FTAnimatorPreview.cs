using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Nyades.BlenderRoundTrip
{
    // A real Unity controller on an isolated preview avatar; no scene animation or Play Mode required.
    public sealed class FTAnimatorPreview : IDisposable
    {
        public GameObject Root { get; private set; }
        public Animator Animator { get; private set; }
        public RuntimeAnimatorController Controller { get; private set; }
        public readonly Dictionary<string, AnimatorControllerParameterType> Parameters = new Dictionary<string, AnimatorControllerParameterType>();
        public readonly Dictionary<string, string> OSCInputTypes = new Dictionary<string, string>();
        PlayableGraph graph;
        AnimatorControllerPlayable playable;
        AnimationLayerMixerPlayable mixer;
        Scene scene;
        bool ownsScene;
        readonly List<Action> unhooks = new List<Action>();
        readonly HashSet<Object> ownedAssets = new HashSet<Object>();
        readonly List<LayerFade> fades = new List<LayerFade>();
        readonly List<RestTransform> restTransforms = new List<RestTransform>();
        sealed class RestTransform
        {
            public Transform target; public Vector3 position, scale; public Quaternion rotation;
            public int animatePosition, animateScale; public bool animateRotation;
        }
        double time;
        sealed class LayerFade { public int layer; public float start, goal, duration; public double began; }
        static readonly BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        public static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
        static object Get(object obj, string name)
        {
            if (obj == null) return null;
            return obj.GetType().GetField(name, Members)?.GetValue(obj) ?? obj.GetType().GetProperty(name, Members)?.GetValue(obj);
        }
        static float Float(object obj, string name, float fallback = 0) => Get(obj, name) is IConvertible value ? Convert.ToSingle(value) : fallback;
        static bool Flag(object obj, string name) => Get(obj, name) is bool value && value;
        public static RuntimeAnimatorController FindFX(GameObject root)
        {
            foreach (var component in root.GetComponents<Component>().Where(c => c != null && c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor"))
            {
                var descriptor = new SerializedObject(component);
                var layers = descriptor.FindProperty("baseAnimationLayers");
                if (layers == null || !layers.isArray) continue;
                for (int i = 0; i < layers.arraySize; i++)
                {
                    var layer = layers.GetArrayElementAtIndex(i); var type = layer.FindPropertyRelative("type");
                    if (type == null || type.enumValueIndex < 0 || type.enumValueIndex >= type.enumNames.Length || type.enumNames[type.enumValueIndex] != "FX") continue;
                    return layer.FindPropertyRelative("animatorController")?.objectReferenceValue as RuntimeAnimatorController;
                }
            }
            return root.GetComponent<Animator>()?.runtimeAnimatorController;
        }
        public FTAnimatorPreview(GameObject avatar, RuntimeAnimatorController controller, bool buildNDMF, Scene previewScene = default)
        {
            if (avatar == null || EditorUtility.IsPersistent(avatar)) throw new Exception("Select an avatar from a scene.");
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new Exception("Edit Mode FX preview requires stopping Play Mode. Use Stream current avatar for Av3Emulator.");
            try
            {
                ownsScene = !previewScene.IsValid();
                scene = ownsScene ? EditorSceneManager.NewPreviewScene() : previewScene;
                Root = Object.Instantiate(avatar); Root.name = avatar.name;
                SceneManager.MoveGameObjectToScene(Root, scene);
                foreach (var transform in Root.GetComponentsInChildren<Transform>(true))
                    if (PrefabUtility.IsPartOfPrefabInstance(transform)) PrefabUtility.UnpackPrefabInstance(transform.gameObject, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                if (buildNDMF) BuildModularAvatar();
                Root.hideFlags = HideFlags.HideAndDontSave;
                foreach (var script in Root.GetComponentsInChildren<MonoBehaviour>(true)) if (script) script.enabled = false;
                foreach (var animator in Root.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                foreach (var renderer in Root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) renderer.updateWhenOffscreen = true;
                Root.SetActive(true);
                Animator = Root.GetComponent<Animator>() ?? Root.AddComponent<Animator>();
                Animator.enabled = true; Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                Controller = controller != null ? controller : FindFX(Root);
                if (Controller == null) throw new Exception("No FX controller found. Enable the NDMF build for Modular Avatar or choose an FX controller.");
                CaptureRestPose();
                Animator.runtimeAnimatorController = null; Animator.applyRootMotion = false;
                HookSDK();
                graph = PlayableGraph.Create("Nyades FT Edit Mode Preview"); graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                playable = AnimatorControllerPlayable.Create(graph, Controller);
                mixer = AnimationLayerMixerPlayable.Create(graph, 1); graph.Connect(playable, 0, mixer, 0); mixer.SetInputWeight(0, 1);
                var output = AnimationPlayableOutput.Create(graph, "FT", Animator); output.SetSourcePlayable(mixer);
                for (int i = 0; i < playable.GetParameterCount(); i++)
                {
                    var parameter = playable.GetParameter(i); Parameters[parameter.name] = parameter.type;
                    float value = parameter.type == AnimatorControllerParameterType.Bool ? (parameter.defaultBool ? 1 : 0) : parameter.type == AnimatorControllerParameterType.Int ? parameter.defaultInt : parameter.defaultFloat;
                    SetParameter(parameter.name, value);
                }
                // Expression defaults can differ from controller defaults, especially after MA merging.
                var descriptor = Root.GetComponents<Component>().FirstOrDefault(c => c && c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
                if (Get(Get(descriptor, "expressionParameters"), "parameters") is IEnumerable defaults)
                    foreach (var parameter in defaults)
                    {
                        string name = Get(parameter, "name") as string;
                        if (!SetParameter(name, Float(parameter, "defaultValue"))) continue;
                        string type = Get(parameter, "valueType")?.ToString();
                        if (type == "Bool" || type == "Int" || type == "Float") OSCInputTypes[name] = type;
                    }
                SetParameter("IsLocal", 1); SetParameter("IsOnFriendsList", 1); SetParameter("TrackingType", 1);
                graph.Play(); graph.Evaluate(0); RestoreUnanimatedPose();
            }
            catch { Dispose(); throw; }
        }
        void CaptureRestPose()
        {
            // A Humanoid playable can apply its reference pose to auxiliary bones
            // even when FX contains only component curves. FX-only preview must
            // leave those bones at the avatar's authored pose, just as Blender does.
            var entries = Root.GetComponentsInChildren<Transform>(true).Select(
                t => new RestTransform { target = t, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray();
            var paths = entries.ToLookup(e => AnimationUtility.CalculateTransformPath(e.target, Root.transform));
            bool humanoidMotion = false;
            foreach (var clip in Controller.animationClips.Distinct())
            {
                humanoidMotion |= clip.isHumanMotion;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(Transform)) continue;
                    string property = binding.propertyName;
                    int axes = property.EndsWith(".x", StringComparison.Ordinal) ? 1 : property.EndsWith(".y", StringComparison.Ordinal) ? 2 : property.EndsWith(".z", StringComparison.Ordinal) ? 4 : 7;
                    foreach (var entry in paths[binding.path])
                    {
                        if (property.StartsWith("m_LocalPosition", StringComparison.Ordinal)) entry.animatePosition |= axes;
                        else if (property.StartsWith("m_LocalScale", StringComparison.Ordinal)) entry.animateScale |= axes;
                        else if (property.StartsWith("m_LocalRotation", StringComparison.Ordinal) || property.StartsWith("localEulerAngles", StringComparison.Ordinal)) entry.animateRotation = true;
                    }
                }
            }
            // Preserve explicitly authored muscle animation when an FX override
            // really contains Humanoid clips, rather than freezing its skeleton.
            if (humanoidMotion && Animator.isHuman)
                foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (bone == HumanBodyBones.LastBone) continue;
                    var transform = Animator.GetBoneTransform(bone);
                    if (transform != null)
                        foreach (var entry in paths[AnimationUtility.CalculateTransformPath(transform, Root.transform)])
                        { entry.animatePosition = entry.animateScale = 7; entry.animateRotation = true; }
                }
            restTransforms.AddRange(entries);
        }
        void RestoreUnanimatedPose()
        {
            foreach (var entry in restTransforms)
            {
                if (entry.target == null) continue;
                var position = entry.target.localPosition; var scale = entry.target.localScale;
                for (int axis = 0; axis < 3; axis++)
                {
                    if ((entry.animatePosition & (1 << axis)) == 0) position[axis] = entry.position[axis];
                    if ((entry.animateScale & (1 << axis)) == 0) scale[axis] = entry.scale[axis];
                }
                if (entry.target.localPosition != position) entry.target.localPosition = position;
                if (!entry.animateRotation && entry.target.localRotation != entry.rotation) entry.target.localRotation = entry.rotation;
                if (entry.target.localScale != scale) entry.target.localScale = scale;
            }
        }
        void BuildModularAvatar()
        {
            var contextType = FindType("nadena.dev.ndmf.BuildContext");
            var processor = FindType("nadena.dev.ndmf.AvatarProcessor");
            var phase = FindType("nadena.dev.ndmf.BuildPhase");
            if (contextType == null || processor == null || phase == null) return;
            var descriptorType = FindType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (descriptorType == null || Root.GetComponent(descriptorType) == null) return;
            var existing = new HashSet<int>(Resources.FindObjectsOfTypeAll<Object>().Select(o => o.GetInstanceID()));
            try
            {
                // Select VRChat explicitly; an editor's ambient platform may be generic Unity.
                var platform = FindType("nadena.dev.ndmf.vrchat.VRChatPlatform")?.GetProperty("Instance", Members)?.GetValue(null);
                var platformType = FindType("nadena.dev.ndmf.platform.INDMFPlatformProvider");
                object context = platform != null && platformType != null
                    ? contextType.GetConstructor(new[] { typeof(GameObject), typeof(string), platformType, typeof(bool) })?.Invoke(new object[] { Root, null, platform, true })
                    : contextType.GetConstructor(new[] { typeof(GameObject), typeof(string), typeof(bool) })?.Invoke(new object[] { Root, null, true });
                if (context == null) throw new Exception("This NDMF version does not support preview builds.");
                object first = phase.GetField("First", Members)?.GetValue(null) ?? phase.GetProperty("First", Members)?.GetValue(null);
                // Include optimization so the visible clone and OSC output use the
                // same renderer/material/shape remapping as the uploaded avatar.
                object last = phase.GetField("Optimizing", Members)?.GetValue(null) ?? phase.GetProperty("Optimizing", Members)?.GetValue(null);
                var method = processor.GetMethods(Members).SingleOrDefault(m => m.Name == "ProcessAvatar" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == contextType);
                if (first == null || last == null || method == null) throw new Exception("This NDMF version does not expose the preview build phases.");
                method.Invoke(null, new[] { context, first, last });
                // Flush virtualized controllers without persisting generated assets.
                contextType.GetMethod("Finish", Members)?.Invoke(context, null);
                if (contextType.GetProperty("Successful")?.GetValue(context) is bool success && !success)
                {
                    var messages = new List<string>();
                    if (Get(Get(context, "ErrorReport"), "Errors") is IEnumerable errors)
                        foreach (var error in errors)
                        {
                            var detail = Get(error, "TheError");
                            if (detail != null && messages.Count < 10) messages.Add(detail.GetType().GetMethod("ToMessage", Members)?.Invoke(detail, null) as string ?? detail.GetType().Name);
                        }
                    throw new Exception("NDMF could not build the preview avatar.\n" + string.Join("\n", messages));
                }
            }
            catch (TargetInvocationException error) { throw new Exception("NDMF preview build failed: " + error.InnerException?.Message, error.InnerException); }
            finally
            {
                // Only own generated assets reachable from our copy. Other editor previews
                // can also create temporary objects during a build event.
                var pending = new Queue<Object>(Root.GetComponentsInChildren<Component>(true).Where(c => c != null).Cast<Object>());
                var visited = new HashSet<int>();
                while (pending.Count > 0)
                {
                    var obj = pending.Dequeue(); if (obj == null || !visited.Add(obj.GetInstanceID())) continue;
                    if (!(obj is Component) && (existing.Contains(obj.GetInstanceID()) || EditorUtility.IsPersistent(obj))) continue;
                    if (!(obj is Component) && !(obj is GameObject)) ownedAssets.Add(obj);
                    var serialized = new SerializedObject(obj); var iterator = serialized.GetIterator();
                    while (iterator.Next(true)) if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue != null) pending.Enqueue(iterator.objectReferenceValue);
                }
            }
        }
        public bool SetParameter(string name, float value)
        {
            if (name == null || !Parameters.TryGetValue(name, out var type) || float.IsNaN(value) || float.IsInfinity(value)) return false;
            switch (type)
            {
                case AnimatorControllerParameterType.Float: playable.SetFloat(name, value); break;
                case AnimatorControllerParameterType.Int: playable.SetInteger(name, Mathf.RoundToInt(value)); break;
                case AnimatorControllerParameterType.Bool: playable.SetBool(name, value != 0); break;
                case AnimatorControllerParameterType.Trigger: if (value != 0) playable.SetTrigger(name); else playable.ResetTrigger(name); break;
            }
            return true;
        }
        public float GetParameter(string name)
        {
            if (name == null || !Parameters.TryGetValue(name, out var type)) return 0;
            switch (type)
            {
                case AnimatorControllerParameterType.Float: return playable.GetFloat(name);
                case AnimatorControllerParameterType.Int: return playable.GetInteger(name);
                case AnimatorControllerParameterType.Bool: return playable.GetBool(name) ? 1 : 0;
                default: return 0;
            }
        }
        public void EnableTracking()
        {
            foreach (string name in Parameters.Keys.Where(n => n.EndsWith("EyeTrackingActive", StringComparison.Ordinal) || n.EndsWith("LipTrackingActive", StringComparison.Ordinal))) SetParameter(name, 1);
        }
        public void Tick(float delta)
        {
            time += delta;
            foreach (var fade in fades.ToArray())
            {
                float t = fade.duration <= 0 ? 1 : Mathf.Clamp01((float)(time - fade.began) / fade.duration);
                if (fade.layer < 0) mixer.SetInputWeight(0, Mathf.Lerp(fade.start, fade.goal, t));
                else playable.SetLayerWeight(fade.layer, Mathf.Lerp(fade.start, fade.goal, t));
                if (t >= 1) fades.Remove(fade);
            }
            graph.Evaluate(delta);
            RestoreUnanimatedPose();
        }
        void Subscribe(Type type, object target, string name, Action<object[]> callback)
        {
            if (type == null) return;
            var field = type.GetField(name, Members); var evt = type.GetEvent(name, Members);
            var delegateType = field?.FieldType ?? evt?.EventHandlerType;
            if (delegateType == null || !typeof(Delegate).IsAssignableFrom(delegateType)) return;
            var args = delegateType.GetMethod("Invoke").GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
            var call = Expression.Invoke(Expression.Constant(callback), Expression.NewArrayInit(typeof(object), args.Select(a => Expression.Convert(a, typeof(object)))));
            var handler = Expression.Lambda(delegateType, call, args).Compile();
            if (field != null)
            {
                field.SetValue(target, Delegate.Combine(field.GetValue(target) as Delegate, handler));
                unhooks.Add(() => field.SetValue(target, Delegate.Remove(field.GetValue(target) as Delegate, handler)));
            }
            else { evt.AddEventHandler(target, handler); unhooks.Add(() => evt.RemoveEventHandler(target, handler)); }
        }
        void HookSDK()
        {
            Subscribe(FindType("VRC.SDKBase.VRC_AvatarParameterDriver"), null, "OnApplySettings", args =>
            {
                if (args.Length == 2 && args[1] as Animator == Animator) ApplyDriver(args[0]);
            });
            foreach (string name in new[] { "VRC.SDKBase.VRC_AnimatorLayerControl", "VRC.SDKBase.VRC_PlayableLayerControl" })
                Subscribe(FindType(name), null, "Initialize", args =>
                {
                    if (args.Length != 1 || args[0] == null) return;
                    var behaviour = args[0];
                    Subscribe(behaviour.GetType(), behaviour, "ApplySettings", applied =>
                    {
                        if (applied.Length != 2 || applied[1] as Animator != Animator || !playable.IsValid()) return;
                        var control = applied[0]; string layer = (Get(control, "playable") ?? Get(control, "layer"))?.ToString();
                        if (layer != "FX") return;
                        int index = control.GetType().Name.Contains("PlayableLayer") ? -1 : (int)Float(control, "layer");
                        if (index >= playable.GetLayerCount()) return;
                        fades.RemoveAll(f => f.layer == index);
                        fades.Add(new LayerFade { layer = index, start = index < 0 ? mixer.GetInputWeight(0) : playable.GetLayerWeight(index), goal = Float(control, "goalWeight", 1), duration = Float(control, "blendDuration"), began = time });
                    });
                });
        }
        void ApplyDriver(object driver)
        {
            if (!(Get(driver, "parameters") is IEnumerable parameters) || !playable.IsValid()) return;
            foreach (var parameter in parameters)
            {
                string name = Get(parameter, "name") as string;
                if (name == null || !Parameters.TryGetValue(name, out var type)) continue;
                float value = Float(parameter, "value");
                switch (Get(parameter, "type")?.ToString())
                {
                    case "Set": break;
                    case "Add": value += GetParameter(name); break;
                    case "Copy":
                        value = GetParameter(Get(parameter, "source") as string);
                        if (Flag(parameter, "convertRange")) value = Mathf.Lerp(Float(parameter, "destMin"), Float(parameter, "destMax"), Mathf.InverseLerp(Float(parameter, "sourceMin"), Float(parameter, "sourceMax"), value));
                        break;
                    case "Random":
                        value = type == AnimatorControllerParameterType.Bool || type == AnimatorControllerParameterType.Trigger ? (UnityEngine.Random.value < Float(parameter, "chance") ? 1 : 0) : type == AnimatorControllerParameterType.Int ? UnityEngine.Random.Range((int)Float(parameter, "valueMin"), (int)Float(parameter, "valueMax") + 1) : UnityEngine.Random.Range(Float(parameter, "valueMin"), Float(parameter, "valueMax"));
                        break;
                    default: continue;
                }
                SetParameter(name, value);
            }
        }
        public void Dispose()
        {
            if (graph.IsValid()) graph.Destroy();
            foreach (var unhook in unhooks.ToArray()) unhook(); unhooks.Clear();
            if (Root != null) Object.DestroyImmediate(Root); Root = null;
            if (ownsScene && scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            foreach (var asset in ownedAssets) if (asset != null && !EditorUtility.IsPersistent(asset)) Object.DestroyImmediate(asset);
            ownedAssets.Clear();
            restTransforms.Clear();
        }
    }
}
