using System;
using System.Collections.Generic;
using ManifoldNET;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;

namespace CompositeCollider3D
{
    /// <summary>
    /// Colliderの閉じた立体を順序どおりにBoolean演算し、物理用Colliderを生成します。
    /// </summary>
    public sealed class CompositeCollider3D : MonoBehaviour
    {
        public enum CollisionRepresentation
        {
            StaticConcave,
            DynamicConvex
        }

        public enum BooleanOperation
        {
            Merge,
            Difference,
            Intersect
        }

        [Serializable]
        public struct SourceEntry
        {
            public Collider collider;
            [Tooltip("最初の要素では使用しません。2番目以降は、それまでの結果に対する演算です。")]
            public BooleanOperation operation;
            [Tooltip("指定した場合、ColliderとOperationはこのSourceコンポーネントから読み取ります。")]
            public CompositeColliderSource3D source;
        }

        [SerializeField] private SourceEntry[] _sources = Array.Empty<SourceEntry>();
        [SerializeField] private CollisionRepresentation _representation = CollisionRepresentation.StaticConcave;
        [SerializeField] private PhysicsMaterial _material;
        [SerializeField] private LayerMask _includeLayers;
        [SerializeField] private LayerMask _excludeLayers;
        [SerializeField] private int _layerOverridePriority;
        [SerializeField] private bool _showDebugWireframes = true;
        [SerializeField] private Color _sourceColor = new(0.2f, 0.7f, 1f, 0.6f);
        [SerializeField] private Color _mergedColor = new(0.2f, 1f, 0.3f, 0.8f);
        [SerializeField] private Color _hullColor = new(1f, 0.5f, 0.1f, 0.8f);

        [SerializeField, HideInInspector] private List<Mesh> _generatedMeshes = new();
        [SerializeField, HideInInspector] private GameObject _generatedRoot;
        [SerializeField, HideInInspector] private Mesh _mergedMesh;
        [SerializeField, HideInInspector] private bool _generatedMeshesAreSavedAssets;

        [ContextMenu("Generate Geometry")]
        public void GenerateGeometry()
        {
            Mesh nextMerged = null;
            List<Mesh> nextHulls = null;
            try
            {
                if (_sources == null || _sources.Length < 2)
                {
                    throw new InvalidOperationException("At least two Collider sources are required.");
                }

                using Manifold first = MakeSolid(ResolveCollider(_sources[0]));
                Manifold accumulated = first;

                bool ownsAccumulated = false;
                try
                {
                    for (int i = 1; i < _sources.Length; i++)
                    {
                        using Manifold next = MakeSolid(ResolveCollider(_sources[i]));
                        BooleanOperation operation = ResolveOperation(_sources[i]);
                        Manifold operationResult = operation switch
                        {
                            BooleanOperation.Merge => accumulated + next,
                            BooleanOperation.Difference => accumulated - next,
                            BooleanOperation.Intersect => accumulated & next,
                            _ => throw new InvalidOperationException("Unsupported Boolean operation.")
                        };

                        if (ownsAccumulated)
                        {
                            accumulated.Dispose();
                        }

                        accumulated = operationResult;
                        ownsAccumulated = true;

                        if (operationResult.Status != ManifoldError.NoError || operationResult.IsEmpty)
                        {
                            throw new InvalidOperationException($"Boolean operation {i} ({operation}) failed or produced an empty solid: {operationResult.Status}");
                        }
                    }

                    float volume = accumulated.Properties.volume;
                    if (float.IsNaN(volume) || float.IsInfinity(volume) || volume <= 0f)
                    {
                        throw new InvalidOperationException($"Boolean result has invalid volume: {volume}.");
                    }

                    using MeshGL result = accumulated.MeshGL;
                    nextMerged = ToUnityMesh(result, "Composite3D Result");
                }
                finally
                {
                    if (ownsAccumulated)
                    {
                        accumulated.Dispose();
                    }
                }

                if (_representation == CollisionRepresentation.DynamicConvex)
                {
                    var decomposer = gameObject.AddComponent<CoACD>();
                    try
                    {
                        nextHulls = decomposer.RunACD(nextMerged);
                    }
                    finally
                    {
                        DisposeUnityObject(decomposer);
                    }

                    if (nextHulls == null || nextHulls.Count == 0)
                    {
                        throw new InvalidOperationException("CoACD produced no convex parts.");
                    }

                    foreach (Mesh hull in nextHulls)
                    {
                        if (hull == null || hull.triangles.Length == 0 || hull.triangles.Length / 3 > 255)
                        {
                            throw new InvalidOperationException("A CoACD part is empty or exceeds 255 triangles.");
                        }
                    }
                }

                var stagedRoot = new GameObject("CompositeCollider3D Generated");
                stagedRoot.transform.SetParent(transform, false);
                stagedRoot.SetActive(false);

                try
                {
                    if (nextHulls == null)
                    {
                        AddCollider(stagedRoot, nextMerged, false);
                    }
                    else
                    {
                        for (int i = 0; i < nextHulls.Count; i++)
                        {
                            var part = new GameObject("Convex " + i);
                            part.transform.SetParent(stagedRoot.transform, false);
                            AddCollider(part, nextHulls[i], true);
                        }
                    }

                    if (_representation == CollisionRepresentation.DynamicConvex)
                    {
                        var body = GetComponent<Rigidbody>();
                        if (body == null)
                        {
                            body = gameObject.AddComponent<Rigidbody>();
                        }
                        body.isKinematic = false;
                    }
                    else if (TryGetComponent<Rigidbody>(out var body) && !body.isKinematic)
                    {
                        throw new InvalidOperationException("StaticConcave requires no dynamic Rigidbody.");
                    }

                    foreach (SourceEntry entry in _sources)
                    {
                        ResolveCollider(entry).enabled = false;
                    }

                    GameObject previousRoot = _generatedRoot;
                    var previousMeshes = new List<Mesh>(_generatedMeshes);
                    bool previousMeshesAreSavedAssets = _generatedMeshesAreSavedAssets;

                    _generatedRoot = stagedRoot;
                    _mergedMesh = nextMerged;
                    _generatedMeshesAreSavedAssets = false;
                    _generatedMeshes.Clear();
                    _generatedMeshes.Add(nextMerged);

                    if (nextHulls != null)
                    {
                        _generatedMeshes.AddRange(nextHulls);
                    }

                    stagedRoot.SetActive(true);

                    if (previousRoot != null)
                    {
                        previousRoot.SetActive(false);
                        DisposeUnityObject(previousRoot);
                    }

                    if (!previousMeshesAreSavedAssets)
                    {
                        foreach (Mesh oldMesh in previousMeshes)
                        {
                            DisposeUnityObject(oldMesh);
                        }
                    }

                    Debug.Log(
                        $"CompositeCollider3D: processed {_sources.Length} solids into {nextMerged.triangles.Length / 3} triangles; convex parts: {nextHulls?.Count ?? 0}.",
                        this);
                }
                catch
                {
                    DisposeUnityObject(stagedRoot);
                    throw;
                }
            }
            catch (Exception e)
            {
                if (nextMerged != null && nextMerged != _mergedMesh)
                {
                    DisposeUnityObject(nextMerged);
                }

                if (nextHulls != null)
                {
                    foreach (Mesh hull in nextHulls)
                    {
                        if (hull != null)
                        {
                            DisposeUnityObject(hull);
                        }
                    }
                }

                Debug.LogError($"CompositeCollider3D generation failed: {e}", this);
            }
        }

        private static Collider ResolveCollider(SourceEntry entry) =>
            entry.source != null ? entry.source.SourceCollider : entry.collider;

        private static BooleanOperation ResolveOperation(SourceEntry entry) =>
            entry.source != null ? entry.source.Operation : entry.operation;

        public Mesh[] GetGeneratedMeshes() => _generatedMeshes.ToArray();

        public void UseSavedMeshes(Mesh[] meshes)
        {
            if (meshes == null || meshes.Length != _generatedMeshes.Count || _generatedRoot == null)
            {
                throw new InvalidOperationException("Saved mesh count does not match the generated Collider set.");
            }

            var colliders = _generatedRoot.GetComponentsInChildren<MeshCollider>(true);

            if (colliders.Length != meshes.Length - 1 && !(colliders.Length == 1 && meshes.Length == 1))
            {
                throw new InvalidOperationException("Generated Collider count changed before saving.");
            }

            foreach (Mesh mesh in meshes)
            {
                if (mesh == null)
                {
                    throw new InvalidOperationException("A saved Mesh is missing.");
                }
            }

            if (meshes.Length == 1)
            {
                colliders[0].sharedMesh = meshes[0];
            }
            else
            {
                for (int i = 0; i < colliders.Length; i++)
                {
                    colliders[i].sharedMesh = meshes[i + 1];
                }
            }

            var previous = new List<Mesh>(_generatedMeshes);
            bool previousAreSavedAssets = _generatedMeshesAreSavedAssets;

            _generatedMeshes.Clear();
            _generatedMeshes.AddRange(meshes);
            _mergedMesh = meshes[0];
            _generatedMeshesAreSavedAssets = true;

            if (previousAreSavedAssets) return;

            foreach (Mesh mesh in previous)
            {
                DisposeUnityObject(mesh);
            }
        }

        private Manifold MakeSolid(Collider source)
        {
            if (source == null)
            {
                throw new InvalidOperationException("A source Collider is missing.");
            }

            Vector3[] vertices;
            int[] triangles;
            Matrix4x4 matrix;

            switch (source)
            {
                case MeshCollider meshCollider:
                {
                    if (meshCollider.sharedMesh == null)
                    {
                        throw new InvalidOperationException($"{source.name}: MeshCollider has no mesh.");
                    }

                    vertices = meshCollider.sharedMesh.vertices;
                    triangles = meshCollider.sharedMesh.triangles;
                    matrix = transform.worldToLocalMatrix * source.transform.localToWorldMatrix;
                    break;
                }
                case BoxCollider box:
                    MakeBox(box, out vertices, out triangles);
                    matrix = transform.worldToLocalMatrix * source.transform.localToWorldMatrix;
                    break;
                case SphereCollider sphere:
                    MakeSphere(sphere, out vertices, out triangles);
                    matrix = transform.worldToLocalMatrix;
                    break;
                case CapsuleCollider capsule:
                    MakeCapsule(capsule, out vertices, out triangles);
                    matrix = transform.worldToLocalMatrix;
                    break;
                default:
                {
                    throw new InvalidOperationException($"{source.name}: unsupported Collider type {source.GetType().Name}.");
                }
            }

            if (vertices.Length < 4 || triangles.Length < 12)
            {
                throw new InvalidOperationException($"{source.name}: mesh is too small for a closed solid.");
            }

            var positions = new float[vertices.Length * 3];

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 p = matrix.MultiplyPoint3x4(vertices[i]);
                positions[i * 3] = p.x;
                positions[i * 3 + 1] = p.y;
                positions[i * 3 + 2] = p.z;
            }

            var indices = new uint[triangles.Length];
            bool mirrored = matrix.determinant < 0f;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                indices[i] = (uint)triangles[i];
                indices[i + 1] = (uint)triangles[i + (mirrored ? 2 : 1)];
                indices[i + 2] = (uint)triangles[i + (mirrored ? 1 : 2)];
            }

            using var input = new MeshGL(positions, indices);
            
            Manifold solid = Manifold.Create(input);

            if (solid.Status != ManifoldError.NoError || solid.IsEmpty)
            {
                ManifoldError error = solid.Status;
                solid.Dispose();
                throw new InvalidOperationException($"{source.name}: invalid closed mesh ({error}).");
            }

            return solid;
        }

        private static void MakeBox(BoxCollider box, out Vector3[] vertices, out int[] triangles)
        {
            Vector3 c = box.center;
            Vector3 h = box.size * 0.5f;

            if (h.x <= 0f || h.y <= 0f || h.z <= 0f)
            {
                throw new InvalidOperationException($"{box.name}: BoxCollider size must be positive.");
            }

            vertices = new[]
            {
                c + new Vector3(-h.x, -h.y, -h.z), c + new Vector3(h.x, -h.y, -h.z),
                c + new Vector3(h.x, h.y, -h.z), c + new Vector3(-h.x, h.y, -h.z),
                c + new Vector3(-h.x, -h.y, h.z), c + new Vector3(h.x, -h.y, h.z),
                c + new Vector3(h.x, h.y, h.z), c + new Vector3(-h.x, h.y, h.z)
            };

            triangles = new[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7,
                0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5,
                0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2
            };
        }

        private static void MakeSphere(SphereCollider sphere, out Vector3[] vertices, out int[] triangles)
        {
            float radius = sphere.radius * MaxAbs(sphere.transform.lossyScale);

            if (radius <= 0f)
            {
                throw new InvalidOperationException($"{sphere.name}: radius must be positive.");
            }

            Vector3 center = sphere.transform.TransformPoint(sphere.center);
            MakeRevolved(center, Quaternion.identity, radius, 0f, out vertices, out triangles);
        }

        private static void MakeCapsule(CapsuleCollider capsule, out Vector3[] vertices, out int[] triangles)
        {
            Vector3 scale = capsule.transform.lossyScale;
            Vector3 axis;
            float axisScale;
            float radialScale;

            switch (capsule.direction)
            {
                case 0:
                    axis = capsule.transform.rotation * Vector3.right;
                    axisScale = Mathf.Abs(scale.x);
                    radialScale = Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                    break;
                case 1:
                    axis = capsule.transform.rotation * Vector3.up;
                    axisScale = Mathf.Abs(scale.y);
                    radialScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                    break;
                case 2:
                    axis = capsule.transform.rotation * Vector3.forward;
                    axisScale = Mathf.Abs(scale.z);
                    radialScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y));
                    break;
                default:
                    throw new InvalidOperationException($"{capsule.name}: invalid capsule direction.");
            }

            float radius = capsule.radius * radialScale;
            float height = Mathf.Max(capsule.height * axisScale, radius * 2f);
            if (radius <= 0f || height <= 0f)
            {
                throw new InvalidOperationException($"{capsule.name}: capsule size must be positive.");
            }

            Vector3 center = capsule.transform.TransformPoint(capsule.center);
            MakeRevolved(center, Quaternion.FromToRotation(Vector3.up, axis), radius,
                height * 0.5f - radius, out vertices, out triangles);
        }

        private static float MaxAbs(Vector3 value) =>
            Mathf.Max(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));

        // The 12-sided, 5-step profile keeps an isolated primitive below Unity's 255-triangle convex limit.
        private static void MakeRevolved(
            Vector3 center,
            Quaternion orientation,
            float radius,
            float cylinderHalfHeight,
            out Vector3[] vertices,
            out int[] triangles)
        {
            const int sides = 12;
            const int hemisphereSteps = 5;
            var rings = new List<Vector2>();

            for (int step = 1; step <= hemisphereSteps; step++)
            {
                float angle = (Mathf.PI * 0.5f) * step / hemisphereSteps;
                rings.Add(
                    new Vector2(
                        radius * Mathf.Sin(angle),
                        cylinderHalfHeight + radius * Mathf.Cos(angle)));
            }

            if (cylinderHalfHeight > 0f)
            {
                rings.Add(new Vector2(radius, -cylinderHalfHeight));
            }

            for (int step = 1; step < hemisphereSteps; step++)
            {
                float angle = Mathf.PI * 0.5f + (Mathf.PI * 0.5f) * step / hemisphereSteps;
                rings.Add(
                    new Vector2(
                        radius * Mathf.Sin(angle),
                        -cylinderHalfHeight + radius * Mathf.Cos(angle)));
            }

            var points = new List<Vector3>(rings.Count * sides + 2)
            {
                center + orientation * new Vector3(0f, cylinderHalfHeight + radius, 0f)
            };

            foreach (Vector2 ring in rings)
            {
                for (int side = 0; side < sides; side++)
                {
                    float angle = 2f * Mathf.PI * side / sides;
                    points.Add(
                        center + orientation * new Vector3(
                            ring.x * Mathf.Cos(angle),
                            ring.y, ring.x * Mathf.Sin(angle)));
                }
            }

            int bottom = points.Count;
            points.Add(center + orientation * new Vector3(0f, -cylinderHalfHeight - radius, 0f));

            var faces = new List<int>(sides * rings.Count * 6);

            for (int side = 0; side < sides; side++)
            {
                int next = (side + 1) % sides;
                faces.Add(0); faces.Add(1 + next); faces.Add(1 + side);
            }

            for (int ring = 0; ring < rings.Count - 1; ring++)
            {
                for (int side = 0; side < sides; side++)
                {
                    int next = (side + 1) % sides;
                    int a = 1 + ring * sides + side;
                    int b = 1 + ring * sides + next;
                    int c = a + sides;
                    int d = b + sides;
                    faces.Add(a);
                    faces.Add(b);
                    faces.Add(c);
                    faces.Add(b);
                    faces.Add(d);
                    faces.Add(c);
                }
            }

            int last = 1 + (rings.Count - 1) * sides;
            for (int side = 0; side < sides; side++)
            {
                faces.Add(bottom);
                faces.Add(last + side);
                faces.Add(last + (side + 1) % sides);
            }

            vertices = points.ToArray();
            triangles = faces.ToArray();
        }

        private static Mesh ToUnityMesh(MeshGL data, string meshName)
        {
            float[] properties = data.VerticesProperties;
            int[] indices = data.TriangleVertices;

            if (properties == null || indices == null || data.PropertiesNumber < 3)
            {
                throw new InvalidOperationException("Manifold returned invalid mesh data.");
            }

            var vertices = new Vector3[data.VerticesNumber];
            int stride = data.PropertiesNumber;

            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = new Vector3(
                    properties[i * stride],
                    properties[i * stride + 1],
                    properties[i * stride + 2]);
            }

            var mesh = new Mesh
            {
                name = meshName,
                indexFormat =
                    vertices.Length > 65535
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16
            };

            mesh.vertices = vertices;
            mesh.triangles = indices;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();

            return mesh;
        }

        private void AddCollider(GameObject owner, Mesh mesh, bool convex)
        {
            var col = owner.AddComponent<MeshCollider>();
            col.convex = convex;
            col.sharedMaterial = _material;
            col.includeLayers = _includeLayers;
            col.excludeLayers = _excludeLayers;
            col.layerOverridePriority = _layerOverridePriority;

            col.sharedMesh = mesh;
            owner.layer = gameObject.layer;
        }

        private static void DisposeUnityObject(Object obj)
        {
            if (obj == null) return;
#if UNITY_EDITOR
            if (UnityEditor.EditorUtility.IsPersistent(obj)) return;
#endif

            if (Application.isPlaying)
            {
                Destroy(obj);
            }
            else
            {
                DestroyImmediate(obj);
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (!_showDebugWireframes) return;

            if (_sources != null)
            {
                foreach (SourceEntry entry in _sources)
                {
                    Collider source = ResolveCollider(entry);
                    if (source is MeshCollider meshSource && meshSource.sharedMesh != null)
                    {
                        Gizmos.color = _sourceColor;
                        Gizmos.matrix = source.transform.localToWorldMatrix;
                        Gizmos.DrawWireMesh(meshSource.sharedMesh);
                    }
                    else if (source is BoxCollider box)
                    {
                        Gizmos.color = _sourceColor;
                        Gizmos.matrix = source.transform.localToWorldMatrix;
                        Gizmos.DrawWireCube(box.center, box.size);
                    }
                    else if (source is SphereCollider sphere)
                    {
                        Gizmos.color = _sourceColor;
                        Gizmos.matrix = Matrix4x4.identity;
                        Gizmos.DrawWireSphere(source.transform.TransformPoint(sphere.center), sphere.radius * MaxAbs(source.transform.lossyScale));
                    }
                    else if (source is CapsuleCollider capsule)
                    {
                        Vector3 scale = source.transform.lossyScale;
                        Vector3 axis = source.transform.rotation *
                            (capsule.direction switch
                            {
                                0 => Vector3.right,
                                2 => Vector3.forward,
                                _ => Vector3.up
                            });

                        float axisScale = capsule.direction switch
                        {
                            0 => Mathf.Abs(scale.x),
                            2 => Mathf.Abs(scale.z),
                            _ => Mathf.Abs(scale.y)
                        };

                        float radialScale = capsule.direction switch
                        {
                            0 => Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)),
                            2 => Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)),
                            _ => Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z))
                        };

                        float radius = capsule.radius * radialScale;
                        float half = Mathf.Max(0f, capsule.height * axisScale * 0.5f - radius);
                        Vector3 center = source.transform.TransformPoint(capsule.center);
                        Gizmos.color = _sourceColor;
                        Gizmos.matrix = Matrix4x4.identity;
                        Gizmos.DrawWireSphere(center + axis * half, radius);
                        Gizmos.DrawWireSphere(center - axis * half, radius);
                    }
                }
            }

            Gizmos.matrix = transform.localToWorldMatrix;
            if (_mergedMesh != null)
            {
                Gizmos.color = _mergedColor;
                Gizmos.DrawWireMesh(_mergedMesh);
            }

            Gizmos.color = _hullColor;
            for (int i = 1; i < _generatedMeshes.Count; i++)
            {
                if (_generatedMeshes[i] != null)
                {
                    Gizmos.DrawWireMesh(_generatedMeshes[i]);
                }
            }
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
