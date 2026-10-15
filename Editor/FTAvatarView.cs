using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nyades.BlenderRoundTrip
{
    // This camera renders the very same isolated avatar evaluated by the OSC bridge.
    public sealed class FTAvatarView : IDisposable
    {
        readonly PreviewRenderUtility utility = new PreviewRenderUtility();
        Vector3 pivot;
        Quaternion orientation = Quaternion.identity;
        float distance = 1, radius = .3f;
        Vector2 orbit;
        public Scene Scene => utility.camera.scene;
        public Camera Camera => utility.camera;

        public FTAvatarView()
        {
            utility.cameraFieldOfView = 30;
            utility.camera.clearFlags = CameraClearFlags.SolidColor;
            utility.camera.backgroundColor = new Color(.12f, .13f, .15f);
            utility.camera.cullingMask = ~0;
            utility.ambientColor = new Color(.6f, .6f, .6f);
            utility.lights[0].intensity = 1.2f;
            utility.lights[1].intensity = .6f;
        }

        public void Frame(GameObject root, bool face)
        {
            var renderers = root.GetComponentsInChildren<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
            var bounds = renderers.Length > 0 ? renderers[0].bounds : new Bounds(root.transform.position, Vector3.one);
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            pivot = bounds.center; radius = Mathf.Max(.01f, bounds.extents.magnitude);
            if (face)
            {
                var animator = root.GetComponent<Animator>();
                var head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
                if (head == null) head = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.Equals("Head", StringComparison.OrdinalIgnoreCase) || t.name.EndsWith(":Head", StringComparison.OrdinalIgnoreCase));
                if (head != null)
                {
                    radius = Mathf.Max(.01f, bounds.size.y * .09f);
                    pivot = head.position + root.transform.up * radius * .3f;
                }
                else
                {
                    var mesh = root.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r => r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0).OrderByDescending(r => r.sharedMesh.blendShapeCount).FirstOrDefault();
                    if (mesh != null) { pivot = mesh.bounds.center; radius = Mathf.Max(.01f, mesh.bounds.extents.magnitude); }
                }
            }
            orientation = root.transform.rotation; orbit = Vector2.zero;
            distance = radius / Mathf.Sin(utility.cameraFieldOfView * .5f * Mathf.Deg2Rad) * 1.1f;
            PositionCamera(1);
        }

        void PositionCamera(float aspect)
        {
            var rotation = orientation * Quaternion.Euler(orbit.y, orbit.x, 0);
            var camera = utility.camera;
            camera.transform.position = pivot + rotation * Vector3.forward * distance;
            camera.transform.rotation = Quaternion.LookRotation(pivot - camera.transform.position, rotation * Vector3.up);
            camera.nearClipPlane = Mathf.Max(.0001f, distance * .001f);
            camera.farClipPlane = Mathf.Max(10, distance + radius * 20);
            camera.aspect = aspect;
            var lights = utility.lights;
            lights[0].transform.rotation = camera.transform.rotation * Quaternion.Euler(25, -25, 0);
            lights[1].transform.rotation = camera.transform.rotation * Quaternion.Euler(-20, 150, 0);
        }

        public Texture Render(Rect rect)
        {
            PositionCamera(rect.width / Mathf.Max(1, rect.height));
            utility.BeginPreview(rect, GUIStyle.none);
            Texture texture = null;
            try { utility.Render(true, false); }
            finally { texture = utility.EndPreview(); }
            return texture;
        }

        public void Draw(Rect rect)
        {
            var e = Event.current;
            int control = GUIUtility.GetControlID(FocusType.Passive, rect);
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition) && (e.button == 0 || e.button == 1 || e.button == 2))
            { GUIUtility.hotControl = control; e.Use(); }
            if (e.type == EventType.MouseDrag && GUIUtility.hotControl == control)
            {
                if (e.button == 2 || e.shift)
                    pivot += (-Camera.transform.right * e.delta.x + Camera.transform.up * e.delta.y) * (distance * .001f);
                else { orbit.x += e.delta.x * .5f; orbit.y = Mathf.Clamp(orbit.y + e.delta.y * .5f, -85, 85); }
                e.Use();
            }
            if (e.type == EventType.MouseUp && GUIUtility.hotControl == control) { GUIUtility.hotControl = 0; e.Use(); }
            if (e.type == EventType.ScrollWheel && rect.Contains(e.mousePosition))
            { distance = Mathf.Clamp(distance * Mathf.Exp(e.delta.y * .08f), radius * .15f, radius * 100); e.Use(); }
            if (e.type == EventType.Repaint) GUI.DrawTexture(rect, Render(rect), ScaleMode.StretchToFill, false);
        }

        public void Dispose() => utility.Cleanup();
    }
}
