// An avatar built in the background, behind XNA's AvatarRenderer surface (State, BindPose, ParentBones).

using System;
using System.Collections.ObjectModel;
using System.Threading;
using Microsoft.Xna.Framework;

namespace AvatarStudio.Avatars
{
    public enum AvatarRendererState { Loading, Ready, Unavailable }

    /// <summary>The 1000-byte manifest, or the 1021-byte descriptor (0x01, manifest, 20 bytes).</summary>
    public sealed class AvatarDescription
    {
        public readonly byte[] Description;
        public AvatarDescription(byte[] d)
        {
            if (d == null) throw new ArgumentNullException("d");
            if (d.Length == 1021 && d[0] == 1) { Description = new byte[AvatarManifest.Size]; Buffer.BlockCopy(d, 1, Description, 0, AvatarManifest.Size); }
            else Description = d;
        }
        public bool IsValid { get { return AvatarManifest.Create(Description) != null; } }
    }

    public sealed class AvatarRenderer : IDisposable
    {
        private volatile AvatarRendererState state = AvatarRendererState.Loading;
        private Avatar avatar;
        private ReadOnlyCollection<Matrix> bind;
        public Exception Error { get; private set; }
        public bool IsDisposed { get; private set; }

        public AvatarRenderer(AvatarDescription d)
        {
            byte[] bytes = d.Description;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    AvatarManifest m = AvatarManifest.Create(bytes);
                    if (m == null) throw new ArgumentException("not an avatar manifest");
                    avatar = AvatarBuilder.Build(m);
                    state = AvatarRendererState.Ready;
                }
                catch (Exception e)
                {
                    Error = e;
                    Console.WriteLine("[engine] avatar unavailable: " + e.Message);
                    state = AvatarRendererState.Unavailable;
                }
            });
        }

        public AvatarRendererState State { get { return state; } }

        public Avatar Avatar { get { return state == AvatarRendererState.Ready ? avatar : null; } }

        /// <summary>Joint transforms (rotate, scale, translate); the root turned 180 degrees about Y, as in XNA.</summary>
        public ReadOnlyCollection<Matrix> BindPose
        {
            get
            {
                if (state != AvatarRendererState.Ready) throw new InvalidOperationException("Avatar is not ready");
                if (bind == null)
                {
                    Joint[] j = avatar.Skeleton.Joints;
                    Matrix[] m = new Matrix[j.Length];
                    for (int i = 0; i < j.Length; i++)
                    {
                        Matrix x = Matrix.CreateFromQuaternion(new Quaternion(j[i].Local.rotation.X, j[i].Local.rotation.Y, j[i].Local.rotation.Z, j[i].Local.rotation.W));
                        x = Matrix.Multiply(x, Matrix.CreateScale(new Vector3(j[i].Local.scale.X, j[i].Local.scale.Y, j[i].Local.scale.Z)));
                        x.M41 = j[i].Local.position.X; x.M42 = j[i].Local.position.Y; x.M43 = j[i].Local.position.Z;
                        m[i] = x;
                    }
                    m[0] = m[0] * Matrix.CreateRotationY((float)Math.PI);
                    bind = new ReadOnlyCollection<Matrix>(m);
                }
                return bind;
            }
        }

        /// <summary>Each joint's parent; the root's is -1.</summary>
        public ReadOnlyCollection<int> ParentBones
        {
            get
            {
                if (state != AvatarRendererState.Ready) throw new InvalidOperationException("Avatar is not ready");
                Joint[] j = avatar.Skeleton.Joints;
                int[] p = new int[j.Length];
                for (int i = 0; i < j.Length; i++) p[i] = j[i].Parent == 255 ? -1 : j[i].Parent;
                return new ReadOnlyCollection<int>(p);
            }
        }

        public void Dispose() { IsDisposed = true; }
    }

    /// <summary>XNA 4.0's preset animations, played from the console's stock animations of the same name.</summary>
    public enum AvatarAnimationPreset
    {
        Stand0, Stand1, Stand2, Stand3, Celebrate,
        MaleIdleLookAround, MaleIdleStretch, MaleIdleShiftWeight, MaleIdleCheckHand,
        FemaleIdleCheckNails, FemaleIdleLookAround, FemaleIdleShiftWeight, FemaleIdleFixShoe,
    }

    public static class Presets
    {
        // TOC entry index and body of each preset's animation
        private static readonly int[,] At =
        {
            { 0x03, 3 }, { 0x04, 3 }, { 0x05, 3 }, { 0x27, 3 }, { 0x08, 3 },
            { 0x13, 1 }, { 0x14, 1 }, { 0x15, 1 }, { 0x26, 1 },
            { 0x09, 2 }, { 0x0A, 2 }, { 0x0B, 2 }, { 0x0C, 2 },
        };

        public static Guid Asset(AvatarAnimationPreset p)
        {
            return new Guid(string.Format("00400000-{0:x4}-{1:x4}-c1c8-f109a19cb2e0", At[(int)p, 0], At[(int)p, 1]));
        }

        /// <summary>Posed at its start; null when the asset is missing.</summary>
        public static AvatarAnimation Create(AvatarAnimationPreset p)
        {
            byte[] b = AssetStore.Get(Asset(p));
            if (b == null) return null;
            try { return AvatarAnimation.Load(b); }
            catch (Exception e) { Console.WriteLine("[engine] preset " + p + ": " + e.Message); return null; }
        }
    }
}
