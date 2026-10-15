using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nyades.BlenderRoundTrip
{
    public static class AvatarOptimization
    {
        const string GroupName = "Nyades Good Optimization";

        [MenuItem("Nyades/Avatar Optimization/Configure Roxy Good")]
        static void ConfigureSelected()
        {
            var root = Selection.activeGameObject;
            while (root != null && root.GetComponent<Animator>() == null) root = root.transform.parent?.gameObject;
            if (root == null || EditorUtility.IsPersistent(root)) throw new Exception("Select the avatar root in the scene.");
            Configure(root);
            EditorSceneManager.MarkSceneDirty(root.scene);
            Debug.Log("Nyades: Good optimization configured. It runs on the build copy; source meshes remain editable.", root);
        }

        // Deliberately uses only the merge component: no texture resizing,
        // shape freezing, controller optimization or physics changes.
        public static void Configure(GameObject root)
        {
            if (root.transform.Find(GroupName) != null) return;
            var type = FTAnimatorPreview.FindType("Anatawa12.AvatarOptimizer.MergeSkinnedMesh");
            if (type == null) throw new Exception("Install AAO Avatar Optimizer 1.9.20 first.");
            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToArray();
            if (skins.Length != 2 || skins.Count(r => r.name == "Body") != 1 || skins.Count(r => r.name == "Outfit") != 1)
                throw new Exception("This configuration expects the Roxy Body and Outfit renderers.");
            var body = skins.Single(r => r.name == "Body");
            var outfit = skins.Single(r => r.name == "Outfit");
            if (outfit.sharedMesh.blendShapeCount != 0 || body.enabled != outfit.enabled || body.gameObject.activeInHierarchy != outfit.gameObject.activeInHierarchy)
                throw new Exception("Renderer enablement or Outfit shape keys changed. Review the merge before configuring it.");

            Undo.IncrementCurrentGroup(); int undo = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Nyades configure Good optimization");
            var group = new GameObject(GroupName); Undo.RegisterCreatedObjectUndo(group, "Create optimization group");
            group.transform.SetParent(root.transform, false);
            var child = new GameObject("Body"); child.transform.SetParent(group.transform, false);
            child.transform.SetPositionAndRotation(body.transform.position, body.transform.rotation);
            var worldScale = body.transform.lossyScale; var parentScale = group.transform.lossyScale;
            child.transform.localScale = new Vector3(worldScale.x / parentScale.x, worldScale.y / parentScale.y, worldScale.z / parentScale.z);
            var merged = child.AddComponent<SkinnedMeshRenderer>();
            EditorUtility.CopySerialized(body, merged);
            merged.sharedMesh = null; merged.sharedMaterials = new Material[0]; merged.bones = new Transform[0];
            // Let AAO combine source bounds in the avatar root's coordinates.
            merged.rootBone = root.transform; merged.localBounds = default;
            var component = child.AddComponent(type);
            type.GetMethod("Initialize").Invoke(component, new object[] { 2 });
            type.GetProperty("MergeBlendShapes").SetValue(component, true);
            var sources = type.GetProperty("SourceSkinnedMeshRenderers").GetValue(component);
            var add = sources.GetType().GetMethod("Add");
            add.Invoke(sources, new object[] { body }); add.Invoke(sources, new object[] { outfit });
            Undo.CollapseUndoOperations(undo);
        }
    }
}
