using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ManifoldNET;
using UnityEngine;
using UnityEngine.Rendering;
using Vector3 = UnityEngine.Vector3;

namespace CompositeCollider3D
{
    public sealed partial class CompositeCollider3D
    {
        private sealed class SourceSnapshot
        {
            public string Name;
            public float[] Positions;
            public uint[] Indices;
            public BooleanOperation Operation;
        }

        private sealed class GenerationSnapshot
        {
            public SourceSnapshot[] Sources;
            public bool Dynamic;
            public double CoacdThreshold;
            public int CoacdSampleResolution;
            public int CoacdMctsIteration;
        }

        private sealed class RawMesh
        {
            public float[] Positions;
            public int[] Indices;
        }

        private sealed class RawResult
        {
            public RawMesh Merged;
            public RawMesh[] Hulls;
            public long BooleanMilliseconds;
            public long OptimizationMilliseconds;
            public long DecompositionMilliseconds;
            public bool CoacdSkippedForConvexMesh;
            public int OriginalVertexCount;
            public int OriginalTriangleCount;
            public int OptimizedVertexCount;
        }

        private static readonly object NativeGenerationGate = new();

#if UNITY_EDITOR
        [NonSerialized] private Task<RawResult> _generationTask;
        [NonSerialized] private string _runningHash;
        [NonSerialized] private bool _retryAfterCurrent;
        [NonSerialized] private long _startedTicks;
        [NonSerialized] private long _captureMilliseconds;

        public bool IsGeometryGenerationRunning => _generationTask != null;

        public void RequestGeometryGeneration()
        {
            if (_generationTask != null)
            {
                _retryAfterCurrent = true;
                return;
            }

            string hash = null;
            try
            {
                _startedTicks = Stopwatch.GetTimestamp();
                hash = ComputeGenerationHash();

                var snapshot = CaptureSnapshot();
                _captureMilliseconds = ElapsedMilliseconds(_startedTicks);
                _runningHash = hash;
                _lastFailedHash = null;
                _generationTask = Task.Run(() => ComputeRaw(snapshot));
                ActiveNativeJobs.Register(_generationTask);
            }
            catch (Exception e)
            {
                _lastFailedHash = hash;
                UnityEngine.Debug.LogError($"CompositeCollider3D input capture failed: {e}", this);
            }
        }

        private void PollGeometryGeneration()
        {
            if (_generationTask is not { IsCompleted: true }) return;

            Task<RawResult> task = _generationTask;
            string completedHash = _runningHash;
            _generationTask = null;
            _runningHash = null;

            try
            {
                RawResult result = task.GetAwaiter().GetResult();
                bool current = completedHash == ComputeGenerationHash();

                if (!current)
                {
                    if (!_retryAfterCurrent) return;

                    _retryAfterCurrent = false;
                    RequestGeometryGeneration();
                    return;
                }

                Mesh merged = null;
                List<Mesh> hulls = null;

                try
                {
                    long applyStarted = Stopwatch.GetTimestamp();
                    merged = ToUnityMesh(result.Merged, "Composite3D Result");

                    if (result.Hulls != null)
                    {
                        hulls = new List<Mesh>(result.Hulls.Length);
                        for (int i = 0; i < result.Hulls.Length; i++)
                        {
                            hulls.Add(ToUnityMesh(result.Hulls[i], $"Convex {i}"));
                        }
                    }

                    CommitGeneratedMeshes(merged, hulls, completedHash);
                    RecordGeneration(result, ElapsedMilliseconds(_startedTicks), _captureMilliseconds,
                        ElapsedMilliseconds(applyStarted), merged, hulls);
                }
                catch
                {
                    if (merged != null && merged != _mergedMesh)
                    {
                        DisposeUnityObject(merged);
                    }

                    if (hulls == null) throw;

                    foreach (Mesh hull in hulls)
                    {
                        if (hull != null && !_generatedMeshes.Contains(hull))
                        {
                            DisposeUnityObject(hull);
                        }
                    }
                    throw;
                }
            }
            catch (Exception e)
            {
                _lastFailedHash = completedHash;
                UnityEngine.Debug.LogError($"CompositeCollider3D background generation failed: {e}", this);
            }

            if (!_retryAfterCurrent) return;

            _retryAfterCurrent = false;
            RequestGeometryGeneration();
        }
#endif

        private static long ElapsedMilliseconds(long startTicks) =>
            (Stopwatch.GetTimestamp() - startTicks) * 1000 / Stopwatch.Frequency;

        private static double SafeCoacdThreshold(float value)
        {
            if (float.IsNaN(value)) return 0.05d;
            return Math.Max(0.01d, Math.Min(1d, (double)value));
        }

        private void RecordGeneration(RawResult result, long totalMs, long captureMs, long applyMs,
            Mesh merged, List<Mesh> hulls)
        {
            _lastTotalMilliseconds = totalMs;
            _lastCaptureMilliseconds = captureMs;
            _lastBooleanMilliseconds = result.BooleanMilliseconds;
            _lastCoacdMilliseconds = result.DecompositionMilliseconds;
            _lastApplyMilliseconds = applyMs;
            UnityEngine.Debug.Log(
                $"CompositeCollider3D generation: total {totalMs} ms " +
                $"(capture {captureMs} ms, Boolean {result.BooleanMilliseconds} ms, " +
                $"mesh optimization {result.OptimizationMilliseconds} ms " +
                $"({result.OriginalVertexCount} to {result.OptimizedVertexCount} vertices, " +
                $"{result.OriginalTriangleCount} to {result.Merged.Indices.Length / 3} triangles), " +
                $"CoACD {result.DecompositionMilliseconds} ms" +
                $"{(result.CoacdSkippedForConvexMesh ? " (convex fast path)" : string.Empty)}, " +
                $"mesh/collider apply {applyMs} ms); " +
                $"result {merged.triangles.Length / 3} triangles, {hulls?.Count ?? 0} convex parts.", this);
        }

        private GenerationSnapshot CaptureSnapshot()
        {
            if (_sources == null || _sources.Length < 2)
            {
                throw new InvalidOperationException("At least two Collider sources are required.");
            }

            var sources = new SourceSnapshot[_sources.Length];

            for (int i = 0; i < sources.Length; i++)
            {
                Collider col = _sources[i];
                sources[i] = CaptureSource(col, i == 0 ? BooleanOperation.Merge : ResolveOperation(_sources[i]));
            }

            return new GenerationSnapshot
            {
                Sources = sources,
                Dynamic = _representation == CollisionRepresentation.DynamicConvex,
                CoacdThreshold = SafeCoacdThreshold(_coacdThreshold),
                CoacdSampleResolution = Mathf.Clamp(_coacdSampleResolution, 1000, 10000),
                CoacdMctsIteration = Mathf.Clamp(_coacdMctsIteration, 60, 2000)
            };
        }

        private SourceSnapshot CaptureSource(Collider source, BooleanOperation operation)
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
                    if (meshCollider.sharedMesh == null)
                    {
                        throw new InvalidOperationException($"{source.name}: MeshCollider has no mesh.");
                    }

                    vertices = meshCollider.sharedMesh.vertices;
                    triangles = meshCollider.sharedMesh.triangles;
                    matrix = transform.worldToLocalMatrix * source.transform.localToWorldMatrix;
                    break;
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
                    throw new InvalidOperationException($"{source.name}: unsupported Collider type {source.GetType().Name}.");
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

            return new SourceSnapshot
            {
                Name = source.name,
                Positions = positions,
                Indices = indices,
                Operation = operation
            };
        }

        private static RawResult ComputeRaw(GenerationSnapshot snapshot)
        {
            lock (NativeGenerationGate)
            {
                var timer = Stopwatch.StartNew();
                Manifold accumulated = null;

                try
                {
                    for (int i = 0; i < snapshot.Sources.Length; i++)
                    {
                        SourceSnapshot source = snapshot.Sources[i];
                        using var input = new MeshGL(source.Positions, source.Indices);
                        Manifold next = Manifold.Create(input);

                        if (next.Status != ManifoldError.NoError || next.IsEmpty)
                        {
                            ManifoldError error = next.Status;
                            next.Dispose();
                            throw new InvalidOperationException($"{source.Name}: invalid closed mesh ({error}).");
                        }

                        if (accumulated == null)
                        {
                            accumulated = next;
                            continue;
                        }

                        try
                        {
                            Manifold combined = source.Operation switch
                            {
                                BooleanOperation.Merge => accumulated + next,
                                BooleanOperation.Difference => accumulated - next,
                                BooleanOperation.Intersect => accumulated & next,
                                _ => throw new InvalidOperationException("Unsupported Boolean operation.")
                            };

                            accumulated.Dispose();
                            accumulated = combined;
                            if (combined.Status != ManifoldError.NoError || combined.IsEmpty)
                            {
                                throw new InvalidOperationException(
                                    $"Boolean operation {i} failed or produced an empty solid: {combined.Status}");
                            }
                        }
                        finally
                        {
                            next.Dispose();
                        }
                    }

                    if (accumulated == null)
                    {
                        throw new NullReferenceException("accumulated is null.");
                    }

                    float volume = accumulated.Properties.volume;

                    if (float.IsNaN(volume) || float.IsInfinity(volume) || volume <= 0f)
                    {
                        throw new InvalidOperationException($"Boolean result has invalid volume: {volume}.");
                    }

                    using MeshGL output = accumulated.MeshGL;
                    RawMesh merged = ToRawMesh(output);
                    long booleanMs = timer.ElapsedMilliseconds;
                    int originalTriangleCount = merged.Indices.Length / 3;
                    var optimizationTimer = Stopwatch.StartNew();

                    merged = OptimizeCollinearEdges(
                        merged,
                        out int originalVertexCount,
                        out int optimizedVertexCount);

                    long optimizationMs = optimizationTimer.ElapsedMilliseconds;
                    RawMesh[] hulls = null;
                    long decompositionMs = 0;
                    bool coacdSkippedForConvexMesh = false;

                    if (snapshot.Dynamic)
                    {
                        int triangleCount = merged.Indices.Length / 3;
                        float convexTolerance = Math.Max(GetMeshScale(merged.Positions) * 1e-6f, 1e-7f);
                        if (triangleCount <= 255 && IsConvexMesh(merged, convexTolerance))
                        {
                            hulls = new[] { merged };
                            coacdSkippedForConvexMesh = true;
                        }
                        else
                        {
                            long decompositionStarted = Stopwatch.GetTimestamp();
                            hulls = CoacdRaw.Decompose(merged, snapshot.CoacdThreshold,
                                snapshot.CoacdSampleResolution, snapshot.CoacdMctsIteration);
                            decompositionMs = ElapsedMilliseconds(decompositionStarted);
                        }

                        if (hulls.Length == 0)
                        {
                            throw new InvalidOperationException("CoACD produced no convex parts.");
                        }

                        foreach (RawMesh hull in hulls)
                        {
                            if (hull.Indices.Length == 0 || hull.Indices.Length / 3 > 255)
                            {
                                throw new InvalidOperationException("A CoACD part is empty or exceeds 255 triangles.");
                            }
                        }
                    }

                    return new RawResult
                    {
                        Merged = merged, Hulls = hulls, BooleanMilliseconds = booleanMs,
                        OptimizationMilliseconds = optimizationMs,
                        DecompositionMilliseconds = decompositionMs,
                        CoacdSkippedForConvexMesh = coacdSkippedForConvexMesh,
                        OriginalVertexCount = originalVertexCount,
                        OriginalTriangleCount = originalTriangleCount,
                        OptimizedVertexCount = optimizedVertexCount
                    };
                }
                finally
                {
                    accumulated?.Dispose();
                }
            }
        }

        private static RawMesh ToRawMesh(MeshGL output)
        {
            float[] properties = output.VerticesProperties;
            int[] indices = output.TriangleVertices;
            int stride = output.PropertiesNumber;

            if (properties == null || indices == null || stride < 3)
            {
                throw new InvalidOperationException("Manifold returned invalid mesh data.");
            }

            var positions = new float[output.VerticesNumber * 3];

            for (int i = 0; i < output.VerticesNumber; i++)
            {
                positions[i * 3] = properties[i * stride];
                positions[i * 3 + 1] = properties[i * stride + 1];
                positions[i * 3 + 2] = properties[i * stride + 2];
            }

            return new RawMesh
            {
                Positions = positions,
                Indices = indices
            };
        }

        private static Mesh ToUnityMesh(RawMesh data, string name)
        {
            var vertices = new Vector3[data.Positions.Length / 3];

            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = new Vector3(
                    data.Positions[i * 3],
                    data.Positions[i * 3 + 1],
                    data.Positions[i * 3 + 2]);
            }

            var mesh = new Mesh
            {
                name = name,
                indexFormat = vertices.Length > 65535
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16,
                vertices = vertices,
                triangles = data.Indices
            };

            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        private static class CoacdRaw
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct NativeMesh
            {
                public IntPtr Vertices;
                public ulong VertexCount;
                public IntPtr Triangles;
                public ulong TriangleCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct NativeMeshArray
            {
                public IntPtr Meshes;
                public ulong Count;
            }

            [DllImport("lib_coacd", CallingConvention = CallingConvention.Cdecl, EntryPoint = "CoACD_run")]
            private static extern NativeMeshArray Run(ref NativeMesh mesh, double threshold, int maxConvexHull,
                int preprocessMode, int preprocessResolution, int sampleResolution, int mctsNodes,
                int mctsIteration, int mctsMaxDepth, bool pca, bool merge, uint seed);

            [DllImport("lib_coacd", CallingConvention = CallingConvention.Cdecl, EntryPoint = "CoACD_freeMeshArray")]
            private static extern void Free(NativeMeshArray array);

            public static RawMesh[] Decompose(RawMesh mesh, double threshold, int sampleResolution, int mctsIteration)
            {
                if (double.IsNaN(threshold) || double.IsInfinity(threshold) || threshold < 0.01d || threshold > 1d ||
                    sampleResolution < 1000 || sampleResolution > 10000 ||
                    mctsIteration < 60 || mctsIteration > 2000)
                {
                    throw new ArgumentOutOfRangeException(nameof(threshold), "CoACD parameters are outside supported bounds.");
                }

                var doubles = new double[mesh.Positions.Length];

                for (int i = 0; i < doubles.Length; i++)
                {
                    doubles[i] = mesh.Positions[i];
                }

                var vertices = GCHandle.Alloc(doubles, GCHandleType.Pinned);
                var triangles = GCHandle.Alloc(mesh.Indices, GCHandleType.Pinned);

                try
                {
                    var input = new NativeMesh
                    {
                        Vertices = vertices.AddrOfPinnedObject(),
                        VertexCount = (ulong)doubles.Length / 3,
                        Triangles = triangles.AddrOfPinnedObject(),
                        TriangleCount = (ulong)mesh.Indices.Length / 3
                    };

                    NativeMeshArray output = Run(
                        ref input,
                        threshold: threshold,
                        maxConvexHull: -1,
                        preprocessMode: 0,
                        preprocessResolution: 50,
                        sampleResolution: sampleResolution,
                        mctsNodes: 20,
                        mctsIteration: mctsIteration,
                        mctsMaxDepth: 3,
                        pca: false,
                        merge: true,
                        seed: 0);

                    try
                    {
                        if (output.Count > int.MaxValue || (output.Count > 0 && output.Meshes == IntPtr.Zero))
                        {
                            throw new InvalidOperationException("CoACD returned invalid mesh count.");
                        }

                        var result = new RawMesh[(int)output.Count];
                        int stride = Marshal.SizeOf<NativeMesh>();

                        for (int i = 0; i < result.Length; i++)
                        {
                            NativeMesh part = Marshal.PtrToStructure<NativeMesh>(IntPtr.Add(output.Meshes, i * stride));

                            if (part.VertexCount > int.MaxValue / 3 || part.TriangleCount > int.MaxValue / 3)
                            {
                                throw new InvalidOperationException("CoACD part is too large.");
                            }

                            var coordinates = new double[(int)part.VertexCount * 3];
                            var indices = new int[(int)part.TriangleCount * 3];

                            Marshal.Copy(part.Vertices, coordinates, 0, coordinates.Length);
                            Marshal.Copy(part.Triangles, indices, 0, indices.Length);

                            var positions = new float[coordinates.Length];
                            for (int j = 0; j < positions.Length; j++)
                            {
                                positions[j] = (float)coordinates[j];
                            }

                            result[i] = new RawMesh
                            {
                                Positions = positions,
                                Indices = indices
                            };
                        }

                        return result;
                    }
                    finally
                    {
                        if (output.Meshes != IntPtr.Zero)
                        {
                            Free(output);
                        }
                    }
                }
                finally
                {
                    triangles.Free();
                    vertices.Free();
                }
            }
        }
    }

#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
    internal static class ActiveNativeJobs
    {
        private static readonly List<Task> Jobs = new();

        static ActiveNativeJobs() => UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += WaitForJobs;

        internal static void Register(Task task)
        {
            lock (Jobs)
            {
                Jobs.Add(task);
            }

            task.ContinueWith(_ =>
            {
                lock (Jobs)
                {
                    Jobs.Remove(task);
                }
            });
        }

        private static void WaitForJobs()
        {
            Task[] current;
            lock (Jobs)
            {
                current = Jobs.ToArray();
            }

            try
            {
                Task.WaitAll(current);
            }
            catch (AggregateException)
            {
                 // The owning component reports failures when it next updates.
            }
        }
    }
#endif
}
