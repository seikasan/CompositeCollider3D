using System;
using System.Collections.Generic;
using ManifoldNET;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Vector3 = UnityEngine.Vector3;

namespace CompositeCollider3D
{
    /// <summary>
    /// v0.1: 閉じたMeshColliderソリッドを1つの静的メッシュまたは動的な凸複合体に統合します。
    /// </summary>
    public sealed class CompositeCollider3D : MonoBehaviour
    {
        public enum CollisionRepresentation
        {
            StaticConcave,
            DynamicConvex
        }

        [SerializeField] private MeshCollider[] _sources = Array.Empty<MeshCollider>();
        [SerializeField] private CollisionRepresentation _representation = CollisionRepresentation.StaticConcave;
        [SerializeField] private PhysicsMaterial _material;
        [SerializeField] private bool _showDebugWireframes = true;
        [SerializeField] private Color _sourceColor = new(0.2f, 0.7f, 1f, 0.6f);
        [SerializeField] private Color _mergedColor = new(0.2f, 1f, 0.3f, 0.8f);
        [SerializeField] private Color _hullColor = new(1f, 0.5f, 0.1f, 0.8f);

        [SerializeField, HideInInspector] private List<Mesh> _generatedMeshes = new();
        [SerializeField, HideInInspector] private GameObject _generatedRoot;
        [SerializeField, HideInInspector] private Mesh _mergedMesh;

        [ContextMenu("Generate Geometry")]
        public void GenerateGeometry()
        {
            Mesh nextMerged = null;
            List<Mesh> nextHulls = null;
            try
            {
                if (_sources == null || _sources.Length < 2)
                {
                    throw new InvalidOperationException("At least two closed MeshColliders are required.");
                }

                using Manifold first = MakeSolid(_sources[0]);
                Manifold accumulated = first;

                bool ownsAccumulated = false;
                try
                {
                    for (int i = 1; i < _sources.Length; i++)
                    {
                        using Manifold next = MakeSolid(_sources[i]);
                        Manifold union = accumulated + next;

                        if (ownsAccumulated)
                        {
                            accumulated.Dispose();
                        }

                        accumulated = union;
                        ownsAccumulated = true;

                        if (union.Status != ManifoldError.NoError || union.IsEmpty)
                        {
                            throw new InvalidOperationException($"Manifold union failed: {union.Status}");
                        }
                    }

                    using MeshGL result = accumulated.MeshGL;
                    nextMerged = ToUnityMesh(result, "Composite3D Union");
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

                    foreach (MeshCollider source in _sources)
                    {
                        source.enabled = false;
                    }

                    GameObject previousRoot = _generatedRoot;
                    var previousMeshes = new List<Mesh>(_generatedMeshes);

                    _generatedRoot = stagedRoot;
                    _mergedMesh = nextMerged;
                    _generatedMeshes.Clear();
                    _generatedMeshes.Add(nextMerged);

                    if (nextHulls != null)
                    {
                        _generatedMeshes.AddRange(nextHulls);
                    }

                    stagedRoot.SetActive(true);

                    if (previousRoot != null)
                    {
                        DisposeUnityObject(previousRoot);
                    }

                    foreach (Mesh oldMesh in previousMeshes)
                    {
                        DisposeUnityObject(oldMesh);
                    }

                    Debug.Log(
                        $"CompositeCollider3D: merged {_sources.Length} meshes into {nextMerged.triangles.Length / 3} triangles; convex parts: {nextHulls?.Count ?? 0}.",
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

        private Manifold MakeSolid(MeshCollider source)
        {
            if (source == null || source.sharedMesh == null)
            {
                throw new InvalidOperationException("A source MeshCollider or mesh is missing.");
            }

            var mesh = source.sharedMesh;
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;

            if (vertices.Length < 4 || triangles.Length < 12)
            {
                throw new InvalidOperationException($"{source.name}: mesh is too small for a closed solid.");
            }

            var positions = new float[vertices.Length * 3];
            Matrix4x4 matrix = transform.worldToLocalMatrix * source.transform.localToWorldMatrix;

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

            col.sharedMesh = mesh;
            owner.layer = gameObject.layer;
        }

        private static void DisposeUnityObject(Object obj)
        {
            if (obj == null) return;

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
                foreach (MeshCollider source in _sources)
                {
                    if (source != null && source.sharedMesh != null)
                    {
                        Gizmos.color = _sourceColor;
                        Gizmos.matrix = source.transform.localToWorldMatrix;
                        Gizmos.DrawWireMesh(source.sharedMesh);
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
