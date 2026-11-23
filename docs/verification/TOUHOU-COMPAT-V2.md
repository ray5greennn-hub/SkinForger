# SkinForger Touhou Compat v0.3.0

This version replaces the unsafe v0.1 global `Renderer.material` and
`Renderer.sharedMaterial` patches with event-driven hooks on Muse Dash's
`ReplaceSkeletonCustomMaterials.Replace` lifecycle and SkinForger's existing
low-frequency `ApplySkeleton` completion point.

Bad Apple performs a later mesh/material refresh after `ReplaceBadApple` returns.
Version 0.3.0 registers a one-shot `OnMeshAndMaterialsUpdated` callback on the
bounded target `SkeletonRenderer`. After that final game refresh, it restores
the runtime-atlas compatibility material in both Spine's active override
dictionary and the final `MeshRenderer.sharedMaterials` slots, then immediately
detaches. It does not use a global renderer setter patch or a per-frame scan.

The compatibility path is restricted to skeletons that SkinForger actually
processed. `TouhouLogic.ReplaceBadApple(GameObject)` supplies the bounded
character hierarchy used to register the matching Spine custom-material
component; unrelated Bad Apple scene objects are not treated as SkinForger
runtime atlases.

The v0.1 source remains in `../TouhouCompat`, and its disabled binary remains
in `Mods/SkinForger.TouhouCompat.dll.77D461B8.stackoverflow.disabled`.
