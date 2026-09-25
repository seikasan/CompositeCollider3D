using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;

namespace CompositeCollider3D
{
    /// <summary>
    /// Colliderの閉じた立体を順序どおりにBoolean演算し、物理用Colliderを生成します。
    /// </summary>
    [ExecuteAlways]
    public sealed partial class CompositeCollider3D : MonoBehaviour
    {
        public enum GenerationType
        {
            Manual,
            Automatic
        }

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

        [SerializeField] private Collider[] _sources = Array.Empty<Collider>();
        [SerializeField] private GenerationType _generationType = GenerationType.Manual;
        [SerializeField] private CollisionRepresentation _representation = CollisionRepresentation.StaticConcave;
        [SerializeField] private PhysicsMaterial _material;
        [SerializeField] private LayerMask _includeLayers;
        [SerializeField] private LayerMask _excludeLayers;
        [SerializeField] private int _layerOverridePriority;
        [SerializeField, Range(0.01f, 1f), Tooltip("Higher values usually use fewer convex parts but approximate concavities less closely.")]
        private float _coacdThreshold = 0.05f;
        [SerializeField, Range(1000, 10000), Tooltip("Samples used to estimate decomposition error. Lower values may generate faster.")]
        private int _coacdSampleResolution = 2000;
        [SerializeField, Range(60, 2000), Tooltip("Search iterations for each CoACD split. Lower values may generate faster.")]
        private int _coacdMctsIteration = 150;

        [SerializeField, HideInInspector] private List<Mesh> _generatedMeshes = new();
        [SerializeField, HideInInspector] private GameObject _generatedRoot;
        [SerializeField, HideInInspector] private Mesh _mergedMesh;
        [SerializeField, HideInInspector] private bool _generatedMeshesAreSavedAssets;
        [SerializeField, HideInInspector] private string _lastGeneratedHash;
        [SerializeField, HideInInspector] private long _lastTotalMilliseconds;
        [SerializeField, HideInInspector] private long _lastCaptureMilliseconds;
        [SerializeField, HideInInspector] private long _lastBooleanMilliseconds;
        [SerializeField, HideInInspector] private long _lastCoacdMilliseconds;
        [SerializeField, HideInInspector] private long _lastApplyMilliseconds;
        [NonSerialized] private string _lastFailedHash;
        [NonSerialized] private bool _settingsDirty = true;
        [NonSerialized] private int _lastAppliedLayer = -1;

#if UNITY_EDITOR
        private void Update()
        {
            if (Application.IsPlaying(gameObject)) return;

            PollGeometryGeneration();

            if (_settingsDirty || _lastAppliedLayer != gameObject.layer)
            {
                ApplyColliderSettings();
                _settingsDirty = false;
                _lastAppliedLayer = gameObject.layer;
            }

            if (_generationType != GenerationType.Automatic ||
                _sources == null ||
                _sources.Length < 2)
            {
                return;
            }

            string hash = ComputeGenerationHash();
            if (_generationTask == null &&
                hash != _lastFailedHash &&
                (hash != _lastGeneratedHash || !HasGeneratedColliders()))
            {
                RequestGeometryGeneration();
            }
        }

        private void OnValidate() => _settingsDirty = true;
#endif

        [ContextMenu("Generate Geometry")]
        public void GenerateGeometry()
        {
#if UNITY_EDITOR
            if (_generationTask != null)
            {
                Debug.LogWarning("CompositeCollider3D generation is already running. Wait for it to finish before calling GenerateGeometry().", this);
                return;
            }
#endif
            Mesh nextMerged = null;
            List<Mesh> nextHulls = null;
            string hash = null;
            try
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_sources == null || _sources.Length < 2)
                {
                    throw new InvalidOperationException("At least two Collider sources are required.");
                }

                hash = ComputeGenerationHash();

                GenerationSnapshot snapshot = CaptureSnapshot();
                long captureMs = ElapsedMilliseconds(started);
                RawResult result = ComputeRaw(snapshot);
                long applyStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                nextMerged = ToUnityMesh(result.Merged, "Composite3D Result");

                if (result.Hulls != null)
                {
                    nextHulls = new List<Mesh>(result.Hulls.Length);
                    for (int i = 0; i < result.Hulls.Length; i++)
                    {
                        nextHulls.Add(ToUnityMesh(result.Hulls[i], $"Convex {i}"));
                    }
                }

                CommitGeneratedMeshes(nextMerged, nextHulls, hash);
                RecordGeneration(result, ElapsedMilliseconds(started), captureMs,
                    ElapsedMilliseconds(applyStarted), nextMerged, nextHulls);
            }
            catch (Exception e)
            {
                _lastFailedHash = hash;
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

        private void CommitGeneratedMeshes(Mesh nextMerged, List<Mesh> nextHulls, string hash)
        {
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
                        var part = new GameObject($"Convex {i}");
                        part.transform.SetParent(stagedRoot.transform, false);
                        AddCollider(part, nextHulls[i], true);
                    }
                }

                if (_representation == CollisionRepresentation.DynamicConvex)
                {
                    var rb = GetComponent<Rigidbody>();
                    if (rb == null)
                    {
                        rb = gameObject.AddComponent<Rigidbody>();
                    }
                    rb.isKinematic = false;
                }
                else if (TryGetComponent<Rigidbody>(out var rb) && !rb.isKinematic)
                {
                    throw new InvalidOperationException("StaticConcave requires no dynamic Rigidbody.");
                }

                foreach (Collider source in _sources)
                {
                    source.enabled = false;
                }

                GameObject previousRoot = _generatedRoot;
                var previousMeshes = new List<Mesh>(_generatedMeshes);
                bool previousMeshesAreSavedAssets = _generatedMeshesAreSavedAssets;

                _generatedRoot = stagedRoot;
                _mergedMesh = nextMerged;
                _generatedMeshesAreSavedAssets = false;
                _lastGeneratedHash = hash;
                _lastFailedHash = null;
                _lastAppliedLayer = gameObject.layer;
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

            }
            catch
            {
                DisposeUnityObject(stagedRoot);
                throw;
            }

        }

        private bool HasGeneratedColliders()
        {
            return _generatedRoot != null && _generatedMeshes is { Count: > 0 };
        }

        public bool IsGeneratedGeometryCurrent =>
            HasGeneratedColliders() && _lastGeneratedHash == ComputeGenerationHash();

        private string ComputeGenerationHash()
        {
            var hash = new Hash128();

            hash.Append((int)_representation);
            if (_representation == CollisionRepresentation.DynamicConvex)
            {
                hash.Append(Mathf.Clamp(_coacdThreshold, 0.01f, 1f));
                hash.Append(Mathf.Clamp(_coacdSampleResolution, 1000, 10000));
                hash.Append(Mathf.Clamp(_coacdMctsIteration, 60, 2000));
            }
            hash.Append(_sources?.Length ?? 0);

            if (_sources == null)
            {
                return hash.ToString();
            }

            for (int i = 0; i < _sources.Length; i++)
            {
                Collider source = _sources[i];

                hash.Append(i == 0 ? 0 : (int)ResolveOperation(source));
                hash.Append(source != null ? source.GetType().FullName : "missing");

                if (source == null) continue;

                Matrix4x4 relative = transform.worldToLocalMatrix * source.transform.localToWorldMatrix;

                for (int j = 0; j < 16; j++)
                {
                    hash.Append(Mathf.Round(relative[j] * 100000f) / 100000f);
                }

                switch (source)
                {
                    case MeshCollider meshCollider:
                        Mesh mesh = meshCollider.sharedMesh;
                        if (mesh != null)
                        {
                            try
                            {
                                hash.Append(mesh.vertices);
                                hash.Append(mesh.triangles);
                            }
                            catch (UnityException) { hash.Append(-1); }
                        }
                        else
                        {
                            hash.Append(-1);
                        }
                        break;
                    case BoxCollider box:
                        AppendVector(ref hash, box.center);
                        AppendVector(ref hash, box.size);
                        break;
                    case SphereCollider sphere:
                        AppendVector(ref hash, sphere.center);
                        hash.Append(sphere.radius);
                        AppendVector(ref hash, source.transform.lossyScale);
                        break;
                    case CapsuleCollider capsule:
                        AppendVector(ref hash, capsule.center);
                        hash.Append(capsule.radius);
                        hash.Append(capsule.height);
                        hash.Append(capsule.direction);
                        AppendVector(ref hash, source.transform.lossyScale);
                        break;
                    default:
                        hash.Append(source.GetType().FullName);
                        break;
                }
            }
            return hash.ToString();
        }

        private static void AppendVector(ref Hash128 hash, Vector3 value)
        {
            hash.Append(value.x);
            hash.Append(value.y);
            hash.Append(value.z);
        }

        private void ApplyColliderSettings()
        {
            if (_generatedRoot == null) return;

            foreach (MeshCollider col in _generatedRoot.GetComponentsInChildren<MeshCollider>(true))
            {
                col.sharedMaterial = _material;
                col.includeLayers = _includeLayers;
                col.excludeLayers = _excludeLayers;
                col.layerOverridePriority = _layerOverridePriority;
                col.gameObject.layer = gameObject.layer;
            }
        }

        private static BooleanOperation ResolveOperation(Collider collider) =>
            collider != null && collider.TryGetComponent<CompositeColliderSource3D>(out var source)
                ? source.Operation
                : BooleanOperation.Merge;

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

        private static void MakeBox(
            BoxCollider box,
            out Vector3[] vertices,
            out int[] triangles)
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

        private static void MakeSphere(
            SphereCollider sphere,
            out Vector3[] vertices,
            out int[] triangles)
        {
            float radius = sphere.radius * MaxAbs(sphere.transform.lossyScale);

            if (radius <= 0f)
            {
                throw new InvalidOperationException($"{sphere.name}: radius must be positive.");
            }

            Vector3 center = sphere.transform.TransformPoint(sphere.center);
            MakeRevolved(
                center,
                Quaternion.identity,
                radius,
                0f,
                out vertices,
                out triangles);
        }

        private static void MakeCapsule(
            CapsuleCollider capsule,
            out Vector3[] vertices,
            out int[] triangles)
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
            MakeRevolved(
                center,
                Quaternion.FromToRotation(Vector3.up, axis),
                radius,
                height * 0.5f - radius,
                out vertices,
                out triangles);
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

        private void AddCollider(
            GameObject owner,
            Mesh mesh,
            bool convex)
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
    }
}
