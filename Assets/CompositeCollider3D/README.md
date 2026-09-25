# CompositeCollider3D

`CompositeCollider3D` combines closed 3D Collider shapes with Boolean operations and creates a collision representation for static or dynamic objects.

## Supported sources

- `MeshCollider`: a readable, closed triangle mesh with consistent outward winding.
- `BoxCollider`, `SphereCollider`, `CapsuleCollider`: converted to closed triangle meshes before the operation. Sphere and capsule surfaces are polygonal approximations.

Add the component to a parent GameObject and populate **Sources** in operation order. A directly assigned Collider always uses **Merge**. To use **Difference** or **Intersect**, add `CompositeColliderSource3D` to the source object and assign that component in the row; its Collider and Operation are used. **Use Child Sources** fills the list from Source components below the parent; the resulting list can be reordered in the Inspector. The first source establishes the initial solid, so its Operation is ignored. For example, `A → Difference B → Merge C` evaluates as `(A − B) ∪ C`.

Choose **Static Concave** to create a single nonconvex MeshCollider, or **Dynamic Convex** to use CoACD to create convex MeshColliders under one Rigidbody. Run **Generate Geometry** from the Inspector or call `GenerateGeometry()` from code. Assign a Physics Material and optional Include Layers, Exclude Layers, and Layer Override Priority on the composite; these are applied to every generated Collider. The source Colliders are disabled after a successful generation to prevent duplicate contacts. The selected object displays source shapes in blue, the Boolean result in green, and convex parts in orange.

**Generation Type** selects **Manual** or **Synchronous**. Manual regenerates only when `GenerateGeometry()` is called. Synchronous checks input geometry, source operations, relative transforms, and collision representation while editing the scene, then regenerates only when one of those changes. Moving or rotating the whole composite together with its child sources keeps the existing result. Physics Material and Layer Override changes are applied to generated Colliders without rebuilding geometry. An unchanged successful result is reused after scene reload. Synchronous regeneration does not run in Play Mode or a player build.

The component uses ManifoldNET for Boolean operations and CoACD for dynamic convex decomposition. Mesh inputs must be readable. Dynamic sources should be children of the composite GameObject. Keep the composite object's scale at `(1,1,1)` for the intended collision shape. Invalid or empty results retain the previous generated Collider.

**Save Generated Meshes...** creates one `.asset` containing the result mesh and any convex parts, then assigns those saved meshes to the generated Colliders. Unsaved generated meshes are scene objects. Changes to source shapes or relative transforms require another call to **Generate Geometry** and, if needed, another save. Native plugin support depends on the platform; the included ManifoldNET package supplies Windows x64 binaries.
