# CompositeCollider3D

`CompositeCollider3D` combines closed 3D Collider shapes with Boolean operations and creates a collision representation for static or dynamic objects.

## Supported sources

- `MeshCollider`: a readable, closed triangle mesh with consistent outward winding.
- `BoxCollider`, `SphereCollider`, `CapsuleCollider`: converted to closed triangle meshes before the operation. Sphere and capsule surfaces are polygonal approximations.

Add the component to a parent GameObject and populate **Sources** in operation order. The first source establishes the initial solid; its Operation value is ignored. Each later entry applies its Operation to the accumulated result: **Merge** adds its volume, **Difference** subtracts it, and **Intersect** retains the overlap. For example, `A → Difference B → Merge C` evaluates as `(A − B) ∪ C`.

Choose **Static Concave** to create a single nonconvex MeshCollider, or **Dynamic Convex** to use CoACD to create convex MeshColliders under one Rigidbody. Run **Generate Geometry** from the component context menu or call `GenerateGeometry()` from code. The source Colliders are disabled after a successful generation to prevent duplicate contacts. The selected object displays source shapes in blue, the Boolean result in green, and convex parts in orange.

The component uses ManifoldNET for Boolean operations and CoACD for dynamic convex decomposition. Mesh inputs must be readable. Dynamic sources should be children of the composite GameObject. Keep the composite object's scale at `(1,1,1)` for the intended collision shape. Invalid or empty results retain the previous generated Collider.

Generated meshes are scene objects. Changes to source shapes or relative transforms require another call to **Generate Geometry**. Native plugin support depends on the platform; the included ManifoldNET package supplies Windows x64 binaries.
