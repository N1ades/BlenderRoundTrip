# Nyades FT Toolkit for Blender

A portable Blender 4.2+ add-on for **Basis** symmetry repair, mirrored shape keys and live face tracking preview. Geometry operations work on the active mesh of any avatar, including meshes without Unity or Round Trip.

## Installation

1. Download `nyades_ft_toolkit-<version>.zip` from [Releases](https://github.com/N1ades/BlenderRoundTrip/releases/latest), or open **Nyades → Blender Round Trip → Open FT Toolkit Add-on** in Unity to reveal `nyades_ft_toolkit.zip`.
2. In Blender, open **Edit → Preferences → Get Extensions → top-right menu → Install from Disk**. Select the ZIP and enable **Nyades FT Toolkit**.
3. Select a mesh, press **N** in the 3D View, and open **FT Toolkit**.

Install once to use the add-on across `.blend` files. Round Trip also works without it. Update by installing a new ZIP over the previous version. The add-on version matches the Round Trip release number. Network permission is used by the local OSC receiver. The ZIP includes source, manifest, README and license, with no external Python dependencies.

## Symmetry repair

- **Axis** selects the local symmetry axis. The plane passes through local zero; object position and rotation stay unchanged.
- **Copy from** selects the side to retain. Default **+ to -** repairs the negative side from the positive side. Use **- to +** to reverse it.
- **Pair distance** limits nearby mirrored vertex matching in mesh coordinates. Default: `0.0002`. With arbitrary object scale, this is not a distance in world meters.
- **Center tolerance** sets error tolerance and the distance from the plane within which vertices snap to zero. Default: `0.000001`.
- **Restore islands by UV** recovers displaced islands by active-channel UVs, materials and face connectivity. Each island needs an unambiguous counterpart, and UVs must uniquely identify its vertices. Different left/right UVs prevent this matching.
- **Check Symmetry** checks Basis independently of the active key or slider values.
- **Repair Symmetry** repairs the entire active mesh in Object Mode or single-object Edit Mode. Vertex selection does not limit the operation. **Ctrl+Z** undoes it. Check the result, save the `.blend`, then import through Round Trip.

Start with **X, + to -, Restore islands by UV** and verify the direction for your mesh. Reports show moved vertices, before/after errors, unpaired vertices on both sides, ambiguous UV islands and maximum displacement. Unpaired or ambiguous regions may remain unchanged. If the final error is nonzero, inspect direction, distance and geometry. Whole-mesh repair can alter intentional asymmetry.

## Preserved data

Vertices are not merged or deleted. Indices, edges, faces, UVs, materials, groups and weights are retained. Every shape key receives the same coordinate correction as Basis: **new key = old deformation + new Basis**. This preserves one-sided expressions, Relative Key, Vertex Group, values, mute state and slider limits. Repairing Basis does not make expressions symmetric.

Shared mesh data is copied for the active object before editing. Absolute shape keys and simultaneous multi-object editing are unsupported. Repair does not run automatically on export; you choose its direction.

## Apply Vertex Group

Select a shape key with a **Vertex Group**, then click **Apply Vertex Group** in **N → FT Toolkit**. Weights are baked into the key's coordinates and its Vertex Group field is cleared. Each vertex becomes `Relative Key + (Shape Key - Relative Key) × weight`: 0 removes movement, 0.5 halves it, and 1 retains it fully. The group and skinning weights remain. Missing groups are treated as no mask, matching Blender; existing empty groups produce zero deformation.

Name, value, mute state, slider limits and Relative Key are retained. Direct and indirect dependent keys receive the same coordinate correction to preserve their deformations and the final appearance. Basis is unchanged. Shared data is copied for the active mesh. Supports Object Mode, single-object Edit Mode and **Ctrl+Z**. Live FT Preview stops during the operation. Available for relative non-Basis keys with an assigned group.

## Shape Key Flip

Select a non-Basis key and click **Flip Active Shape Key**. This swaps both sides' deformations and reflects movement along the selected axis. It uses the same UV/topology and distance pairing as symmetry repair. **Copy from** affects Repair only; Flip always swaps both sides. Basis, other keys, Relative Key, name, value and active key settings are retained. Flipping again restores the old deformation within Blender's coordinate precision.

Any unpaired vertex outside the center plane cancels the entire flip before editing. The error reports unpaired vertex counts. Check **Pair distance**, matching UVs, or repair Basis first. This prevents partial flips with skipped neighbors.

With **Mirror shape key mask** enabled, the Vertex Group mask is mirrored into a separate group. Original groups and skinning weights stay unchanged. A return flip reuses the original mask if its weights match the result. Symmetric masks need no copy. With the option disabled, the mask reference stays unchanged, so a one-sided mask can hide a flipped deformation. Supports Object Mode, single-object Edit Mode and **Ctrl+Z**.

## Create a mirrored shape key

**Create Mirrored Shape Key** creates a new key using the same pairs as Flip. The source geometry, value and mask are preserved. The new name swaps **Right ↔ Left**, including CamelCase (`MouthSmileRight → MouthSmileLeft`), separate `right/left`, and `RIGHT/LEFT`. Names without a side receive `_Mirrored`. Existing keys are not overwritten; Blender adds `.001` and subsequent suffixes. The new key becomes active at **0**. Relative Key, limits and mute state are retained; masks mirror when enabled. Supports Object/Edit Mode and Undo.

## Live FT preview: Unity → Blender

Unity evaluates the Animator and sends final shape key weights. Blender does not reproduce Unity blend trees, smoothing or binary FT parameters.

1. Open the Round Trip `.blend`, enter **Object Mode**, then **N → FT Toolkit → Live FT Preview → Start Live FT Preview**. Default receiver: **127.0.0.1:9003**.
2. In Unity, open **Nyades → FT Live Preview**, or **Open Live FT Preview** in Round Trip. Select the avatar root. **Round Trip JSON** provides mesh matching; the Round Trip button uses the sidecar beside your `.blend` when available. Without a sidecar, leave the field empty and use unique mesh names; Blender's `unity_name` is also considered.
3. Choose a mode below, match **Blender OSC port** in both applications, then start Unity preview.

### Edit Mode FX without Play Mode

Leave **Stream current avatar** disabled. Unity runs an FX AnimatorControllerPlayable on a temporary avatar copy in a preview scene. **Build Modular Avatar / NDMF preview copy** builds merged FX controllers and parameters using installed MA/NDMF tools. Build assets are not saved; the source avatar and controllers are retained. Set **FX override** for a separate controller.

**Show Unity avatar preview** displays the same animated copy alongside Blender without Play Mode. **Frame Face** frames the head when found; **Frame Avatar** shows the full avatar. Drag to rotate, scroll to zoom, and Shift-drag or middle-drag to pan. Unity and Blender use their own materials with weights from the same Animator result. Disable Unity preview to reduce GPU work when unnecessary.

FX preview retains local poses of transforms the controller does not animate, preventing extra bones from implicitly resetting to the Humanoid reference pose. Explicit position, rotation and scale curves apply; individual position/scale axes are respected. Humanoid muscle clips retain control of human bones. Emulator mode reads the runtime pose. The bridge sends shape key weights, excluding explicit bone animation and IK.

### Update rate and performance

Set **Updates per second = 60** in Unity and Blender; Blender supports 10–120. Change it before starting. Blender's window timer updates while the mouse is idle. Pending packets are coalesced so each key gets only its latest value per frame. Unchanged values are skipped, without a full mesh update per packet. Unity caches names, full frame weights and packets, with up to 64 keys per packet.

Blender reports incoming **Unity** frames, **receiver** updates and **processing** time separately. These are transport and handler rates, not GPU FPS. Low incoming rates indicate a Unity/source bottleneck; if input is sufficient but the viewport is slow, try Solid shading and disable expensive modifiers temporarily. Target rates do not guarantee matching viewport FPS with expensive geometry or materials.

Send VRCFT/OSC `/avatar/parameters/<exact parameter name>` to **127.0.0.1:9000**, or the configured **VRCFT OSC input port**. Float, Int, Bool and OSC bundles are supported. Slashes within names are retained, such as `FT/v2/JawOpen`. **Enable Eye / Lip Tracking** enables existing parameters ending in `EyeTrackingActive` or `LipTrackingActive`, also enabled on startup. Enter values manually in the parameter list; clear the `FT` filter to show every parameter.

For VRCFT legacy OSC, select your **VRChat OSC profile** (`usr_...`) after starting, then **Prepare / Notify VRCFT (legacy OSC)**. It creates its own `avtr_NyadesFTPreview_...json` in `LocalLow/VRChat/VRChat/OSC/<usr_...>/Avatars` and sends `/avatar/change` to **127.0.0.1:9001**. Addresses and types use the built avatar's Expression Parameters, falling back to controller parameters. Binary Bool parameters are therefore described correctly even when direct blend trees use float inputs. Internal Animator parameters are omitted when Expression Parameters are available. Existing real-avatar configurations are preserved. This follows Av3Emulator's OSC configuration and notification approach. VRCFT must run in legacy OSC mode and target Unity's configured input port; repeat the notification if needed.

If no profile exists, create one through the normal VRChat workflow, or use **Export VRCFT OSC parameter JSON** for manual setup. OSCQuery/mDNS discovery is not included. To reuse an Av3Emulator input port, stop the emulator session first, or use streaming mode below.

Supported features include Animator transitions, blend trees, curves, masks and controller smoothing, plus SDK Parameter Driver Set/Add/Copy/Random, Animator Layer Control and FX Playable Layer Control. This expression preview does not simulate IK, PhysBones/Contacts, other playable layers or arbitrary third-party handlers.

### Av3Emulator / Gesture Manager

Enable **Stream current avatar**. Start the emulator normally, enable OSC there, and select the **local runtime avatar** whose renderers are animated. The bridge reads that avatar's final weights and sends them to Blender. It does not bind the emulator's input port or replace its Animator. Full Av3Emulator simulation uses Play Mode; manually adjusted weights can also stream outside Play Mode.

### Stopping, editing and saving

- **Stop** restores Blender values, mute state, slider limits and Show Only Shape Key. If incoming data stops, sliders restore after one second.
- Saving a `.blend` temporarily restores authoring values, then resumes the live viewport pose, preventing camera expressions from being saved accidentally.
- Edit Mode pauses updates for that mesh. Undo and loading another file stop the receiver; restart it afterward.
- Stopping Unity preview, changing Play Mode, closing the window or recompiling closes sockets and removes the preview copy. Editor FX preview does not save or modify the source avatar.
- Weights normalize by Unity's full frame weight: `30 / 100 → 0.3`, `30 / 50 → 0.6`. Negative values and values above 1 work within Blender's limits. Zeros are transmitted so closed expressions do not remain stuck.
- Blender displays packet, malformed-message and unmatched mesh/key counters. New keys can be animated after a normal return import and animation setup.

Protocol: OSC `/nyades/ft/frame` with one JSON string containing `version: 1`, `session`, `sequence`, mesh identifier/name and `{name, value}` items. Unity splits keys into small packets. Both sockets run locally: Unity receives VRCFT parameters; Blender receives final shape key weights.

## Build the ZIP

From the repository root:

```sh
python Editor/Blender/build_ft_toolkit.py
```

From the Unity project root:

```sh
python Assets/Nyades/BlenderRoundTrip/Editor/Blender/build_ft_toolkit.py
```

Includes source, manifest, README and LICENSE, without Unity `.meta` files or Python caches. Source license: GPL-3.0-or-later.
