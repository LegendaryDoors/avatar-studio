// An avatar's skeleton (joints, parents, bind pose), read from a bit stream.

using System;
using System.IO;
using Microsoft.Xna.Framework;

namespace AvatarStudio.Avatars
{
    public struct Pose
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
    }

    public struct Joint
    {
        public const int InvalidJointIndex = -1;
        public int Parent, Child, Sibling;
        public Vector3 BindPosition;
        public Quaternion BindRotation;
        public Pose Local;
    }

    public sealed class Skeleton
    {
        public enum SkeletonVersion { Invalid, Nxe, Natal }
        public const int AvatarMaxJoints = 72;
        public Joint[] Joints;

        public Skeleton Clone() { return new Skeleton { Joints = (Joint[])Joints.Clone() }; }

        /// <summary>The body skeleton of a version, from the console tables; null when not installed.</summary>
        public static Skeleton Body(SkeletonVersion v)
        {
            byte[] b = ConsoleTables.Get(v == SkeletonVersion.Natal ? "skeleton-natal" : "skeleton-nxe");
            return b == null ? null : Parse(new ByteCursor(b, true));
        }

        public static Skeleton Parse(ByteCursor c)
        {
            BitReader r = new BitReader(c);
            int n = r.I(32);
            Vector3Unpacker pos = new Vector3Unpacker(), rot = new Vector3Unpacker();
            pos.Header(r);
            rot.Header(r);
            if (n < 0 || n > 4096) throw new InvalidDataException("a skeleton with " + n + " joints");
            Joint[] j = new Joint[n];
            for (int i = 0; i < n; i++)
            {
                int parent = r.I(8);
                float x, y, z;
                pos.Next(r, out x, out y, out z);
                j[i].BindPosition = new Vector3(x, y, z);
                rot.Next(r, out x, out y, out z);
                j[i].BindRotation = AvatarMath.Exp(x, y, z);
                j[i].Parent = parent;
            }
            if (n > AvatarMaxJoints) throw new InvalidDataException("a skeleton over 72 joints");
            Children(j);
            Locals(j);
            return new Skeleton { Joints = j };
        }

        private static void Children(Joint[] j)
        {
            for (int i = 0; i < j.Length; i++) { j[i].Child = -1; j[i].Sibling = -1; }
            for (int i = j.Length - 1; i >= 0; i--)
            {
                int p = j[i].Parent;
                if (p == 255) continue;
                if (j[p].Child == -1) { j[p].Child = i; continue; }
                j[i].Sibling = j[p].Child;
                j[p].Child = i;
            }
        }

        private static void Locals(Joint[] j)
        {
            for (int i = j.Length - 1; i >= 1; i--)
            {
                Matrix a = AvatarMath.FromQuaternion(j[i].BindRotation);
                a.M41 = j[i].BindPosition.X; a.M42 = j[i].BindPosition.Y; a.M43 = j[i].BindPosition.Z;
                int p = j[i].Parent;
                Matrix b = AvatarMath.FromQuaternion(j[p].BindRotation);
                b.M41 = j[p].BindPosition.X; b.M42 = j[p].BindPosition.Y; b.M43 = j[p].BindPosition.Z;
                Matrix l = AvatarMath.Multiply(a, AvatarMath.InvertRigid(b));
                j[i].Local.position = new Vector3(l.M41, l.M42, l.M43);
                j[i].Local.rotation = AvatarMath.ToQuaternion(l);
                j[i].Local.scale = new Vector3(1f, 1f, 1f);
            }
            if (j.Length > 0)
            {
                j[0].Local.position = j[0].BindPosition;
                j[0].Local.rotation = j[0].BindRotation;
                j[0].Local.scale = new Vector3(1f, 1f, 1f);
            }
        }
    }

    public static class AvatarMath
    {
        /// <summary>The quaternion whose logarithm is (x, y, z); identity below 1e-7.</summary>
        public static Quaternion Exp(float x, float y, float z)
        {
            float a = (float)Math.Sqrt(x * x + y * y + z * z);
            if (a < 1E-07f) return new Quaternion(0f, 0f, 0f, 1f);
            float s = (float)Math.Sin(a), w = (float)Math.Cos(a), k = s / a;
            return new Quaternion(x * k, y * k, z * k, w);
        }

        public static Matrix FromQuaternion(Quaternion q)
        {
            float xx = q.X * q.X, xy = q.X * q.Y, xz = q.X * q.Z, xw = q.X * q.W, yy = q.Y * q.Y, yz = q.Y * q.Z, yw = q.Y * q.W, zz = q.Z * q.Z, zw = q.Z * q.W;
            Matrix m = new Matrix();
            m.M11 = 1f - 2f * (yy + zz); m.M12 = 2f * (xy + zw); m.M13 = 2f * (xz - yw); m.M14 = 0f;
            m.M21 = 2f * (xy - zw); m.M22 = 1f - 2f * (xx + zz); m.M23 = 2f * (yz + xw); m.M24 = 0f;
            m.M31 = 2f * (xz + yw); m.M32 = 2f * (yz - xw); m.M33 = 1f - 2f * (xx + yy); m.M34 = 0f;
            m.M41 = 0f; m.M42 = 0f; m.M43 = 0f; m.M44 = 1f;
            return m;
        }

        public static Matrix Multiply(Matrix a, Matrix b)
        {
            Matrix r = new Matrix();
            r.M11 = a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41;
            r.M12 = a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42;
            r.M13 = a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43;
            r.M14 = a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44;
            r.M21 = a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41;
            r.M22 = a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42;
            r.M23 = a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43;
            r.M24 = a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44;
            r.M31 = a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41;
            r.M32 = a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42;
            r.M33 = a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43;
            r.M34 = a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44;
            r.M41 = a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41;
            r.M42 = a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42;
            r.M43 = a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43;
            r.M44 = a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44;
            return r;
        }

        /// <summary>Inverse of a rotation-and-translation matrix.</summary>
        public static Matrix InvertRigid(Matrix m)
        {
            Matrix r = new Matrix();
            r.M11 = m.M11; r.M21 = m.M12; r.M31 = m.M13;
            r.M12 = m.M21; r.M22 = m.M22; r.M32 = m.M23;
            r.M13 = m.M31; r.M23 = m.M32; r.M33 = m.M33;
            r.M14 = 0f; r.M24 = 0f; r.M34 = 0f; r.M44 = 1f;
            r.M41 = 0f - (m.M41 * r.M11 + m.M42 * r.M21 + m.M43 * r.M31);
            r.M42 = 0f - (m.M41 * r.M12 + m.M42 * r.M22 + m.M43 * r.M32);
            r.M43 = 0f - (m.M41 * r.M13 + m.M42 * r.M23 + m.M43 * r.M33);
            return r;
        }

        /// <summary>A rotation matrix's quaternion: by the trace, else by the largest diagonal term.</summary>
        public static Quaternion ToQuaternion(Matrix m)
        {
            float t = m.M11 + m.M22 + m.M33 + 1f;
            Quaternion q = new Quaternion();
            if (t > 0.001f)
            {
                float s = 0.5f / (float)Math.Sqrt(t);
                q.W = 0.25f / s;
                q.X = (m.M23 - m.M32) * s; q.Y = (m.M31 - m.M13) * s; q.Z = (m.M12 - m.M21) * s;
            }
            else if (m.M11 > m.M22 && m.M11 > m.M33)
            {
                float s = (float)Math.Sqrt(1f + m.M11 - m.M22 - m.M33);
                q.X = 0.5f * s;
                s = 1f / (s + s);
                q.Y = (m.M12 + m.M21) * s; q.Z = (m.M13 + m.M31) * s; q.W = (m.M23 - m.M32) * s;
            }
            else if (m.M22 > m.M33)
            {
                float s = (float)Math.Sqrt(1.0 + (double)m.M22 - (double)m.M11 - (double)m.M33);
                q.Y = 0.5f * s;
                s = 1f / (s + s);
                q.X = (m.M12 + m.M21) * s; q.Z = (m.M23 + m.M32) * s; q.W = (m.M31 - m.M13) * s;
            }
            else
            {
                float s = (float)Math.Sqrt(1.0 + (double)m.M33 - (double)m.M11 - (double)m.M22);
                q.Z = 0.5f * s;
                s = 1f / (s + s);
                q.X = (m.M13 + m.M31) * s; q.Y = (m.M23 + m.M32) * s; q.W = (m.M12 - m.M21) * s;
            }
            return q;
        }
    }
}
