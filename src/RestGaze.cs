// The editor's resting pose: a blend of nine stock animations by where the avatar looks.

using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using AvatarStudio.Avatars;

namespace AvatarStudio
{
    sealed class RestGaze : IDisposable
    {
        // the object's slots +12..+44 in order, GUID indices (body 3)
        private static readonly int[] Index = { 0x22, 0x1F, 0x24, 0x1C, 0x1E, 0x20, 0x21, 0x1D, 0x23 };
        private const int DownRight = 0, Right = 1, UpRight = 2, Down = 3, Neutral = 4, Up = 5, DownLeft = 6, Left = 7, UpLeft = 8;

        private readonly AvatarAnimation[] anims = new AvatarAnimation[9];
        private readonly AnimClip[] clips = new AnimClip[9];       // played as the console plays them
        private readonly Pose[][] buffers = new Pose[9][];

        private RestGaze() { }

        /// <summary>The nine, or null when any is missing (the rest then uses id 2 alone).</summary>
        public static RestGaze TryLoad()
        {
            RestGaze g = new RestGaze();
            for (int k = 0; k < 9; k++)
            {
                g.anims[k] = EditorAnimations.CreateStock(Index[k], 3);
                g.clips[k] = AnimClip.Of(g.anims[k]);
                if (g.clips[k] == null) { g.Dispose(); return null; }
            }
            return g;
        }

        /// <summary>Animation k's joints at the cursor.</summary>
        private Pose[] PoseAt(int k, float t)
        {
            AnimClip c = clips[k];
            c.Time = t;
            if (buffers[k] == null || buffers[k].Length != c.Joints) buffers[k] = new Pose[c.Joints];
            c.Sample(buffers[k]);
            return buffers[k];
        }

        /// <summary>The rest's joint poses at cursor `t` for the gaze.</summary>
        public void EvaluatePoses(float t, Vector2 gaze, Pose[] into)
        {
            float x = gaze.X, y = gaze.Y;
            float r = (float)Math.Sqrt(x * x + y * y);
            if (r > 1f) { x /= r; y /= r; }
            int vert = y >= 0f ? Up : Down;
            int horiz = x >= 0f ? Left : Right;
            int corner = x >= 0f ? (y >= 0f ? UpLeft : DownLeft) : (y >= 0f ? UpRight : DownRight);
            x = Math.Abs(x); y = Math.Abs(y);

            int n = into.Length;
            Pose[] b1 = Copy(PoseAt(Neutral, t), n), b2 = Copy(PoseAt(horiz, t), n);
            if (y > 0f) { Blend(b1, PoseAt(vert, t), y); Blend(b2, PoseAt(corner, t), y); }
            for (int i = 0; i < n; i++) into[i] = Mix(b1[i], b2[i], x);
        }

        /// <summary>A joint's matrix: rotation, then scale, then translation.</summary>
        public static Matrix Compose(Pose p)
        {
            Matrix m = Matrix.CreateFromQuaternion(new Quaternion(p.rotation.X, p.rotation.Y, p.rotation.Z, p.rotation.W))
                     * Matrix.CreateScale(p.scale.X, p.scale.Y, p.scale.Z);
            m.M41 = p.position.X; m.M42 = p.position.Y; m.M43 = p.position.Z;
            return m;
        }

        /// <summary>The nine's frames and rates, each distinct pair once.</summary>
        public string Shape
        {
            get
            {
                List<string> s = new List<string>();
                foreach (AnimClip c in clips)
                {
                    string p = c.Frames + " frames at " + c.Rate.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                    if (!s.Contains(p)) s.Add(p);
                }
                return string.Join(", ", s.ToArray());
            }
        }

        public AvatarExpression Expression { get { return clips[Neutral].Expression; } }

        private static Pose[] Copy(Pose[] src, int n)
        {
            Pose[] p = new Pose[n];
            Array.Copy(src, p, Math.Min(n, src.Length));
            return p;
        }

        private static void Blend(Pose[] into, Pose[] with, float w)
        {
            for (int i = 0; i < into.Length && i < with.Length; i++) into[i] = Mix(into[i], with[i], w);
        }

        /// <summary>Position and scale lerped, rotation slerped on the short arc (sub_922792E8).</summary>
        public static Pose Mix(Pose a, Pose b, float t)
        {
            if (t <= 0f) return a;
            Pose o = a;
            o.position.X = a.position.X + (b.position.X - a.position.X) * t;
            o.position.Y = a.position.Y + (b.position.Y - a.position.Y) * t;
            o.position.Z = a.position.Z + (b.position.Z - a.position.Z) * t;
            o.scale.X = a.scale.X + (b.scale.X - a.scale.X) * t;
            o.scale.Y = a.scale.Y + (b.scale.Y - a.scale.Y) * t;
            o.scale.Z = a.scale.Z + (b.scale.Z - a.scale.Z) * t;
            Quaternion q = Quaternion.Slerp(new Quaternion(a.rotation.X, a.rotation.Y, a.rotation.Z, a.rotation.W),
                                            new Quaternion(b.rotation.X, b.rotation.Y, b.rotation.Z, b.rotation.W), t);
            o.rotation.X = q.X; o.rotation.Y = q.Y; o.rotation.Z = q.Z; o.rotation.W = q.W;
            return o;
        }

        public void Dispose() { foreach (AvatarAnimation a in anims) if (a != null) a.Dispose(); }
    }
}
