# Nyades Blender Round Trip

[![Build and release](https://github.com/N1ades/BlenderRoundTrip/actions/workflows/release.yml/badge.svg)](https://github.com/N1ades/BlenderRoundTrip/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/N1ades/BlenderRoundTrip)](https://github.com/N1ades/BlenderRoundTrip/releases/latest)
[![Unity 2022.3+](https://img.shields.io/badge/Unity-2022.3%2B-black)](https://unity.com/)
[![Blender 4.2+](https://img.shields.io/badge/Blender-4.2%2B-orange)](https://www.blender.org/)

Move meshes, rigs and shape keys between Unity and Blender while retaining existing avatar objects. Includes **Nyades FT Toolkit** for symmetry repair, mirrored shape keys and live face tracking preview.

Open **Nyades → Blender Round Trip** or **Nyades → FT Live Preview** in Unity. Requires Unity **2022.3+** and a Blender installation with FBX export; FT Toolkit requires Blender **4.2+**. Round Trip does not require the VRChat SDK, Modular Avatar or Poiyomi. SDK and NDMF integrations become available when those tools are installed.

## Installation

Use one installation method per project. When moving from `.unitypackage` to a Git package, remove the old `Assets/Nyades/BlenderRoundTrip` folder to avoid duplicate classes. VCC recognizes this folder through `legacyFolders`. Working files and backups are stored separately in `Assets/Nyades/BlenderRoundTripData`.

### VRChat Creator Companion / VCC

In **VCC → Settings → Packages → Add Repository**, enter:

```text
https://raw.githubusercontent.com/N1ades/BlenderRoundTrip/vpm/index.json
```

Add **Nyades Blender Round Trip** through **Manage Project**. Every published release appears in this repository automatically; install updates through VCC. [Add repository to VCC](vcc://vpm/addRepo?url=https%3A%2F%2Fraw.githubusercontent.com%2FN1ades%2FBlenderRoundTrip%2Fvpm%2Findex.json).

### Unity Package Manager / Git URL

In Unity, open **Window → Package Manager → + → Add package from git URL**:

```text
https://github.com/N1ades/BlenderRoundTrip.git#upm
```

Git must be installed. The `upm` branch contains the latest built package with the release version in `package.json`. For a specific release, replace `#upm` with its tag, such as `#v1.3.1`.

Unity locks Git dependencies to a commit. Opening Round Trip or FT Preview checks GitHub Releases in the background, at most once per hour. A small **Update available** notice displays the installed and available versions. **Update via UPM** installs the new release tag through Unity Package Manager. **Check for updates** runs a manual check. Installation starts when you click the button, so it does not interrupt your work. For VCC installations, the notice opens VCC; for `.unitypackage` installations, it opens the download page.

### Unity asset package

Download `Nyades-BlenderRoundTrip-<version>.unitypackage` from the [latest release](https://github.com/N1ades/BlenderRoundTrip/releases/latest), then use **Assets → Import Package → Custom Package**. Files are installed in `Assets/Nyades/BlenderRoundTrip`, with stable GUIDs between releases.

`Nyades-BlenderRoundTrip-<version>-Assets.zip` contains the same folder for manual copying, including `.meta` files. The Blender add-on is available separately as `nyades_ft_toolkit-<version>.zip`.

## Unity → Blender

1. Drag the avatar root from the Hierarchy into **Avatar root**, or select it and click **Use selected object**.
2. Set **Blender executable**. Steam and Blender Foundation installations are detected automatically.
3. Choose **Export Unity blendshape values**. Enabled transfers current Unity values; disabled starts Blender shape keys at **0**. This setting is saved per project. Click **Unity → New Blender File** and choose a new `.blend` file.
4. Open the file in Blender. It contains all SkinnedMeshRenderer and MeshRenderer/MeshFilter objects under the selected root, including inactive objects.

Exported data includes geometry, up to eight 2D UV channels, vertex colors, normals, material slots, weights, relative shape keys and bone rest poses, including orientation and scale. Child bones without skin weights are included. Blender materials preview color and the main texture; complete Unity shaders are not transferred.

Shape key values are normalized by their full frame weight: Unity **30** with a full frame of **100** becomes **0.3** in Blender; **30** with a full frame of **50** becomes **0.6**. These are slider values, not deformations baked into Basis. On the return trip, FBX is exported with zeroed sliders, and Unity renderer values are retained by name.

If a shape key has a **Vertex Group**, its weights are applied to the deformation before FBX export: 0 removes movement, 0.5 halves it, and 1 retains it fully. FBX receives a regular blendshape without a group mask. Relative key dependencies retain their deformations; skinning groups and weights are preserved. This conversion runs only in the background exporter. The saved `.blend` retains editable keys with their assigned groups.

Blender meshes are named `NRTM_0000` and bones `NRTB_0000`. Original names are stored in the `unity_name` custom property. These identifiers and the `Nyades_RoundTrip_Manifest` text block provide matching even when Unity names are duplicated. Keep these identifiers unchanged.

## Blender → Unity

1. Edit vertices, topology, UVs, weights and shape keys. Edit bones in the armature's **Edit Mode**: positions, directions, roll and length. You may add new bones and weights. Save with **Ctrl+S**.
2. Select the same avatar root and **Saved Blender file**.
3. Keep **Import bone edits** enabled and click **Saved Blender File → Unity**. Disable it for a mesh-only transfer; rest pose changes then prevent import.
4. Save the Unity scene or your changes in Prefab Mode after importing.

The tool replaces mesh data on existing renderers and updates existing bones' local transforms. Bone objects, components and PhysBones/SDK references are retained. New bones are added, weights are bound to them, and bind poses are recalculated. After rig changes, the Humanoid Avatar is rebuilt with the original human bone mapping. Validation on a temporary copy rejects invalid Humanoid rigs before changing your selected avatar.

Materials, Animator, SDK and Modular Avatar components remain on their objects. The FBX defines the new blendshape list: deleted keys are removed and new keys are added. Deleting or renaming keys does not block import. Existing values are retained by name; new keys start at 0. VRChat eyelid indices are remapped by name through serialization without an SDK dependency; deleted or invalid references become **-1** (unassigned).

Material slots may be removed, added or reordered. FBX defines submesh count and order. Original Unity materials, including shaders and settings, are matched by original slot identifiers rather than current indices, so dropped empty slots do not shift materials onto other faces. New exports store `nyades_mesh_id` and `nyades_slot` on Blender materials; renaming preserves these properties. Older files use the `NRTM_..._Slot_...` name prefix, which must be retained. Materials from other exported meshes match their Unity originals. A matching unique name can reuse an existing avatar material. Unknown materials are imported as Standard previews and can be assigned a Unity shader afterward. Empty slots stay null; removing all slots does not restore them automatically. **Ctrl+Z** restores meshes and material arrays.

Unity has no separate bone length property. Blender length changes transfer as a local Y scale factor. Set other scale components through the **unity_scale** bone custom property. Positive nonzero scales are supported. Bind poses use the actual resulting Unity matrices, including nonuniform parent scales.

Before editing the avatar, the tool creates `Before_Import.prefab` with the old rig, components and independent mesh copies. New mesh assets and rebuilt Humanoid Avatars are saved separately. **Ctrl+Z** reverts mesh replacements, bone edits, added bones and Humanoid Avatar replacement. Working files and backups default to `Assets/Nyades/BlenderRoundTripData/<avatar>/`; the path is configurable. Run SDK **Build & Test** after major changes.

**Import Existing FBX** also supports ordinary FBX with matching mesh paths or unique mesh names and unique bone names. Files with Nyades identifiers require the matching transport-generated `.json` sidecar. Replace older avatar-specific merged-mesh exports with a fresh export from Unity.

## Blender FT Toolkit

The standalone **Nyades FT Toolkit** repairs Basis symmetry while preserving shape key deformations, weights and topology. Click **Open FT Toolkit Add-on**, install the ZIP through **Blender → Preferences → Get Extensions → Install from Disk**, select a mesh, and open **N → FT Toolkit → Repair Symmetry**. By default, the negative X side is repaired from the positive side. It also works outside Round Trip; export does not require it.

The add-on pairs islands by UVs, materials and topology, then repairs small deviations through distance matching. Ambiguous pairs are skipped and reported. **Flip Active Shape Key** mirrors the active deformation and, by default, its Vertex Group mask. Unpaired vertices cancel the flip before any changes. **Ctrl+Z** supports both operations. See the [FT Toolkit guide](Editor/Blender/FTToolkit/README.md). The embedded archive is `Editor/Blender/nyades_ft_toolkit.zip`.

**Create Mirrored Shape Key** creates a separate mirrored key and swaps **Right ↔ Left** in its name. The source is retained; name collisions receive `.001`. The new key starts at 0.

**Apply Vertex Group** bakes a group mask into a relative shape key and clears its Vertex Group field. Visible deformation, Relative Key, dependent keys, the group itself and skinning weights are preserved. Supports Object/Edit Mode and **Ctrl+Z**.

**Open Live FT Preview** opens the Unity bridge. It receives OSC face tracking parameters and evaluates the FX Animator on a temporary copy without Play Mode, or streams final weights from an Av3Emulator/Gesture Manager runtime avatar. In Blender, enable **N → FT Toolkit → Live FT Preview**. Unity receives parameters on **9000** by default; Blender receives final weights on **9003**. Emulator mode leaves its input port to the emulator. See the add-on guide for VRCFT setup and limitations.

**Show Unity avatar preview** displays the temporary avatar alongside Blender without Play Mode. Rotate and zoom the camera or use **Frame Face / Frame Avatar**. Set **Updates per second = 60** on both sides. Window timers, coalesced poses, unchanged-value skipping and cached packets reduce per-frame work. Blender reports incoming and receiver rates and processing time; smoothness also depends on geometry and shaders.

FX preview retains bone poses that the controller does not animate, preventing extra bones from implicitly resetting to the Humanoid reference pose. Explicit FX position, rotation and scale curves still apply. The bridge sends shape key weights; explicit bone animations and full IK are not synchronized to Blender.

## Limitations

- Create a new `.blend` with the current tool for bone transfers. Older v1 files support meshes only. Ordinary FBX without Nyades data does not support rest pose changes.
- Retain existing bone identifiers and parents. Deleting, renaming or reparenting existing bones is blocked to protect references and animation paths. New bones may be parented to existing or new bones. Remove a copied `nyades_id` from a newly duplicated bone.
- **Pose Mode** changes are excluded; only the **Edit Mode** rest pose transfers. Keep the special avatar root bone unchanged when present. Bone length changes alone do not necessarily scale the visible mesh; edit geometry separately when needed.
- Keep existing mesh objects. Editing topology and material slots is supported; joining, deleting or creating mesh objects is not.
- Keep the Unity hierarchy unchanged between export and import.
- Supported inputs are triangle meshes, at most four skin weights per vertex, and relative shape keys with one positive-weight frame. Unsupported source data is rejected on export.
- Blender modifiers are not applied during export, preserving shape keys. Edit topology directly in the mesh.
- Cloth, additional vertex streams and third-party data tied to vertex indices are not rebuilt after topology changes.
- Shape key normal/tangent deltas and complex Unity materials are not exported. Regular normals, UVs and vertex colors are transferred.

Export and import use a background Blender process; an open session is not required. Files may be saved in Object, Edit or Pose Mode. Background export switches to Object Mode and commits saved Edit Mode edits; animation poses remain excluded. **Copy Error** copies the full traceback and Blender output. Closing the window stops the process and cancels pending import. After changing proportions, check Humanoid motion, eye look, colliders and PhysBones: their settings are retained but are not adjusted automatically for new anatomy.

## Optional Roxy optimization preset

**Nyades → Avatar Optimization → Configure Roxy Good** requires exactly two renderers named `Body` and `Outfit`, and **AAO Avatar Optimizer 1.9.20**. It creates a `Nyades Good Optimization` group with **AAO Merge Skinned Mesh**. Merging runs on the build copy; source meshes remain editable. The preset is specific to this layout. Check performance ratings in the VRChat SDK. Avatar models and prefabs are not included.

Texture optimization, shape key freezing, geometry reduction and Animator optimization are not enabled. FT preview with **Build NDMF** runs the Optimizing phase; the Body name retains its Blender mapping.

## Automatic releases and development

Every **push to `main`** runs [Build and release](.github/workflows/release.yml): validation, packaging and GitHub Release publication. Versions use `1.3.<github.run_number>`. PR checks and failed builds can leave gaps in run numbers. No manual tags are required. A push with multiple commits releases its final commit.

Each release includes a VPM/UPM ZIP, `.unitypackage`, Assets ZIP, standalone Blender ZIP, `package.json` and `SHA256SUMS.txt`. All archives are rebuilt from source with matching manifest versions. Files upload to a draft first; the release becomes public after every upload completes, then the VCC index is updated. PRs build and validate without publication. Reruns preserve published releases.

Branches: `main` contains source and CI, `upm` contains the latest built Git package, and `vpm` serves the VCC JSON index. Tags `v1.3.N` point to versioned packages. Source commits are recorded in release notes and package commit messages. No extra tokens are needed: the standard `GITHUB_TOKEN` receives content write access only in the publishing job.

Local builds require Python **3.11+** and Git for the publication integration test:

```sh
python -m unittest discover -s tests -v
python scripts/build_release.py --version 1.3.1
```

Output: `dist/`. The script checks Python syntax, GUIDs, archive contents and manifests without launching Unity or Blender. Check C# compilation and avatar behavior separately in Unity. Development checkouts may live in `Assets/Nyades/BlenderRoundTrip`; Git and VCC installations live under `Packages`.

Blender FT Toolkit is licensed under [GPL-3.0-or-later](Editor/Blender/FTToolkit/LICENSE), as declared in its manifest and source headers.
