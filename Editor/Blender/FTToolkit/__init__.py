# SPDX-License-Identifier: GPL-3.0-or-later
"""Nyades FT Toolkit: topology-preserving Basis symmetry repair.

Works on the active mesh, independently of Round Trip identifiers or materials.
All distances and the symmetry plane are in object-local coordinates.
"""
import traceback
import time
import re
from array import array
from collections import defaultdict, deque

import bpy
from bpy.props import BoolProperty, EnumProperty, FloatProperty, IntProperty, PointerProperty, StringProperty
from mathutils import Vector
from mathutils.kdtree import KDTree
from . import live_preview

bl_info = {
    "name": "Nyades FT Toolkit", "author": "Nyades", "version": (1, 3, 2),
    "blender": (4, 2, 0), "location": "3D View > Sidebar > FT Toolkit",
    "description": "Repair symmetry, create mirrored shape keys and preview Unity face tracking via OSC",
    "category": "Mesh",
}


def _coordinates(mesh):
    keys = mesh.shape_keys
    if keys and not keys.use_relative:
        raise ValueError("Use relative shape keys before repairing symmetry.")
    data = keys.reference_key.data if keys else mesh.vertices
    values = array('f', [0.0]) * (len(data) * 3)
    data.foreach_get('co', values)
    return [Vector(values[i:i + 3]) for i in range(0, len(values), 3)]


def symmetry_stats(coordinates, axis=0, epsilon=1e-6):
    if not coordinates:
        return {"asymmetric": 0, "max_error": 0.0}
    tree = KDTree(len(coordinates))
    for i, position in enumerate(coordinates):
        tree.insert(position, i)
    tree.balance()
    distances = []
    for position in coordinates:
        mirrored = position.copy()
        mirrored[axis] *= -1
        distances.append(tree.find(mirrored)[2])
    return {"asymmetric": sum(d > epsilon for d in distances), "max_error": max(distances)}


def _mesh_identity(mesh):
    """UV signatures, material memberships and connected components."""
    parent = list(range(len(mesh.vertices)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for edge in mesh.edges:
        parent[find(edge.vertices[0])] = find(edge.vertices[1])
    parts = defaultdict(list)
    for i in range(len(parent)):
        parts[find(i)].append(i)
    uv = [set() for _ in parent]
    materials = [set() for _ in parent]
    active_uv = mesh.uv_layers.active
    for polygon in mesh.polygons:
        for loop_index in polygon.loop_indices:
            i = mesh.loops[loop_index].vertex_index
            materials[i].add(polygon.material_index)
            if active_uv:
                u, v = active_uv.data[loop_index].uv
                uv[i].add((round(u, 6), round(v, 6)))
    signatures = [(tuple(sorted(uv[i])), tuple(sorted(materials[i]))) for i in range(len(parent))]
    # Compare face connectivity as well as UV identity, including the material per face.
    faces = defaultdict(list)
    edges = defaultdict(list)
    for edge in mesh.edges:
        edges[find(edge.vertices[0])].append(tuple(edge.vertices))
    for polygon in mesh.polygons:
        faces[find(polygon.vertices[0])].append((tuple(polygon.vertices), polygon.material_index))
    return parts, signatures, materials, faces, edges, bool(active_uv)


def _cycle_signature(values):
    """Face identity independent of winding and starting corner."""
    values = tuple(values)
    reverse = tuple(reversed(values))
    return min(sequence[i:] + sequence[:i] for sequence in (values, reverse) for i in range(len(values)))


def _uv_pairs(mesh, positions, axis, sign, epsilon, identity):
    parts, signatures, _, faces, edges, has_uv = identity
    if not has_uv:
        return {}, 0, 0
    source_islands = defaultdict(list)
    targets = []
    for root, indices in parts.items():
        if not all(signatures[i][0] for i in indices):
            continue
        lookup = {signatures[i]: i for i in indices}
        if len(lookup) != len(indices):
            continue  # UVs cannot identify these individual vertices uniquely.
        fingerprint = tuple(sorted(lookup))
        if all(sign * positions[i][axis] > epsilon for i in indices):
            source_islands[fingerprint].append((root, lookup))
        elif all(sign * positions[i][axis] < -epsilon for i in indices):
            targets.append((root, fingerprint, lookup))
    candidates = []
    ambiguous = 0
    for target_root, fingerprint, target in targets:
        matching = []
        target_edges = {tuple(sorted((signatures[a], signatures[b]))) for a, b in edges[target_root]}
        target_faces = sorted((_cycle_signature([signatures[i] for i in indices]), mat) for indices, mat in faces[target_root])
        for source_root, source in source_islands[fingerprint]:
            source_edges = {tuple(sorted((signatures[a], signatures[b]))) for a, b in edges[source_root]}
            source_faces = sorted((_cycle_signature([signatures[i] for i in indices]), mat) for indices, mat in faces[source_root])
            if source_edges == target_edges and source_faces == target_faces:
                matching.append((source_root, source))
        if len(matching) == 1:
            candidates.append((matching[0][0], target, matching[0][1]))
        elif len(matching) > 1:
            ambiguous += 1
    use_counts = defaultdict(int)
    for root, _, _ in candidates:
        use_counts[root] += 1
    pairs = {}
    islands = 0
    for root, target, source in candidates:
        if use_counts[root] != 1:
            ambiguous += 1
            continue
        pairs.update((target[key], source[key]) for key in target)
        islands += 1
    return pairs, islands, ambiguous


def _near_pairs(positions, axis, sign, distance, epsilon, excluded_targets, excluded_sources, identity):
    """One-to-one matching; never collapse multiple vertices onto one partner."""
    _, signatures, materials, _, _, _ = identity
    sources = [i for i, p in enumerate(positions) if sign * p[axis] > epsilon and i not in excluded_sources]
    targets = [i for i, p in enumerate(positions) if sign * p[axis] < -epsilon and i not in excluded_targets]
    if not sources:
        return {}, len(targets)
    tree = KDTree(len(sources))
    for i in sources:
        tree.insert(positions[i], i)
    tree.balance()
    adjacency = {}
    choices = []
    for target in targets:
        mirrored = positions[target].copy()
        mirrored[axis] *= -1
        options = [(d, signatures[target] != signatures[source], source)
                   for _, source, d in tree.find_range(mirrored, distance)
                   if materials[target] & materials[source] or d <= epsilon or not (materials[target] or materials[source])]
        options.sort()
        adjacency[target] = [source for _, _, source in options]
        choices.extend((d, mismatch, source, target) for d, mismatch, source in options)
    source_to_target = {}
    pairs = {}
    for _, _, source, target in sorted(choices):
        if source not in source_to_target and target not in pairs:
            source_to_target[source] = target
            pairs[target] = source
    # Resolve competing nearby vertices without recursive depth limits on large meshes.
    for initial in targets:
        if initial in pairs:
            continue
        queue = deque([initial])
        visited = {initial}
        parents = {}
        free_source = None
        while queue and free_source is None:
            target = queue.popleft()
            for source in adjacency[target]:
                if source in parents:
                    continue
                parents[source] = target
                previous = source_to_target.get(source)
                if previous is None:
                    free_source = source
                    break
                if previous not in visited:
                    visited.add(previous)
                    queue.append(previous)
        while free_source is not None:
            target = parents[free_source]
            previous_source = pairs.get(target)
            pairs[target] = free_source
            source_to_target[free_source] = target
            free_source = previous_source
    return pairs, len(targets) - len(pairs)


def _pairing(mesh, positions, axis, sign, distance, epsilon, use_uv):
    identity = _mesh_identity(mesh)
    uv_pairs, islands, ambiguous = _uv_pairs(mesh, positions, axis, sign, epsilon, identity) if use_uv else ({}, 0, 0)
    near_pairs, unmatched = _near_pairs(positions, axis, sign, distance, epsilon, set(uv_pairs), set(uv_pairs.values()), identity)
    pairs = dict(uv_pairs)
    pairs.update(near_pairs)
    return pairs, {
        "uv_islands": islands, "ambiguous_islands": ambiguous,
        "unmatched_targets": unmatched,
        "unmatched_sources": sum(sign * p[axis] > epsilon for p in positions) - len(pairs),
    }


def repair_symmetry(obj, axis=0, source_positive=True, distance=0.0002, epsilon=1e-6, use_uv=True):
    """Repair the Basis, translating every key by the same per-vertex correction.

    The caller must commit Edit Mode first. No vertex, edge, face, UV, group,
    material, key setting or index is removed or reordered. Shared data is copied.
    """
    if obj.type != 'MESH' or obj.mode != 'OBJECT':
        raise ValueError("Repair requires an active mesh in Object Mode.")
    if axis not in (0, 1, 2) or distance <= 0 or epsilon <= 0:
        raise ValueError("Choose a valid axis and positive distances.")
    mesh = obj.data
    if not obj.is_editable or not mesh.is_editable:
        raise ValueError("Make the object and mesh local before repairing symmetry.")
    positions = _coordinates(mesh)
    if not positions:
        raise ValueError("The active mesh has no vertices.")
    sign = 1 if source_positive else -1
    pairs, pairing_report = _pairing(mesh, positions, axis, sign, distance, epsilon, use_uv)
    updated = [p.copy() for p in positions]
    for target, source in pairs.items():
        updated[target] = positions[source].copy()
        updated[target][axis] *= -1
    for i, position in enumerate(positions):
        if abs(position[axis]) <= epsilon:
            updated[i][axis] = 0
    moved = sum((a - b).length > 1e-9 for a, b in zip(updated, positions))
    report = {
        "before": symmetry_stats(positions, axis, epsilon),
        "after": symmetry_stats(updated, axis, epsilon),
        "moved": moved, **pairing_report,
        "max_correction": max((a - b).length for a, b in zip(updated, positions)),
    }
    if not moved:
        return report
    original_mesh = mesh
    if mesh.users > 1:
        mesh = mesh.copy()
        obj.data = mesh
    keys = mesh.shape_keys
    snapshots = {}
    if keys:
        for key in keys.key_blocks:
            values = array('f', [0.0]) * (len(positions) * 3)
            key.data.foreach_get('co', values)
            snapshots[key.name] = values
    flat_base = array('f', (component for p in updated for component in p))
    try:
        if keys:
            old_base = snapshots[keys.reference_key.name]
            for key in keys.key_blocks:
                values = flat_base if key == keys.reference_key else array('f', (
                    flat_base[i] + snapshots[key.name][i] - old_base[i] for i in range(len(flat_base))))
                key.data.foreach_set('co', values)
        else:
            mesh.vertices.foreach_set('co', flat_base)
        mesh.update()
        obj.update_tag(refresh={'DATA'})
        bpy.context.view_layer.update()
        report["after"] = symmetry_stats(_coordinates(mesh), axis, epsilon)
    except Exception:
        if mesh != original_mesh:
            obj.data = original_mesh
            bpy.data.meshes.remove(mesh)
        elif keys:
            for key in keys.key_blocks:
                key.data.foreach_set('co', snapshots[key.name])
            mesh.update()
        else:
            mesh.vertices.foreach_set('co', array('f', (c for p in positions for c in p)))
            mesh.update()
        raise
    return report


def _group_weights(obj, group):
    result = array('f', [0.0]) * len(obj.data.vertices)
    for vertex in obj.data.vertices:
        for membership in vertex.groups:
            if membership.group == group.index:
                result[vertex.index] = membership.weight
                break
    return result


def apply_vertex_group(obj):
    """Bake the active key's mask, preserving its relative deformation and dependents."""
    if obj.type != 'MESH' or obj.mode != 'OBJECT' or not obj.is_editable or not obj.data.is_editable:
        raise ValueError('Select an editable mesh in Object Mode.')
    mesh = obj.data
    keys = mesh.shape_keys
    key = obj.active_shape_key
    if not keys or not keys.use_relative or key is None or key == keys.reference_key:
        raise ValueError('Select a relative shape key other than Basis.')
    group_name = key.vertex_group
    if not group_name:
        raise ValueError('The selected shape key has no Vertex Group assigned.')
    group = obj.vertex_groups.get(group_name)
    weights = _group_weights(obj, group) if group else None
    children = defaultdict(list)
    for candidate in keys.key_blocks:
        if candidate != keys.reference_key and candidate.relative_key:
            children[candidate.relative_key.name].append(candidate.name)
    affected = {key.name}
    queue = deque([key.name])
    while queue:
        for name in children[queue.popleft()]:
            if name not in affected:
                affected.add(name)
                queue.append(name)
    if key.relative_key != key and key.relative_key.name in affected:
        raise ValueError('The selected key belongs to a Relative Key cycle. Choose an acyclic reference before applying its mask.')
    snapshots = {}
    for name in affected:
        values = array('f', [0.0]) * (len(mesh.vertices) * 3)
        keys.key_blocks[name].data.foreach_get('co', values)
        snapshots[name] = values
    old = snapshots[key.name]
    reference = array('f', [0.0]) * len(old)
    key.relative_key.data.foreach_get('co', reference)
    baked = array('f', (reference[i] + (old[i] - reference[i]) *
                       (weights[i // 3] if weights is not None else 1.0) for i in range(len(old))))
    correction = array('f', (baked[i] - old[i] for i in range(len(old))))
    key_name = key.name
    original_mesh = mesh
    if mesh.users > 1:
        mesh = mesh.copy()
        obj.data = mesh
        keys = mesh.shape_keys
        key = keys.key_blocks[key_name]
    try:
        key.data.foreach_set('co', baked)
        # Descendants use this key's coordinates as their reference. Shift their
        # targets by the same correction so their own (possibly masked) deltas stay intact.
        for name in affected - {key_name}:
            keys.key_blocks[name].data.foreach_set('co', array('f', (v + correction[i] for i, v in enumerate(snapshots[name]))))
        key.vertex_group = ''
        mesh.update()
        obj.update_tag(refresh={'DATA'})
        bpy.context.view_layer.update()
    except Exception:
        if mesh != original_mesh:
            obj.data = original_mesh
            bpy.data.meshes.remove(mesh)
        else:
            for name, values in snapshots.items():
                keys.key_blocks[name].data.foreach_set('co', values)
            key.vertex_group = group_name
            mesh.update()
        raise
    return {'key': key_name, 'group': group_name, 'dependent_keys': len(affected) - 1,
            'moved': sum(any(abs(correction[i * 3 + c]) > 1e-9 for c in range(3)) for i in range(len(mesh.vertices)))}


def flip_shape_key(obj, axis=0, distance=0.0002, epsilon=1e-6, use_uv=True, mirror_mask=True):
    """Swap reflected relative deltas across a complete, bijective vertex map.

    Unlike a partial mirror, unmatched vertices cancel the operation before any
    mutation. A copied mask keeps the effective deformation mirrored as well.
    """
    if obj.type != 'MESH' or obj.mode != 'OBJECT':
        raise ValueError("Flip requires an active mesh in Object Mode.")
    if not obj.is_editable or not obj.data.is_editable:
        raise ValueError("Make the object and mesh local before flipping.")
    if axis not in (0, 1, 2) or distance <= 0 or epsilon <= 0:
        raise ValueError("Choose a valid axis and positive distances.")
    mesh = obj.data
    key = obj.active_shape_key
    if not mesh.shape_keys or key is None or key == mesh.shape_keys.reference_key:
        raise ValueError("Select a shape key other than Basis to flip.")
    positions = _coordinates(mesh)
    pairs, report = _pairing(mesh, positions, axis, 1, distance, epsilon, use_uv)
    if report['unmatched_targets'] or report['unmatched_sources']:
        raise ValueError(f"Flip cancelled: {report['unmatched_targets']} negative / {report['unmatched_sources']} positive vertices have no unique partner. Check UV pairs, Pair distance or repair Basis first. No key was changed.")
    mapping = list(range(len(positions)))
    for target, source in pairs.items():
        mapping[target] = source
        mapping[source] = target
    old = array('f', [0.0]) * (len(positions) * 3)
    reference = array('f', [0.0]) * len(old)
    key.data.foreach_get('co', old)
    key.relative_key.data.foreach_get('co', reference)
    flipped = array('f', old)
    for target, source in enumerate(mapping):
        for component in range(3):
            delta = old[source * 3 + component] - reference[source * 3 + component]
            flipped[target * 3 + component] = reference[target * 3 + component] + (-delta if component == axis else delta)
    old_group_name = key.vertex_group
    old_group = obj.vertex_groups.get(old_group_name) if mirror_mask else None
    mirrored_weights = None
    if old_group:
        weights = _group_weights(obj, old_group)
        mirrored_weights = array('f', (weights[source] for source in mapping))
        if weights == mirrored_weights:
            mirrored_weights = None
    original_mesh = mesh
    if mesh.users > 1:
        mesh = mesh.copy()
        obj.data = mesh
        key = obj.active_shape_key
    created_group = None
    try:
        if mirrored_weights is not None:
            # Reuse the original mask on a second flip, without editing skinning groups.
            suffix = '_FT_Flip'
            candidate_name = old_group_name[:-len(suffix)] if old_group_name.endswith(suffix) else old_group_name + suffix
            group = obj.vertex_groups.get(candidate_name)
            if group is None or _group_weights(obj, group) != mirrored_weights:
                group = obj.vertex_groups.new(name=candidate_name)
                created_group = group
                for i, weight in enumerate(mirrored_weights):
                    if weight:
                        group.add([i], weight, 'REPLACE')
            key.vertex_group = group.name
        key.data.foreach_set('co', flipped)
        mesh.update()
        obj.update_tag(refresh={'DATA'})
        bpy.context.view_layer.update()
    except Exception:
        if created_group:
            obj.vertex_groups.remove(created_group)
        if mesh != original_mesh:
            obj.data = original_mesh
            bpy.data.meshes.remove(mesh)
        else:
            key.vertex_group = old_group_name
            key.data.foreach_set('co', old)
            mesh.update()
        raise
    return {**report, "key": key.name, "paired_vertices": len(pairs) * 2,
            "center_vertices": len(positions) - len(pairs) * 2, "mask": key.vertex_group,
            "moved": sum(any(abs(flipped[i*3+a] - old[i*3+a]) > 1e-9 for a in range(3)) for i in range(len(positions)))}


def mirrored_key_name(name):
    """Swap directional words and common CamelCase suffixes, avoiding 'Brightness'."""
    pattern = r'Right|Left|RIGHT|LEFT|(?<![a-zA-Z])(?:right|left)(?![a-zA-Z])'

    def replace(match):
        word = match.group(0)
        opposite = 'left' if word.lower() == 'right' else 'right'
        return opposite.upper() if word.isupper() else opposite if word.islower() else opposite.title()

    result, count = re.subn(pattern, replace, name)
    return result if count else name + '_Mirrored'


def create_mirrored_key(obj, axis=0, distance=0.0002, epsilon=1e-6, use_uv=True, mirror_mask=True):
    """Create an independent mirrored target; preserve the source and all other keys."""
    if obj.type != 'MESH' or obj.mode != 'OBJECT' or not obj.is_editable or not obj.data.is_editable:
        raise ValueError('Select an editable mesh in Object Mode.')
    source = obj.active_shape_key
    if source is None or source == obj.data.shape_keys.reference_key:
        raise ValueError('Select a shape key other than Basis.')
    positions = _coordinates(obj.data)
    _, report = _pairing(obj.data, positions, axis, 1, distance, epsilon, use_uv)
    if report['unmatched_targets'] or report['unmatched_sources']:
        raise ValueError(f"Mirror copy cancelled: {report['unmatched_targets']} negative / {report['unmatched_sources']} positive vertices have no partner. Check UV pairs, Pair distance or repair Basis first.")
    original_mesh = obj.data
    original_index = obj.active_shape_key_index
    source_name = source.name
    if original_mesh.users > 1:
        obj.data = original_mesh.copy()
        source = obj.data.shape_keys.key_blocks[source_name]
    new_key = None
    try:
        new_key = obj.shape_key_add(name=mirrored_key_name(source_name), from_mix=False)
        coordinates = array('f', [0.0]) * (len(source.data) * 3)
        source.data.foreach_get('co', coordinates)
        new_key.data.foreach_set('co', coordinates)
        new_key.relative_key = obj.data.shape_keys.key_blocks[source.relative_key.name]
        new_key.vertex_group = source.vertex_group
        new_key.slider_min = source.slider_min
        new_key.slider_max = source.slider_max
        new_key.interpolation = source.interpolation
        new_key.mute = source.mute
        new_key.value = 0.0
        obj.active_shape_key_index = len(obj.data.shape_keys.key_blocks) - 1
        result = flip_shape_key(obj, axis, distance, epsilon, use_uv, mirror_mask)
        return {**result, 'source_key': source_name}
    except Exception:
        if new_key is not None:
            obj.shape_key_remove(new_key)
        obj.active_shape_key_index = original_index
        if obj.data != original_mesh:
            temporary = obj.data
            obj.data = original_mesh
            bpy.data.meshes.remove(temporary)
        raise


class NYADES_FT_Settings(bpy.types.PropertyGroup):
    axis: EnumProperty(name="Axis", items=[('X', 'X', ''), ('Y', 'Y', ''), ('Z', 'Z', '')], default='X')
    source_side: EnumProperty(name="Copy from", items=[('POSITIVE', '+ to -', 'Keep the positive side'), ('NEGATIVE', '- to +', 'Keep the negative side')], default='POSITIVE')
    distance: FloatProperty(name="Pair distance", description="Maximum object-local distance for nearby vertex pairs", default=0.0002, min=1e-7, precision=6)
    epsilon: FloatProperty(name="Center tolerance", description="Object-local distance from the symmetry plane to snap to zero; also the analysis tolerance", default=1e-6, min=1e-8, precision=7)
    use_uv: BoolProperty(name="Restore islands by UV", description="Match unique islands by active UVs, materials and topology, even beyond the pair distance", default=True)
    mirror_mask: BoolProperty(name="Mirror shape key mask", description="Assign a mirrored copy of the flipped key's Vertex Group; existing groups and skin weights stay unchanged", default=True)
    last_report: StringProperty()
    last_error: StringProperty()
    live_port: IntProperty(name='OSC port', description='Receive resolved Unity shape key values on localhost', default=9003, min=1024, max=65535)
    live_fps: IntProperty(name='Updates per second', description='Target receiver refresh rate; set the Unity sender to the same rate', default=60, min=10, max=120)
    live_status: StringProperty(default='Stopped')


def _run_operator(operator, context, action):
    obj = context.active_object
    settings = context.scene.nyades_ft_toolkit
    previous_mode = obj.mode
    try:
        if previous_mode == 'EDIT':
            if len(context.objects_in_mode) > 1:
                raise ValueError("Leave multi-object Edit Mode; repair one active mesh at a time.")
            bpy.ops.object.mode_set(mode='OBJECT')
        axis = 'XYZ'.index(settings.axis)
        if action == 'apply_group':
            live_preview.receiver.stop()
            result = apply_vertex_group(obj)
            message = (f"Applied Vertex Group: {result['group']}\nShape key: {result['key']}; mask cleared\n"
                       f"Moved: {result['moved']} vertices; dependent keys preserved: {result['dependent_keys']}")
            operator.report({'INFO'}, f"Applied {result['group']} to {result['key']}; cleared shape key mask")
        elif action == 'repair':
            result = repair_symmetry(obj, axis, settings.source_side == 'POSITIVE', settings.distance, settings.epsilon, settings.use_uv)
            message = (f"Moved: {result['moved']} vertices; UV island pairs: {result['uv_islands']}\n"
                       f"Asymmetric: {result['before']['asymmetric']} -> {result['after']['asymmetric']}\n"
                       f"Unpaired: {result['unmatched_targets']} target / {result['unmatched_sources']} source\n"
                       f"Ambiguous UV islands skipped: {result['ambiguous_islands']}\n"
                       f"Max correction: {result['max_correction']:.6g}; remaining error: {result['after']['max_error']:.6g}")
            incomplete = result['after']['asymmetric'] or result['unmatched_targets'] or result['unmatched_sources']
            operator.report({'WARNING'} if incomplete else {'INFO'}, f"Moved {result['moved']} vertices; {result['after']['asymmetric']} asymmetric; {result['unmatched_targets']} / {result['unmatched_sources']} unpaired")
        elif action in {'flip', 'mirror_copy'}:
            operation = create_mirrored_key if action == 'mirror_copy' else flip_shape_key
            result = operation(obj, axis, settings.distance, settings.epsilon, settings.use_uv, settings.mirror_mask)
            label = 'Created mirror' if action == 'mirror_copy' else 'Flipped'
            message = (f"{label}: {result['key']}\nPaired vertices: {result['paired_vertices']}; center: {result['center_vertices']}\n"
                       f"UV island pairs: {result['uv_islands']}; moved: {result['moved']}\n"
                       f"Mask: {result['mask'] or '(none)'}")
            operator.report({'INFO'}, f"{label} {result['key']}; {result['paired_vertices']} paired vertices; {result['center_vertices']} center vertices")
        else:
            result = symmetry_stats(_coordinates(obj.data), axis, settings.epsilon)
            message = f"Basis asymmetric vertices: {result['asymmetric']}\nMax mirror error: {result['max_error']:.6g}"
            operator.report({'INFO'}, message.replace('\n', '; '))
        settings.last_report = message
        settings.last_error = ''
        return {'FINISHED'}
    except Exception as error:
        settings.last_error = traceback.format_exc()
        settings.last_report = str(error)
        operator.report({'ERROR'}, str(error))
        return {'CANCELLED'}
    finally:
        if previous_mode == 'EDIT' and obj.mode != 'EDIT':
            bpy.ops.object.mode_set(mode='EDIT')


def _poll_mesh(context):
    obj = context.active_object
    return obj is not None and obj.type == 'MESH' and obj.mode in {'OBJECT', 'EDIT'} and obj.is_editable


class NYADES_FT_OT_RepairSymmetry(bpy.types.Operator):
    bl_idname = 'nyades_ft.repair_symmetry'
    bl_label = 'Repair Symmetry'
    bl_description = 'Repair the entire Basis and preserve every shape key delta; undo with Ctrl+Z'
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        return _poll_mesh(context)

    def execute(self, context):
        return _run_operator(self, context, 'repair')


class NYADES_FT_OT_CheckSymmetry(bpy.types.Operator):
    bl_idname = 'nyades_ft.check_symmetry'
    bl_label = 'Check Symmetry'
    bl_description = 'Measure mirror errors on the Basis, independently of active shape key values'

    @classmethod
    def poll(cls, context):
        return _poll_mesh(context)

    def execute(self, context):
        return _run_operator(self, context, 'check')


class NYADES_FT_OT_FlipShapeKey(bpy.types.Operator):
    bl_idname = 'nyades_ft.flip_shape_key'
    bl_label = 'Flip Active Shape Key'
    bl_description = 'Swap mirrored relative deformations of the active key using complete UV/topology pairs; cancel if any pair is missing'
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        if not _poll_mesh(context):
            return False
        obj = context.active_object
        return obj.data.shape_keys is not None and obj.active_shape_key is not None and obj.active_shape_key != obj.data.shape_keys.reference_key

    def execute(self, context):
        return _run_operator(self, context, 'flip')


class NYADES_FT_OT_CopyError(bpy.types.Operator):
    bl_idname = 'nyades_ft.copy_error'
    bl_label = 'Copy Error'

    def execute(self, context):
        context.window_manager.clipboard = context.scene.nyades_ft_toolkit.last_error
        return {'FINISHED'}


class NYADES_FT_OT_ApplyVertexGroup(bpy.types.Operator):
    bl_idname = 'nyades_ft.apply_vertex_group'
    bl_label = 'Apply Vertex Group'
    bl_description = 'Bake the assigned group weights into the active shape key and clear its mask; preserve relative keys, skinning groups and the visible deformation'
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        if not NYADES_FT_OT_FlipShapeKey.poll(context):
            return False
        return context.active_object.data.shape_keys.use_relative and bool(context.active_object.active_shape_key.vertex_group)

    def execute(self, context):
        return _run_operator(self, context, 'apply_group')


class NYADES_FT_OT_CreateMirroredKey(bpy.types.Operator):
    bl_idname = 'nyades_ft.create_mirrored_key'
    bl_label = 'Create Mirrored Shape Key'
    bl_description = 'Create a mirrored copy, swap Right/Left in its name and keep the original key; existing names receive a numeric suffix'
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        return NYADES_FT_OT_FlipShapeKey.poll(context)

    def execute(self, context):
        return _run_operator(self, context, 'mirror_copy')


class NYADES_FT_OT_LivePreview(bpy.types.Operator):
    bl_idname = 'nyades_ft.live_preview'
    bl_label = 'Start Live FT Preview'
    bl_description = 'Receive Unity Animator results over OSC; Stop restores the original sliders'

    def execute(self, context):
        settings = context.scene.nyades_ft_toolkit
        try:
            if live_preview.receiver.running:
                live_preview.receiver.stop()
            else:
                live_preview.receiver.start(context.scene, settings.live_port)
                live_preview.receiver.enable_window_timer(context)
                self._generation = live_preview.receiver.generation
                context.window_manager.modal_handler_add(self)
                return {'RUNNING_MODAL'}
            return {'FINISHED'}
        except Exception as error:
            live_preview.receiver.stop()
            settings.last_error = traceback.format_exc()
            settings.live_status = str(error)
            self.report({'ERROR'}, str(error))
            return {'CANCELLED'}

    def modal(self, context, event):
        receiver = live_preview.receiver
        if not receiver.running or self._generation != receiver.generation:
            return {'CANCELLED'}
        # Blender's Event does not expose the originating Timer. Rate-limit
        # TIMER events so unrelated window timers cannot multiply our updates.
        if event.type == 'TIMER' and time.monotonic() - receiver.last_tick >= .95 / receiver.scene.nyades_ft_toolkit.live_fps:
            receiver.tick()
        return {'PASS_THROUGH'}

    def cancel(self, context):
        if self._generation == live_preview.receiver.generation:
            live_preview.receiver.stop()


class NYADES_FT_PT_LivePreview(bpy.types.Panel):
    bl_label = 'Live FT Preview'
    bl_idname = 'NYADES_FT_PT_live_preview'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'FT Toolkit'

    def draw(self, context):
        layout = self.layout
        settings = context.scene.nyades_ft_toolkit
        column = layout.column()
        column.enabled = not live_preview.receiver.running
        column.prop(settings, 'live_port')
        column.prop(settings, 'live_fps')
        layout.operator('nyades_ft.live_preview', text='Stop Live FT Preview' if live_preview.receiver.running else 'Start Live FT Preview', icon='PAUSE' if live_preview.receiver.running else 'PLAY')
        layout.label(text='Unity: Nyades > FT Live Preview')
        layout.label(text='Receives final Animator shape weights.')
        layout.label(text='Use Object Mode while previewing.')
        box = layout.box()
        for line in settings.live_status.split('; '):
            box.label(text=line)
        if live_preview.receiver.unmatched:
            layout.label(text='Unmatched mesh / shape names:', icon='INFO')
            for name in sorted(live_preview.receiver.unmatched)[:5]:
                layout.label(text=name)


class NYADES_FT_PT_Symmetry(bpy.types.Panel):
    bl_label = 'Symmetry and Shape Key Flip'
    bl_idname = 'NYADES_FT_PT_symmetry'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'FT Toolkit'

    def draw(self, context):
        layout = self.layout
        settings = context.scene.nyades_ft_toolkit
        obj = context.active_object
        layout.label(text=obj.name if obj and obj.type == 'MESH' else 'Select a mesh', icon='MESH_DATA')
        layout.prop(settings, 'axis', expand=True)
        layout.prop(settings, 'source_side')
        layout.prop(settings, 'distance')
        layout.prop(settings, 'epsilon')
        layout.prop(settings, 'use_uv')
        layout.label(text='Plane: local axis = 0. Entire mesh.', icon='INFO')
        layout.label(text='Preserves shape key deformations.')
        row = layout.row(align=True)
        row.operator('nyades_ft.check_symmetry')
        row.operator('nyades_ft.repair_symmetry', icon='MOD_MIRROR')
        layout.separator()
        layout.label(text='Flip swaps both sides of the active key.')
        layout.prop(settings, 'mirror_mask')
        layout.operator('nyades_ft.flip_shape_key', icon='ARROW_LEFTRIGHT')
        layout.operator('nyades_ft.create_mirrored_key', icon='DUPLICATE')
        layout.separator()
        layout.operator('nyades_ft.apply_vertex_group', icon='GROUP_VERTEX')
        if settings.last_report:
            box = layout.box()
            for line in settings.last_report.splitlines():
                box.label(text=line)
        if settings.last_error:
            layout.operator('nyades_ft.copy_error', icon='COPYDOWN')


CLASSES = (NYADES_FT_Settings, NYADES_FT_OT_RepairSymmetry, NYADES_FT_OT_CheckSymmetry, NYADES_FT_OT_FlipShapeKey, NYADES_FT_OT_CreateMirroredKey, NYADES_FT_OT_ApplyVertexGroup, NYADES_FT_OT_CopyError, NYADES_FT_OT_LivePreview, NYADES_FT_PT_Symmetry, NYADES_FT_PT_LivePreview)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.Scene.nyades_ft_toolkit = PointerProperty(type=NYADES_FT_Settings)
    live_preview.register()


def unregister():
    live_preview.unregister()
    del bpy.types.Scene.nyades_ft_toolkit
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)


if __name__ == '__main__':
    register()
