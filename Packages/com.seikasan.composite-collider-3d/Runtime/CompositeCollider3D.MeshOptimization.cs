using System;
using System.Collections.Generic;
using UnityEngine;

namespace CompositeCollider3D
{
    public sealed partial class CompositeCollider3D
    {
        private sealed class MeshEdge
        {
            public int FirstTriangle = -1;
            public int SecondTriangle = -1;
            public int Count;
            public int Orientation;
        }

        private static RawMesh OptimizeCollinearEdges(
            RawMesh source,
            out int originalVertexCount,
            out int optimizedVertexCount)
        {
            originalVertexCount = source.Positions.Length / 3;
            optimizedVertexCount = originalVertexCount;
            if (source.Indices.Length < 12 || originalVertexCount < 5)
            {
                return source;
            }

            RawMesh original = source;
            RawMesh current = source;
            float scale = GetMeshScale(source.Positions);
            float positionTolerance = Math.Max(scale * 1e-6f, 1e-7f);
            const int maxPasses = 32;

            for (int pass = 0; pass < maxPasses; pass++)
            {
                if (!TryBuildTopology(
                        current,
                        out Dictionary<ulong, MeshEdge> edges,
                        out List<int>[] neighbors,
                        out List<int>[] incidentTriangles,
                        out Vector3[] normals))
                {
                    optimizedVertexCount = originalVertexCount;
                    return original;
                }

                var reserved = new bool[neighbors.Length];
                var collapses = new Dictionary<int, int>();
                for (int vertex = 0; vertex < neighbors.Length; vertex++)
                {
                    if (reserved[vertex] ||
                        !TryFindCollinearFeatureEdge(
                            vertex,
                            current,
                            edges,
                            neighbors,
                            incidentTriangles,
                            normals,
                            positionTolerance,
                            out int first,
                            out int second))
                    {
                        continue;
                    }

                    int target = DistanceSquared(current.Positions, vertex, first) <=
                                 DistanceSquared(current.Positions, vertex, second) ? first : second;
                    collapses.Add(vertex, target);
                    reserved[vertex] = true;

                    foreach (int adjacent in neighbors[vertex])
                    {
                        reserved[adjacent] = true;
                    }
                }

                if (collapses.Count == 0) break;

                RawMesh next = CollapseVertices(current, collapses, positionTolerance);
                if (next == null || !TryBuildTopology(next, out _, out _, out _, out _))
                {
                    optimizedVertexCount = originalVertexCount;
                    return original;
                }

                current = next;
                optimizedVertexCount = current.Positions.Length / 3;
            }

            if (optimizedVertexCount == originalVertexCount ||
                !PreservesMeshGeometry(original, current, scale, positionTolerance))
            {
                optimizedVertexCount = originalVertexCount;
                return original;
            }

            return current;
        }

        private static bool TryFindCollinearFeatureEdge(
            int vertex,
            RawMesh mesh,
            Dictionary<ulong, MeshEdge> edges,
            List<int>[] neighbors,
            List<int>[] incidentTriangles,
            Vector3[] normals,
            float positionTolerance,
            out int first,
            out int second)
        {
            first = -1;
            second = -1;
            int featureCount = 0;

            foreach (int neighbor in neighbors[vertex])
            {
                MeshEdge edge = edges[EdgeKey(vertex, neighbor)];

                if (edge.Count != 2) return false;

                Vector3 firstNormal = normals[edge.FirstTriangle];
                Vector3 secondNormal = normals[edge.SecondTriangle];

                if (Vector3.Dot(firstNormal, secondNormal) < 0.99999f)
                {
                    featureCount++;

                    if (featureCount > 2) return false;

                    if (first < 0)
                    {
                        first = neighbor;
                    }
                    else
                    {
                        second = neighbor;
                    }
                }
            }

            if (featureCount != 2 || incidentTriangles[vertex].Count == 0) return false;

            Vector3 point = ReadPosition(mesh.Positions, vertex);
            Vector3 firstPoint = ReadPosition(mesh.Positions, first);
            Vector3 secondPoint = ReadPosition(mesh.Positions, second);
            Vector3 toFirst = firstPoint - point;
            Vector3 toSecond = secondPoint - point;
            float firstLength = toFirst.magnitude;
            float secondLength = toSecond.magnitude;

            if (firstLength <= positionTolerance || secondLength <= positionTolerance) return false;

            Vector3 firstDirection = toFirst / firstLength;
            Vector3 secondDirection = toSecond / secondLength;

            if (Vector3.Dot(firstDirection, secondDirection) > -0.99999f ||
                Vector3.Cross(firstDirection, secondDirection).sqrMagnitude > 1e-10f)
            {
                return false;
            }

            foreach (int triangle in incidentTriangles[vertex])
            {
                int index = triangle * 3;
                Vector3 origin = ReadPosition(mesh.Positions, mesh.Indices[index]);
                Vector3 normal = normals[triangle];

                if (Math.Abs(Vector3.Dot(normal, firstPoint - origin)) > positionTolerance ||
                    Math.Abs(Vector3.Dot(normal, secondPoint - origin)) > positionTolerance)
                {
                    return false;
                }
            }

            return true;
        }

        private static RawMesh CollapseVertices(
            RawMesh mesh,
            Dictionary<int, int> collapses,
            float positionTolerance)
        {
            var indices = new List<int>(mesh.Indices.Length);
            float areaToleranceSquared = positionTolerance * positionTolerance *
                                         positionTolerance * positionTolerance;

            for (int i = 0; i < mesh.Indices.Length; i += 3)
            {
                int oldAIndex = mesh.Indices[i];
                int oldBIndex = mesh.Indices[i + 1];
                int oldCIndex = mesh.Indices[i + 2];
                int a = ResolveCollapse(oldAIndex, collapses);
                int b = ResolveCollapse(oldBIndex, collapses);
                int c = ResolveCollapse(oldCIndex, collapses);

                if (a == b || b == c || c == a) continue;

                bool changed = a != oldAIndex || b != oldBIndex || c != oldCIndex;
                if (!changed)
                {
                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);
                    continue;
                }

                Vector3 pa = ReadPosition(mesh.Positions, a);
                Vector3 pb = ReadPosition(mesh.Positions, b);
                Vector3 pc = ReadPosition(mesh.Positions, c);
                Vector3 newNormal = Vector3.Cross(pb - pa, pc - pa);

                if (newNormal.sqrMagnitude <= areaToleranceSquared) continue;

                Vector3 oldA = ReadPosition(mesh.Positions, oldAIndex);
                Vector3 oldB = ReadPosition(mesh.Positions, oldBIndex);
                Vector3 oldC = ReadPosition(mesh.Positions, oldCIndex);
                Vector3 oldNormal = Vector3.Cross(oldB - oldA, oldC - oldA);

                if (Vector3.Dot(oldNormal, newNormal) <= 0f) return null;

                indices.Add(a);
                indices.Add(b);
                indices.Add(c);
            }

            if (indices.Count < 12) return null;

            var remap = new int[mesh.Positions.Length / 3];
            for (int i = 0; i < remap.Length; i++)
            {
                remap[i] = -1;
            }

            var compactPositions = new List<float>(mesh.Positions.Length);
            var compactIndices = new int[indices.Count];
            for (int i = 0; i < indices.Count; i++)
            {
                int oldIndex = indices[i];
                if (remap[oldIndex] >= 0)
                {
                    compactIndices[i] = remap[oldIndex];
                    continue;
                }

                int newIndex = compactPositions.Count / 3;
                remap[oldIndex] = newIndex;
                compactPositions.Add(mesh.Positions[oldIndex * 3]);
                compactPositions.Add(mesh.Positions[oldIndex * 3 + 1]);
                compactPositions.Add(mesh.Positions[oldIndex * 3 + 2]);
                compactIndices[i] = newIndex;
            }

            return new RawMesh
            {
                Positions = compactPositions.ToArray(),
                Indices = compactIndices
            };
        }

        private static bool TryBuildTopology(
            RawMesh mesh,
            out Dictionary<ulong, MeshEdge> edges,
            out List<int>[] neighbors,
            out List<int>[] incidentTriangles,
            out Vector3[] normals)
        {
            int vertexCount = mesh.Positions.Length / 3;
            int triangleCount = mesh.Indices.Length / 3;

            edges = new Dictionary<ulong, MeshEdge>(mesh.Indices.Length);
            neighbors = new List<int>[vertexCount];
            incidentTriangles = new List<int>[vertexCount];
            normals = new Vector3[triangleCount];

            for (int i = 0; i < vertexCount; i++)
            {
                neighbors[i] = new List<int>(6);
                incidentTriangles[i] = new List<int>(6);
            }

            for (int triangle = 0; triangle < triangleCount; triangle++)
            {
                int offset = triangle * 3;
                int a = mesh.Indices[offset];
                int b = mesh.Indices[offset + 1];
                int c = mesh.Indices[offset + 2];
                if (a < 0 || b < 0 || c < 0 || a >= vertexCount || b >= vertexCount ||
                    c >= vertexCount || a == b || b == c || c == a)
                {
                    return false;
                }

                Vector3 pa = ReadPosition(mesh.Positions, a);
                Vector3 pb = ReadPosition(mesh.Positions, b);
                Vector3 pc = ReadPosition(mesh.Positions, c);
                Vector3 cross = Vector3.Cross(pb - pa, pc - pa);

                if (cross.sqrMagnitude <= 1e-20f) return false;

                normals[triangle] = cross.normalized;
                incidentTriangles[a].Add(triangle);
                incidentTriangles[b].Add(triangle);
                incidentTriangles[c].Add(triangle);

                AddEdge(edges, neighbors, a, b, triangle);
                AddEdge(edges, neighbors, b, c, triangle);
                AddEdge(edges, neighbors, c, a, triangle);
            }

            foreach (MeshEdge edge in edges.Values)
            {
                if (edge.Count != 2 || edge.Orientation != 0) return false;
            }

            return true;
        }

        private static void AddEdge(
            Dictionary<ulong, MeshEdge> edges,
            List<int>[] neighbors,
            int start,
            int end,
            int triangle)
        {
            ulong key = EdgeKey(start, end);
            if (!edges.TryGetValue(key, out MeshEdge edge))
            {
                edge = new MeshEdge();
                edges.Add(key, edge);
                neighbors[start].Add(end);
                neighbors[end].Add(start);
            }

            edge.Orientation += start < end ? 1 : -1;

            if (edge.Count == 0)
            {
                edge.FirstTriangle = triangle;
            }
            else if (edge.Count == 1)
            {
                edge.SecondTriangle = triangle;
            }

            edge.Count++;
        }

        private static bool PreservesMeshGeometry(
            RawMesh original,
            RawMesh optimized,
            float scale,
            float positionTolerance)
        {
            MeasureMesh(original, out double originalArea, out double originalVolume);
            MeasureMesh(optimized, out double optimizedArea, out double optimizedVolume);

            double areaTolerance = Math.Max(
                originalArea * 1e-5,
                (double)positionTolerance * positionTolerance * original.Indices.Length);

            double volumeTolerance = Math.Max(
                Math.Abs(originalVolume) * 1e-5,
                (double)scale * scale * scale * 1e-8);

            return Math.Abs(originalArea - optimizedArea) <= areaTolerance &&
                   Math.Abs(originalVolume - optimizedVolume) <= volumeTolerance;
        }

        private static void MeasureMesh(RawMesh mesh, out double area, out double volume)
        {
            area = 0d;
            volume = 0d;
            for (int i = 0; i < mesh.Indices.Length; i += 3)
            {
                Vector3 a = ReadPosition(mesh.Positions, mesh.Indices[i]);
                Vector3 b = ReadPosition(mesh.Positions, mesh.Indices[i + 1]);
                Vector3 c = ReadPosition(mesh.Positions, mesh.Indices[i + 2]);
                Vector3 cross = Vector3.Cross(b - a, c - a);
                area += cross.magnitude * 0.5d;
                volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6d;
            }
        }

        private static int ResolveCollapse(int vertex, Dictionary<int, int> collapses) =>
            collapses.GetValueOrDefault(vertex, vertex);

        private static float DistanceSquared(float[] positions, int a, int b)
        {
            Vector3 delta = ReadPosition(positions, a) - ReadPosition(positions, b);
            return delta.sqrMagnitude;
        }

        private static Vector3 ReadPosition(float[] positions, int index) =>
            new(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]);

        private static float GetMeshScale(float[] positions)
        {
            Vector3 minimum = ReadPosition(positions, 0);
            Vector3 maximum = minimum;
            for (int i = 1; i < positions.Length / 3; i++)
            {
                Vector3 point = ReadPosition(positions, i);
                minimum = Vector3.Min(minimum, point);
                maximum = Vector3.Max(maximum, point);
            }

            return (maximum - minimum).magnitude;
        }

        private static ulong EdgeKey(int a, int b)
        {
            uint minimum = (uint)Math.Min(a, b);
            uint maximum = (uint)Math.Max(a, b);
            return ((ulong)minimum << 32) | maximum;
        }
    }
}
