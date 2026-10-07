using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace PilotHeim.Assets
{
    /// <summary>
    /// Runtime loader for PHM2 skinned meshes written by tools/tf2_export.py from the user's own
    /// Titanfall 2 install (already in Unity space: metres, Y up, left-handed, flipped winding).
    /// </summary>
    public sealed class PhModel
    {
        public string Name;
        public string[] BoneNames;
        public int[] Parents;
        public Vector3[] RestPos;
        public Quaternion[] RestRot;
        public Matrix4x4[] BindPoses;
        public readonly List<SubMesh> SubMeshes = new List<SubMesh>();

        public sealed class SubMesh
        {
            public string Material;
            public Vector3[] Pos, Nrm;
            public Vector2[] Uv;
            public BoneWeight[] Weights;
            public int[] Tris;
        }

        private static string Str(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
        private static Vector3 V3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        public static PhModel Load(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "PHM2") throw new InvalidDataException(path + ": not PHM2");
                var m = new PhModel { Name = Str(r) };
                int nb = r.ReadInt32();
                m.BoneNames = new string[nb]; m.Parents = new int[nb];
                m.RestPos = new Vector3[nb]; m.RestRot = new Quaternion[nb]; m.BindPoses = new Matrix4x4[nb];
                for (int i = 0; i < nb; i++)
                {
                    m.BoneNames[i] = Str(r);
                    m.Parents[i] = r.ReadInt32();
                    m.RestPos[i] = V3(r);
                    m.RestRot[i] = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    var bp = new Matrix4x4();
                    for (int k = 0; k < 16; k++) bp[k] = r.ReadSingle();     // column-major, same as Unity's indexer
                    m.BindPoses[i] = bp;
                }
                int ns = r.ReadInt32();
                for (int s = 0; s < ns; s++)
                {
                    var sm = new SubMesh { Material = Str(r) };
                    int nv = r.ReadInt32(), ni = r.ReadInt32();
                    sm.Pos = new Vector3[nv]; sm.Nrm = new Vector3[nv]; sm.Uv = new Vector2[nv]; sm.Weights = new BoneWeight[nv];
                    for (int v = 0; v < nv; v++)
                    {
                        sm.Pos[v] = V3(r); sm.Nrm[v] = V3(r);
                        sm.Uv[v] = new Vector2(r.ReadSingle(), r.ReadSingle());
                        byte b0 = r.ReadByte(), b1 = r.ReadByte(), b2 = r.ReadByte();
                        float w0 = r.ReadSingle(), w1 = r.ReadSingle(), w2 = r.ReadSingle();
                        sm.Weights[v] = new BoneWeight { boneIndex0 = b0, weight0 = w0, boneIndex1 = b1, weight1 = w1, boneIndex2 = b2, weight2 = w2 };
                    }
                    sm.Tris = new int[ni];
                    for (int k = 0; k < ni; k++) sm.Tris[k] = r.ReadInt32();
                    m.SubMeshes.Add(sm);
                }
                return m;
            }
        }

        /// <summary>Builds bones + SkinnedMeshRenderer under <paramref name="parent"/>. Returns the bone transforms.</summary>
        public Transform[] Build(Transform parent, Func<string, Material> material, out SkinnedMeshRenderer smr, ICollection<string> skipMaterials = null)
        {
            var root = new GameObject("PH_" + Path.GetFileNameWithoutExtension(Name.Replace('\\', '/'))).transform;
            root.SetParent(parent, false);
            int nb = BoneNames.Length;
            var bones = new Transform[nb];
            for (int i = 0; i < nb; i++) bones[i] = new GameObject(BoneNames[i]).transform;
            for (int i = 0; i < nb; i++)
            {
                bones[i].SetParent(Parents[i] >= 0 ? bones[Parents[i]] : root, false);
                bones[i].localPosition = RestPos[i];
                bones[i].localRotation = RestRot[i];
            }

            // one Unity mesh, one submesh per Titanfall material
            var verts = new List<Vector3>(); var nrms = new List<Vector3>(); var uvs = new List<Vector2>(); var bw = new List<BoneWeight>();
            var tris = new List<int[]>(); var mats = new List<Material>();
            foreach (var sm in SubMeshes)
            {
                if (skipMaterials != null && skipMaterials.Contains(sm.Material)) continue;
                int b = verts.Count;
                verts.AddRange(sm.Pos); nrms.AddRange(sm.Nrm); uvs.AddRange(sm.Uv); bw.AddRange(sm.Weights);
                var t = new int[sm.Tris.Length];
                for (int k = 0; k < t.Length; k++) t[k] = sm.Tris[k] + b;
                tris.Add(t);
                mats.Add(material(sm.Material));
            }
            var mesh = new Mesh { name = Name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts); mesh.SetNormals(nrms); mesh.SetUVs(0, uvs);
            mesh.boneWeights = bw.ToArray();
            mesh.bindposes = BindPoses;
            mesh.subMeshCount = tris.Count;
            for (int s = 0; s < tris.Count; s++) mesh.SetTriangles(tris[s], s, false);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            var go = new GameObject("mesh");
            go.transform.SetParent(root, false);
            smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = bones;
            smr.rootBone = bones[0];
            smr.sharedMaterials = mats.ToArray();
            smr.updateWhenOffscreen = false;
            var bounds = mesh.bounds; bounds.Expand(4f);
            smr.localBounds = new Bounds(bones[0].InverseTransformPoint(root.TransformPoint(bounds.center)), bounds.size * 1.5f);
            return bones;
        }
    }
}
