"""Background Blender transport for Nyades Blender Round Trip. No add-on required."""
import argparse
import json
import sys
import hashlib
import re
from array import array
from pathlib import Path

import bpy
from mathutils import Vector, Matrix, Quaternion

MANIFEST = "Nyades_RoundTrip_Manifest"
CONVERSION = Matrix(((-1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
BONE_AXES = Matrix.Diagonal((-1, 1, 1, 1))


def matrix_from(values):
    return Matrix([values[i:i + 4] for i in range(0, 16, 4)])


def matrix_values(value):
    return [component for row in value for component in row]


def convert(value):
    return Vector((-value[0], -value[2], value[1]))


def prepare_object_mode():
    # .blend files retain their editing mode. Commit saved mesh/armature edits before
    # reading their data, renaming bones, or invoking the FBX exporter in the background.
    if bpy.context.mode != 'OBJECT':
        if not bpy.ops.object.mode_set.poll():
            raise RuntimeError('Cannot leave the saved Blender editing mode. Save with an active mesh or armature and retry.')
        bpy.ops.object.mode_set(mode='OBJECT')
    bpy.context.view_layer.update()


def deselect_objects():
    # Data API selection works without the editor-area context required by select_all.
    for obj in bpy.context.view_layer.objects:
        obj.select_set(False)


def bake_shape_key_vertex_groups(obj):
    """Bake masked relative deltas into independent targets in the export process only."""
    keys = obj.data.shape_keys
    if not keys:
        return 0
    if not keys.use_relative:
        raise RuntimeError('Use relative shape keys: ' + obj.name)
    basis = keys.reference_key
    masked = [key for key in keys.key_blocks if key != basis and key.vertex_group]
    if not masked:
        return 0
    # Objects sharing a mesh can have different group names/index mappings.
    if obj.data.users > 1:
        obj.data = obj.data.copy()
        keys = obj.data.shape_keys
        basis = keys.reference_key
    count = len(obj.data.vertices)
    coordinates = {}
    relatives = {}
    masks = {}
    for key in keys.key_blocks:
        values = array('f', [0.0]) * (count * 3)
        key.data.foreach_get('co', values)
        coordinates[key.name] = values
        relatives[key.name] = (key.relative_key or basis).name
        masks[key.name] = key.vertex_group
    weights = {}
    for group_name in set(masks.values()) - {''}:
        group = obj.vertex_groups.get(group_name)
        # Blender treats a missing group as unmasked; an existing empty group has zero weights.
        if group is None:
            weights[group_name] = None
            continue
        values = array('f', [0.0]) * count
        for vertex in obj.data.vertices:
            for membership in vertex.groups:
                if membership.group == group.index:
                    values[vertex.index] = membership.weight
                    break
        weights[group_name] = values
    base_coordinates = coordinates[basis.name]
    # Snapshot every original target first: changing one masked key must not alter the
    # delta of another key that uses it as its Relative Key, even if that key is unmasked.
    for key in keys.key_blocks:
        if key == basis:
            continue
        target = coordinates[key.name]
        reference = coordinates[relatives[key.name]]
        mask = weights.get(masks[key.name])
        baked = array('f', (base_coordinates[i] + (target[i] - reference[i]) *
                            (mask[i // 3] if mask is not None else 1.0)
                            for i in range(count * 3)))
        key.data.foreach_set('co', baked)
        key.relative_key = basis
        key.vertex_group = ''
    obj.data.update()
    print('Baked shape-key vertex groups:', obj.name, len(masked), flush=True)
    return len(masked)


def create_blend(payload, output):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    armature = None
    if payload["rig"]:
        armature = bpy.data.objects.new("Nyades_Rig", bpy.data.armatures.new("Avatar Rig"))
        bpy.context.collection.objects.link(armature)
        bpy.context.view_layer.objects.active = armature
        armature.select_set(True)
        bpy.ops.object.mode_set(mode="EDIT")
        for entry in payload["rig"]:
            bone = armature.data.edit_bones.new(entry["id"])
            bone.head = convert(entry["position"])
            children = [child for child in payload["rig"] if child.get("parent") == entry["id"]]
            tail = next((convert(child["position"]) for child in children
                         if (convert(child["position"]) - bone.head).length > 0.00001), None)
            length = (tail - bone.head).length if tail is not None else .025
            if entry.get("matrix"):
                if entry.get("rotation"):
                    q = entry["rotation"]
                    unity_pose = Matrix.LocRotScale(Vector(entry["position"]), Quaternion((q[3], *q[:3])), Vector((1, 1, 1)))
                else:
                    unity_pose = matrix_from(entry["matrix"])
                converted = CONVERSION @ unity_pose @ BONE_AXES
                location, rotation, scale = converted.decompose()
                bone.matrix = Matrix.LocRotScale(location, rotation, Vector((1, 1, 1)))
                bone.length = max(length, .00001)
            else:
                bone.tail = tail if tail is not None else bone.head + Vector((0, 0, .025))
        for entry in payload["rig"]:
            if entry.get("parent"):
                armature.data.edit_bones[entry["id"]].parent = armature.data.edit_bones[entry["parent"]]
        bpy.ops.object.mode_set(mode="OBJECT")
        for entry in payload["rig"]:
            armature.data.bones[entry["id"]]["unity_name"] = entry["name"]
            armature.data.bones[entry["id"]]["nyades_id"] = entry["id"]
            armature.data.bones[entry["id"]]["nyades_rest_matrix"] = matrix_values(armature.data.bones[entry["id"]].matrix_local)
            armature.data.bones[entry["id"]]["nyades_rest_length"] = armature.data.bones[entry["id"]].length
            if entry.get("scale"):
                armature.data.bones[entry["id"]]["unity_scale"] = entry["scale"]
        armature.show_in_front = True

    created = []
    for item in payload["meshes"]:
        print("Creating mesh", item["name"], flush=True)
        mesh = bpy.data.meshes.new(item["name"])
        obj = bpy.data.objects.new(item["id"], mesh)
        bpy.context.collection.objects.link(obj)
        obj["unity_name"] = item["name"]
        vertices = [convert(v["values"]) for v in item["vertices"]]
        faces, slots = [], []
        for slot, wrapped in enumerate(item["triangles"]):
            indices = wrapped["values"]
            for i in range(0, len(indices), 3):
                faces.append((indices[i], indices[i + 2], indices[i + 1]))
                slots.append(slot)
        mesh.from_pydata(vertices, [], faces)
        mesh.update()
        for polygon, slot in zip(mesh.polygons, slots):
            polygon.material_index = slot
            polygon.use_smooth = True
        # Keep empty UV channels as placeholders so UV1 never silently becomes UV0.
        last_uv = max((i for i, channel in enumerate(item["uv"]) if channel["values"]), default=-1)
        for i in range(last_uv + 1):
            layer = mesh.uv_layers.new(name="UV" + str(i))
            values = item["uv"][i]["values"]
            if values:
                for loop in mesh.loops:
                    layer.data[loop.index].uv = values[loop.vertex_index]["values"]
        if item["colors"]:
            colors = mesh.color_attributes.new(name="Color", type="FLOAT_COLOR", domain="CORNER")
            for loop in mesh.loops:
                colors.data[loop.index].color = item["colors"][loop.vertex_index]["values"]
        if item["normals"]:
            mesh.normals_split_custom_set_from_vertices([convert(v["values"]).normalized() for v in item["normals"]])
        for slot, description in enumerate(item["materials"]):
            # Per-slot materials keep equal or empty material names from collapsing FBX slots.
            material = bpy.data.materials.new(item["id"] + "_Slot_" + str(slot) + "_" + description["name"])
            material['nyades_mesh_id'] = item['id']
            material['nyades_slot'] = slot
            material.use_nodes = True
            material.diffuse_color = tuple(description["color"])
            bsdf = material.node_tree.nodes.get("Principled BSDF")
            bsdf.inputs["Base Color"].default_value = tuple(description["color"])
            texture = description.get("texture")
            if texture and Path(texture).is_file():
                try:
                    image = bpy.data.images.load(texture, check_existing=True)
                    node = material.node_tree.nodes.new("ShaderNodeTexImage")
                    node.image = image
                    material.node_tree.links.new(node.outputs["Color"], bsdf.inputs["Base Color"])
                except RuntimeError:
                    print("Preview texture could not be loaded:", texture)
            mesh.materials.append(material)
        if item["skinned"]:
            groups = [obj.vertex_groups.new(name=name) for name in item["bones"]]
            batches = {}
            for index, weight in enumerate(item["weights"]):
                for bone, strength in zip(weight["indices"], weight["values"]):
                    if strength > 0:
                        batches.setdefault((bone, strength), []).append(index)
            for (bone, strength), indices in batches.items():
                groups[bone].add(indices, strength, "REPLACE")
            obj.modifiers.new("Avatar Rig", "ARMATURE").object = armature
        if item["shapes"]:
            obj.shape_key_add(name="Basis", from_mix=False)
            for shape in item["shapes"]:
                key = obj.shape_key_add(name=shape["name"], from_mix=False)
                key.data.foreach_set("co", [component for i, delta in enumerate(shape["delta"])
                                             for component in vertices[i] + convert(delta["values"])])
                # shape_key_add can initialize a newly created key at 1. Set every value explicitly.
                # Unity uses frame-weight units (usually 100); Blender uses 1 for the full target.
                value = float(shape.get("value", 0)) / float(shape["weight"])
                key.slider_min = min(0.0, value)
                key.slider_max = max(1.0, value)
                key.value = value
        created.append(obj)
    # Store only correspondence and frame weights, not a second copy of all geometry.
    manifest = {"version": payload["version"], "avatar": payload["avatar"], "rig": payload["rig"],
                "meshes": [{key: item[key] for key in ("id", "path", "name", "skinned")} |
                           {"shapes": [{"name": shape["name"], "weight": shape["weight"]}
                                       for shape in item["shapes"]]} for item in payload["meshes"]]}
    bpy.data.texts.new(MANIFEST).write(json.dumps(manifest, ensure_ascii=False))
    deselect_objects()
    if created:
        created[0].select_set(True)
        bpy.context.view_layer.objects.active = created[0]
    bpy.ops.wm.save_as_mainfile(filepath=str(output))


def export_fbx(source, output, manifest_path):
    bpy.ops.wm.open_mainfile(filepath=str(source))
    prepare_object_mode()
    text = bpy.data.texts.get(MANIFEST)
    payload = json.loads(text.as_string()) if text else None
    if payload and payload.get("version") not in (1, 2):
        raise RuntimeError("Unsupported Nyades manifest version")
    wanted = {entry["id"] for entry in payload["meshes"]} if payload else None
    if payload:
        for entry in payload['meshes']:
            obj = bpy.data.objects.get(entry['id'])
            if obj is None or obj.type != 'MESH':
                continue
            if obj.data.users > 1 and any(s.material is None for s in obj.material_slots):
                obj.data = obj.data.copy()
            edited = []
            for slot, material_slot in enumerate(obj.material_slots):
                material = material_slot.material
                empty = material is None
                if empty:
                    # FBX omits null material references. Use a unique export-only
                    # placeholder so its face assignment survives; Unity restores null.
                    material = bpy.data.materials.new(f'{entry["id"]}_Empty_Slot_{slot}')
                    material_slot.material = material
                identity = re.match(r'^(NRTM_\d+)_Slot_(\d+)_', material.name)
                original_mesh = material.get('nyades_mesh_id') or (identity.group(1) if identity else None)
                original_slot = material.get('nyades_slot', int(identity.group(2)) if identity else -1)
                # Upgrade old files in the background without changing the source .blend.
                edited.append({'name': material.name, 'originalMesh': original_mesh,
                               'originalSlot': int(original_slot), 'empty': empty})
            entry['editedMaterials'] = edited
    # Bake before canonicalizing bone/group names, so masks referring to skin groups
    # still resolve to their original group and are exported as ordinary morph deltas.
    for obj in bpy.data.objects:
        if obj.type == 'MESH' and (wanted is None or obj.name in wanted):
            bake_shape_key_vertex_groups(obj)
    if payload and payload["version"] == 2:
        armature = bpy.data.objects.get("Nyades_Rig")
        if payload["rig"] and (armature is None or armature.type != "ARMATURE"):
            raise RuntimeError("The exported armature is missing")
        payload["editedRig"] = []
        if armature:
            originals = {entry["id"]: entry for entry in payload["rig"]}
            ids = {}
            for bone in armature.data.bones:
                token = bone.get("nyades_id")
                if token in originals and bone.name != token:
                    raise RuntimeError("Keep the identifier of existing bone: " + originals[token]["name"])
                if token not in originals:
                    token = "NRTB_NEW_" + hashlib.sha1(bone.name.encode("utf-8")).hexdigest()[:16]
                if token in ids.values():
                    raise RuntimeError("Duplicated bone identifier: " + bone.name)
                ids[bone.name] = token
            if not set(originals).issubset(set(ids.values())):
                raise RuntimeError("An existing bone was deleted. Keep original bones to preserve Unity references.")
            for bone in armature.data.bones:
                token = ids[bone.name]
                original = originals.get(token)
                if original:
                    baseline = matrix_from(list(bone["nyades_rest_matrix"]))
                    ratio = bone.length / float(bone["nyades_rest_length"])
                    current = armature.matrix_world @ bone.matrix_local
                    delta = CONVERSION.inverted() @ current @ baseline.inverted() @ CONVERSION
                    updated = delta @ matrix_from(original["matrix"])
                    old_rotation = original["rotation"]
                    rotation = delta.to_quaternion().normalized() @ Quaternion((old_rotation[3], *old_rotation[:3]))
                    scale = list(bone.get("unity_scale", original["scale"]))
                    scale[1] *= ratio
                    entry = dict(original)
                else:
                    updated = CONVERSION.inverted() @ armature.matrix_world @ bone.matrix_local @ BONE_AXES
                    rotation = updated.to_quaternion().normalized()
                    scale = list(bone.get("unity_scale", (1, 1, 1)))
                    entry = {"id": token, "name": bone.name, "path": None}
                entry["parent"] = ids[bone.parent.name] if bone.parent else None
                entry["matrix"] = matrix_values(updated)
                entry["position"] = list(updated.translation)
                entry["rotation"] = [rotation.x, rotation.y, rotation.z, rotation.w]
                entry["scale"] = scale
                payload["editedRig"].append(entry)
            # Rename only in this background process, including groups: the source .blend stays intact.
            for bone in armature.data.bones:
                bone.name = ids[bone.name]
            for obj in bpy.data.objects:
                if obj.type == "MESH":
                    for group in obj.vertex_groups:
                        if group.name in ids:
                            group.name = ids[group.name]
    deselect_objects()
    selected = []
    for obj in bpy.data.objects:
        if obj.type == "MESH" and (wanted is None or obj.name in wanted):
            if obj.data.shape_keys:
                if not obj.data.shape_keys.use_relative:
                    raise RuntimeError("Use relative shape keys: " + obj.name)
                for key in obj.data.shape_keys.key_blocks:
                    key.value = 0
            obj.hide_viewport = False
            obj.hide_set(False)
            obj.select_set(True)
            selected.append(obj)
        if obj.type == "ARMATURE" and (payload is None or obj.name == "Nyades_Rig"):
            obj.data.pose_position = "REST"
            obj.hide_viewport = False
            obj.hide_set(False)
            obj.select_set(True)
    if wanted and {obj.name for obj in selected} != wanted:
        raise RuntimeError("An exported mesh object was deleted or renamed. Keep NRTM object IDs.")
    if not selected:
        raise RuntimeError("No avatar meshes found")
    bpy.context.view_layer.objects.active = selected[0]
    bpy.context.view_layer.update()
    bpy.ops.export_scene.fbx(filepath=str(output), use_selection=True,
                             object_types={"MESH", "ARMATURE"}, use_mesh_modifiers=False,
                             add_leaf_bones=False, bake_anim=False, axis_forward="-Z", axis_up="Y",
                             path_mode="AUTO", use_armature_deform_only=False)
    if payload:
        Path(manifest_path).write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=["push", "pull"], required=True)
    parser.add_argument("--source")
    parser.add_argument("--output", required=True)
    parser.add_argument("--json", required=True)
    options = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    if options.mode == "pull":
        payload = json.loads(Path(options.json).read_text(encoding="utf-8-sig"))
        create_blend(payload, options.output)
    else:
        if not options.source:
            parser.error("--source is required for push")
        export_fbx(options.source, options.output, options.json)
    print("NYADES_ROUNDTRIP_OK", options.mode, options.output)


if __name__ == "__main__":
    main()
