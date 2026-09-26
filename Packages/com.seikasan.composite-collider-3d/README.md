# CompositeCollider3D

`CompositeCollider3D` combines closed 3D Collider shapes with Boolean operations and creates a collision representation for static or dynamic objects.

## Supported sources

- `MeshCollider`: a readable, closed triangle mesh with consistent outward winding.
- `BoxCollider`, `SphereCollider`, `CapsuleCollider`: converted to closed triangle meshes before the operation. Sphere and capsule surfaces are polygonal approximations.

Add the component to a parent GameObject and populate **Sources** with Colliders in operation order. A Collider without `CompositeColliderSource3D` on the same GameObject uses **Merge**. To use **Difference** or **Intersect**, add `CompositeColliderSource3D` alongside that Collider and choose its Operation; the Sources list still holds the Collider. **Use Child Colliders** fills the list from child Colliders, excluding generated Colliders. The list can be reordered in the Inspector. The first source establishes the initial solid, so its Operation is ignored. For example, `A → Difference B → Merge C` evaluates as `(A − B) ∪ C`.

Choose **Static Concave** to create a single nonconvex MeshCollider, or **Dynamic Convex** to use CoACD to create convex MeshColliders under one Rigidbody. Run **Generate Geometry** from the Inspector or call `GenerateGeometry()` from code. Assign a Physics Material and optional Include Layers, Exclude Layers, and Layer Override Priority on the composite; these are applied to every generated Collider. The source Colliders are disabled after a successful generation to prevent duplicate contacts.

**Generation Type** selects **Manual** or **Automatic**. In the Inspector, **Generate Geometry** starts background generation. Automatic checks input geometry, source operations, relative transforms, and collision representation while editing the scene, then starts background generation when one changes. The previous Colliders remain active until the new result is ready. Moving or rotating the whole composite together with its child sources keeps the existing result. Physics Material and Layer Override changes are applied to generated Colliders without rebuilding geometry. An unchanged successful result is reused after scene reload. Automatic regeneration does not run in Play Mode or a player build. The `GenerateGeometry()` scripting API remains synchronous.

After generation, the Console reports total time, input capture time, Boolean time, CoACD time, Unity mesh and Collider application time, result triangle count, and convex part count. CoACD time is zero in Static Concave mode.

For Dynamic Convex, **Advanced (CoACD)** exposes Concavity Threshold, Sample Resolution, and MCTS Iterations. The initial values are `0.05`, `2000`, and `150`. Lower sample resolution or fewer search iterations may shorten generation; a higher threshold may produce fewer parts with a less precise concavity. The Inspector's read-only **Generated Info** shows whether the result matches the current inputs and settings, its triangle and Collider counts, and the last successful generation times.

The component uses ManifoldNET for Boolean operations and CoACD for dynamic convex decomposition. Mesh inputs must be readable. Dynamic sources should be children of the composite GameObject. Keep the composite object's scale at `(1,1,1)` for the intended collision shape. Invalid or empty results retain the previous generated Collider.

**Save Generated Meshes...** creates one `.asset` containing the result mesh and any convex parts, then assigns those saved meshes to the generated Colliders. Unsaved generated meshes are scene objects. Changes to source shapes or relative transforms require another call to **Generate Geometry** and, if needed, another save. The package includes ManifoldNET, CoACD, and their native Windows x86_64 binaries. Boolean operations and dynamic decomposition are currently supported only on Windows x86_64.

## Installation

This package targets Unity 6000.0 or later. After the repository is publicly available, install it from the Unity Package Manager using this Git URL:

`https://github.com/seikasan/CompositeCollider3D.git?path=/Packages/com.seikasan.composite-collider-3d`

The package ID is `com.seikasan.composite-collider-3d`.

ManifoldNET and CoACD are bundled with the package, so there are no third-party Git package dependencies. Boolean generation and dynamic decomposition currently support Windows x86_64 only; other platforms are not supported by the included native binaries.

## License

Composite Collider 3D is distributed under the MIT License. See `LICENSE.md`. Third-party component licenses and attributions are in `Third Party Notices`.
