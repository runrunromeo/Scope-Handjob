using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ScopeHandjob
{
    /// <summary>Vertices + triangles of one mesh. FromBounds = the real shape could not be read, this is its box.</summary>
    internal sealed class MeshGeometry
    {
        internal Vector3[] Vertices;
        internal int[] Triangles;
        internal bool FromBounds;
    }

    /// <summary>
    /// Reads weapon meshes. The game's meshes are not CPU-readable, so the vertex and index buffers are
    /// copied back from the GPU once per mesh and cached. If that fails the mesh's bounding box is used
    /// (bigger than the part, so the hand keeps more distance, never less).
    /// </summary>
    internal static class MeshReader
    {
        private static readonly Dictionary<Mesh, MeshGeometry> Cache = new Dictionary<Mesh, MeshGeometry>();
        internal static int BoxFallbacks;

        internal static MeshGeometry Get(Mesh mesh)
        {
            if (mesh == null) return null;
            if (Cache.TryGetValue(mesh, out MeshGeometry g)) return g;
            if (Cache.Count > 600) Cache.Clear();
            g = TryRead(mesh);
            if (g == null)
            {
                BoxFallbacks++;
                g = Box(mesh.bounds);
            }
            Cache[mesh] = g;
            return g;
        }

        private static MeshGeometry TryRead(Mesh mesh)
        {
            try
            {
                if (mesh.isReadable) return new MeshGeometry { Vertices = mesh.vertices, Triangles = mesh.triangles };
                if (!mesh.HasVertexAttribute(VertexAttribute.Position)) return null;
                int count = mesh.vertexCount;
                if (count <= 0 || count > 300000) return null;

                int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
                int offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
                VertexAttributeFormat format = mesh.GetVertexAttributeFormat(VertexAttribute.Position);
                int stride = mesh.GetVertexBufferStride(stream);
                if (format != VertexAttributeFormat.Float32 && format != VertexAttributeFormat.Float16) return null;

                byte[] raw = new byte[stride * count];
                using (GraphicsBuffer vb = mesh.GetVertexBuffer(stream))
                {
                    if (vb == null) return null;
                    vb.GetData(raw);
                }
                var vertices = new Vector3[count];
                for (int i = 0; i < count; i++)
                {
                    int o = i * stride + offset;
                    vertices[i] = format == VertexAttributeFormat.Float32
                        ? new Vector3(BitConverter.ToSingle(raw, o), BitConverter.ToSingle(raw, o + 4), BitConverter.ToSingle(raw, o + 8))
                        : new Vector3(Mathf.HalfToFloat(BitConverter.ToUInt16(raw, o)), Mathf.HalfToFloat(BitConverter.ToUInt16(raw, o + 2)),
                            Mathf.HalfToFloat(BitConverter.ToUInt16(raw, o + 4)));
                }
                if (!MatchesBounds(vertices, mesh.bounds)) return null;

                bool wide = mesh.indexFormat == IndexFormat.UInt32;
                int total = 0;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    SubMeshDescriptor d = mesh.GetSubMesh(s);
                    total = Math.Max(total, d.indexStart + d.indexCount);
                }
                if (total <= 0) return null;
                byte[] indexRaw = new byte[total * (wide ? 4 : 2)];
                using (GraphicsBuffer ib = mesh.GetIndexBuffer())
                {
                    if (ib == null) return null;
                    ib.GetData(indexRaw);
                }
                var triangles = new List<int>(total);
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    SubMeshDescriptor d = mesh.GetSubMesh(s);
                    if (d.topology != MeshTopology.Triangles) continue;
                    int end = Math.Min(d.indexStart + d.indexCount, total);
                    for (int k = d.indexStart; k + 2 < end; k += 3)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            int at = k + c;
                            int index = (wide ? (int)BitConverter.ToUInt32(indexRaw, at * 4) : BitConverter.ToUInt16(indexRaw, at * 2)) + d.baseVertex;
                            if (index < 0 || index >= count) return null;
                            triangles.Add(index);
                        }
                    }
                }
                return new MeshGeometry { Vertices = vertices, Triangles = triangles.ToArray() };
            }
            catch (Exception error)
            {
                HandDriver.Log("mesh read failed for " + mesh.name + ": " + error.Message);
                return null;
            }
        }

        /// <summary>GPU data that does not fill the mesh's own bounds is not trusted.</summary>
        private static bool MatchesBounds(Vector3[] vertices, Bounds expected)
        {
            Vector3 min = vertices[0], max = vertices[0];
            foreach (Vector3 v in vertices)
            {
                if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)) return false;
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            float tolerance = 0.002f + expected.size.magnitude * 0.05f;
            return (min - expected.min).magnitude <= tolerance && (max - expected.max).magnitude <= tolerance;
        }

        internal static MeshGeometry Box(Bounds b)
        {
            Vector3 c = b.center, e = b.extents;
            var v = new Vector3[8];
            for (int i = 0; i < 8; i++)
                v[i] = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
            int[] t =
            {
                0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1,
                2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3,
            };
            return new MeshGeometry { Vertices = v, Triangles = t, FromBounds = true };
        }
    }

    /// <summary>A part's mesh vertices (game meshes are read back from the GPU), used to fit hinges.</summary>
    internal static partial class WeaponPoints
    {
        /// <summary>Vertices of one part's mesh (the part may be inactive), through remap.</summary>
        internal static Vector3[] Vertices(Transform part, Matrix4x4 remap, out bool exact)
        {
            exact = false;
            MeshFilter filter = part != null ? part.GetComponent<MeshFilter>() : null;
            if (filter == null) filter = part != null ? part.GetComponentInChildren<MeshFilter>(true) : null;
            if (filter == null || filter.sharedMesh == null) return null;
            MeshGeometry g = MeshReader.Get(filter.sharedMesh);
            exact = !g.FromBounds;
            Matrix4x4 m = remap * filter.transform.localToWorldMatrix;
            var w = new Vector3[g.Vertices.Length];
            for (int i = 0; i < w.Length; i++) w[i] = m.MultiplyPoint3x4(g.Vertices[i]);
            return w;
        }
    }
}
