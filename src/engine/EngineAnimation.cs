// Avatar animations: an STRB animation's keyframes and face, behind XNA 4.0's AvatarAnimation.

using System;
using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Xna.Framework;

namespace AvatarStudio.Avatars
{
    /// <summary>The face's layers in one frame.</summary>
    public struct FaceLayers { public int MouthLayer, LeftEyebrowLayer, RightEyebrowLayer, LeftEyeLayer, RightEyeLayer; }

    public enum AnimationPlayMode { Once, Loop, Bounce }

    public sealed class KeyframeAnimation
    {
        public readonly Pose[][] AvatarKeyframes, CarryableKeyframes;
        public readonly FaceLayers[] Facial;
        public readonly float Fps, Length;
        public readonly int FrameCount;
        public readonly AvatarGender Body;

        public KeyframeAnimation(Pose[][] body, Pose[][] carryable, FaceLayers[] face, float fps, AvatarGender bodyMask)
        {
            AvatarKeyframes = body; CarryableKeyframes = carryable; Facial = face; Fps = fps; Body = bodyMask;
            FrameCount = body.Length;
            Length = (float)FrameCount / fps;
        }

        public int AvatarJointsCount { get { return AvatarKeyframes == null ? 0 : AvatarKeyframes[0].Length; } }
        public int CarryableJointsCount { get { return CarryableKeyframes == null ? 0 : CarryableKeyframes[0].Length; } }

        // ---- reading -----------------------------------------------------------------------------------

        /// <summary>Throws when `strb` is not an animation asset.</summary>
        public static KeyframeAnimation Load(byte[] strb)
        {
            StrbFile f = StrbFile.Read(strb);
            if (f == null) throw new InvalidDataException("not an animation asset");
            AssetMetadata meta = AssetMetadata.Read(f);
            if (meta == null) throw new InvalidDataException("an animation asset without metadata");
            StrbBlock? anim = null;
            foreach (StrbBlock k in f.Blocks) if (k.Id == (int)StrbBlockId.Animation) { anim = k; break; }
            if (anim == null) throw new InvalidDataException("an animation asset without an animation block");
            return Parse(new ByteCursor(f.Data, true) { At = anim.Value.Offset }, meta.BodyTypeMask);
        }

        public static KeyframeAnimation Parse(ByteCursor c, AvatarGender body)
        {
            BitReader r = new BitReader(c);
            r.I(32);
            float fps = r.F();
            r.I(32); r.I(32); r.I(32); r.I(32);
            int secondary = r.I(32), face = r.I(32);
            r.I(32);
            int size = r.I(32);
            byte[] packed = c.Bytes(size);
            Pose[][] bodyKeys = Keyframes(new ByteCursor(packed, true));
            Pose[][] carry = Keyframes(new ByteCursor(packed, true) { At = secondary });
            if (carry.Length > 0 && carry[0].Length == 0) carry = null;
            else if (carry.Length == 0) throw new IndexOutOfRangeException("a carryable part with no frames");
            return new KeyframeAnimation(bodyKeys, carry, Faces(new ByteCursor(packed, true) { At = face }), fps, body);
        }

        private static Pose[][] Keyframes(ByteCursor c)
        {
            BitReader r = new BitReader(c);
            int frames = r.I(32), joints = r.I(32);
            Vector3Unpacker[] pos = new Vector3Unpacker[joints], rot = new Vector3Unpacker[joints], scl = new Vector3Unpacker[joints];
            for (int j = joints - 1; j >= 0; j--)
            {
                pos[j] = new Vector3Unpacker(); rot[j] = new Vector3Unpacker(); scl[j] = new Vector3Unpacker();
                pos[j].Header(r); rot[j].Header(r); scl[j].Header(r);
            }
            for (int j = joints; j < 72; j++)                    // the unused joints' headers, read and dropped
            {
                new Vector3Unpacker().Header(r); new Vector3Unpacker().Header(r); new Vector3Unpacker().Header(r);
            }
            Pose[][] k = new Pose[frames][];
            for (int f = 0; f < frames; f++)
            {
                k[f] = new Pose[joints];
                for (int j = 0; j < joints; j++)
                {
                    float x, y, z;
                    pos[j].Next(r, out x, out y, out z); k[f][j].position = new Vector3(x, y, z);
                    rot[j].Next(r, out x, out y, out z); k[f][j].rotation = AvatarMath.Exp(x, y, z);
                    scl[j].Next(r, out x, out y, out z); k[f][j].scale = new Vector3(x, y, z);
                }
            }
            return k;
        }

        private static FaceLayers[] Faces(ByteCursor c)
        {
            BitReader r = new BitReader(c);
            int frames = r.I(32);
            if (r.I(32) != 5) throw new InvalidDataException("a face track without five layers");
            IntUnpacker rightEye = new IntUnpacker(), leftEye = new IntUnpacker(), rightBrow = new IntUnpacker(), leftBrow = new IntUnpacker(), mouth = new IntUnpacker();
            rightEye.Header(r); leftEye.Header(r); rightBrow.Header(r); leftBrow.Header(r); mouth.Header(r);
            FaceLayers[] l = new FaceLayers[frames];
            for (int i = 0; i < frames; i++)
            {
                l[i].MouthLayer = mouth.Next(r);
                if (l[i].MouthLayer > 13) throw new InvalidDataException("a mouth layer over 13");
                l[i].LeftEyebrowLayer = leftBrow.Next(r);
                if (l[i].LeftEyebrowLayer > 4) throw new InvalidDataException("an eyebrow layer over 4");
                l[i].RightEyebrowLayer = rightBrow.Next(r);
                if (l[i].RightEyebrowLayer > 4) throw new InvalidDataException("an eyebrow layer over 4");
                l[i].LeftEyeLayer = leftEye.Next(r);
                if (l[i].LeftEyeLayer > 13) l[i].LeftEyeLayer = 12;
                l[i].RightEyeLayer = rightEye.Next(r);
                if (l[i].RightEyeLayer > 13) l[i].RightEyeLayer = 12;
            }
            return l;
        }

        // ---- sampling ----------------------------------------------------------------------------------

        public void GetAvatarPose(float time, AnimationPlayMode mode, float weight, Pose[] buffer) { GetSkeletonPose(AvatarKeyframes, time, mode, weight, buffer); }

        public bool GetCarryablePose(float time, AnimationPlayMode mode, float weight, Pose[] buffer)
        {
            if (CarryableKeyframes == null) return false;
            GetSkeletonPose(CarryableKeyframes, time, mode, weight, buffer);
            return true;
        }

        public void GetSkeletonPose(Pose[][] keys, float time, AnimationPlayMode mode, float weight, Pose[] buffer)
        {
            float f = time * Fps;
            int a = (int)Math.Floor(f), b;
            float tb = f - (float)a, ta = 1f - tb, rest = 1f - weight;
            if (mode == AnimationPlayMode.Once) { a = Math.Min(a, FrameCount - 1); b = Math.Min(a + 1, FrameCount - 1); }
            else if (mode == AnimationPlayMode.Loop) { a %= FrameCount; b = (a + 1) % FrameCount; }
            else
            {
                if (((a / FrameCount) & 1) == 0) { a %= FrameCount; b = Math.Min(a + 1, FrameCount - 1); }
                else { a = FrameCount - 1 - a % FrameCount; b = Math.Max(a - 1, 0); }
            }
            for (int i = 0; i < buffer.Length && i < keys[a].Length; i++)
            {
                Pose pa = keys[a][i], pb = keys[b][i];
                Vector3 p = new Vector3(ta * pa.position.X + tb * pb.position.X, ta * pa.position.Y + tb * pb.position.Y, ta * pa.position.Z + tb * pb.position.Z);
                Vector3 s = new Vector3(ta * pa.scale.X + tb * pb.scale.X, ta * pa.scale.Y + tb * pb.scale.Y, ta * pa.scale.Z + tb * pb.scale.Z);
                Quaternion q = Blend(pa.rotation, pb.rotation, ta, tb);
                if (weight == 1f) { buffer[i].position = p; buffer[i].scale = s; buffer[i].rotation = q; continue; }
                Pose o = buffer[i];
                buffer[i].position = new Vector3(weight * p.X + rest * o.position.X, weight * p.Y + rest * o.position.Y, weight * p.Z + rest * o.position.Z);
                buffer[i].scale = new Vector3(weight * s.X + rest * o.scale.X, weight * s.Y + rest * o.scale.Y, weight * s.Z + rest * o.scale.Z);
                buffer[i].rotation = Blend(q, o.rotation, weight, rest);
            }
        }

        /// <summary>u a + v b along the shorter arc, renormalised.</summary>
        private static Quaternion Blend(Quaternion a, Quaternion b, float u, float v)
        {
            Quaternion q;
            if (a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W >= 0f)
                q = new Quaternion(u * a.X + v * b.X, u * a.Y + v * b.Y, u * a.Z + v * b.Z, u * a.W + v * b.W);
            else
                q = new Quaternion(u * a.X - v * b.X, u * a.Y - v * b.Y, u * a.Z - v * b.Z, u * a.W - v * b.W);
            float n = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
            if (n > 0f)
            {
                n = (float)(1.0 / Math.Sqrt(n));
                q.X *= n; q.Y *= n; q.Z *= n; q.W *= n;
            }
            return q;
        }

        /// <summary>The face at a time: the frame under time x fps, looped or bounced.</summary>
        public FaceLayers GetFace(float time, AnimationPlayMode mode)
        {
            float t;
            if (mode == AnimationPlayMode.Once) t = time;
            else
            {
                t = time % Length;
                if (mode == AnimationPlayMode.Bounce && (((int)(time / Length)) & 1) == 1) t = Length - t;
            }
            int f = (int)(t * Fps);
            if (f >= FrameCount) f = FrameCount - 1;
            return Facial[f];
        }
    }

    /// <summary>An asset's metadata (block 6, or 8 with a version byte).</summary>
    public sealed class AssetMetadata
    {
        public AvatarGender BodyTypeMask;
        public BinaryAssetType AssetType;
        public uint AssetTypeDetails;
        public int Subcategory;
        public Skeleton.SkeletonVersion SkeletonVersion = Skeleton.SkeletonVersion.Nxe;

        public static AssetMetadata Read(StrbFile f)
        {
            foreach (StrbBlock k in f.Blocks)
                if (k.Id == (int)StrbBlockId.AssetMetadata) return Fields(new ByteCursor(f.Data, true) { At = k.Offset }, false);
            foreach (StrbBlock k in f.Blocks)
                if (k.Id == (int)StrbBlockId.AssetMetadataVersioned)
                {
                    ByteCursor c = new ByteCursor(f.Data, true) { At = k.Offset };
                    int v = c.ReadByte();
                    if (v == 1) return Fields(c, false);
                    if (v == 2) return Fields(c, true);
                    return new AssetMetadata { SkeletonVersion = Skeleton.SkeletonVersion.Invalid };
                }
            return null;
        }

        private static AssetMetadata Fields(ByteCursor c, bool versioned)
        {
            AssetMetadata m = new AssetMetadata();
            m.BodyTypeMask = (AvatarGender)c.ReadByte();
            m.AssetType = (BinaryAssetType)c.I32();
            m.AssetTypeDetails = (uint)c.I32();
            m.Subcategory = c.I32();
            if (versioned) m.SkeletonVersion = (Skeleton.SkeletonVersion)c.ReadByte();
            return m;
        }
    }

    public enum AvatarEye { Neutral, Sad, Angry, Confused, Laughing, Shocked, Happy, Yawning, Sleeping, LookUp, LookDown, LookLeft, LookRight, Blink }
    public enum AvatarEyebrow { Neutral, Sad, Angry, Confused, Raised }
    public enum AvatarMouth { Neutral, Sad, Angry, Confused, Laughing, Shocked, Happy, PhoneticO, PhoneticAi, PhoneticEe, PhoneticFv, PhoneticW, PhoneticL, PhoneticDth }

    /// <summary>XNA 4.0's facial expression; each part checked on set.</summary>
    public struct AvatarExpression
    {
        private AvatarEye leftEye, rightEye;
        private AvatarEyebrow leftEyebrow, rightEyebrow;
        private AvatarMouth mouth;

        public AvatarEye LeftEye { get { return leftEye; } set { if (value < AvatarEye.Neutral || value > AvatarEye.Blink) throw new ArgumentException("Incorrect LeftEye value"); leftEye = value; } }
        public AvatarEye RightEye { get { return rightEye; } set { if (value < AvatarEye.Neutral || value > AvatarEye.Blink) throw new ArgumentException("Incorrect RightEye value"); rightEye = value; } }
        public AvatarEyebrow LeftEyebrow { get { return leftEyebrow; } set { if (value < AvatarEyebrow.Neutral || value > AvatarEyebrow.Raised) throw new ArgumentException("Incorrect LeftEyebrow value"); leftEyebrow = value; } }
        public AvatarEyebrow RightEyebrow { get { return rightEyebrow; } set { if (value < AvatarEyebrow.Neutral || value > AvatarEyebrow.Raised) throw new ArgumentException("Incorrect RightEyebrow value"); rightEyebrow = value; } }
        public AvatarMouth Mouth { get { return mouth; } set { if (value < AvatarMouth.Neutral || value > AvatarMouth.PhoneticDth) throw new ArgumentException("Incorrect Mouth value"); mouth = value; } }
    }

    /// <summary>XNA 4.0's AvatarAnimation over keyframes: play position, bone matrices, face.
    /// The asset's left eye is the viewer's right: LookLeft and LookRight swap on it.</summary>
    public sealed class AvatarAnimation : IDisposable
    {
        public readonly KeyframeAnimation Clip;
        private readonly Pose[] poses;
        private readonly Matrix[] bones;
        private readonly ReadOnlyCollection<Matrix> boneView;
        private float cursor;
        private TimeSpan position, length;
        public bool IsDisposed { get; private set; }

        private AvatarAnimation(KeyframeAnimation clip)
        {
            Clip = clip;
            length = TimeSpan.FromMilliseconds(clip.Length * 1000f);
            poses = new Pose[clip.AvatarJointsCount];
            bones = new Matrix[clip.AvatarJointsCount];
            boneView = new ReadOnlyCollection<Matrix>(bones);
        }

        /// <summary>Posed at its start, as XNA's AvatarResources.LoadAnimation.</summary>
        public static AvatarAnimation Load(byte[] strb)
        {
            AvatarAnimation a = new AvatarAnimation(KeyframeAnimation.Load(strb));
            a.Update(TimeSpan.Zero, false);
            return a;
        }

        public static AvatarAnimation FromClip(KeyframeAnimation clip) { return new AvatarAnimation(clip); }

        public ReadOnlyCollection<Matrix> BoneTransforms { get { return boneView; } }
        public TimeSpan Length { get { return length; } }

        public TimeSpan CurrentPosition
        {
            get { return position; }
            set { position = value; cursor = 0f; Update(value, false); }
        }

        public AvatarExpression Expression
        {
            get
            {
                AvatarExpression e = new AvatarExpression();
                FaceLayers f = Clip.GetFace(cursor, AnimationPlayMode.Once);
                AvatarEye left = (AvatarEye)f.LeftEyeLayer;
                e.LeftEye = left == AvatarEye.LookLeft ? AvatarEye.LookRight : left == AvatarEye.LookRight ? AvatarEye.LookLeft : left;
                e.RightEye = (AvatarEye)f.RightEyeLayer;
                e.LeftEyebrow = (AvatarEyebrow)f.LeftEyebrowLayer;
                e.RightEyebrow = (AvatarEyebrow)f.RightEyebrowLayer;
                e.Mouth = (AvatarMouth)f.MouthLayer;
                return e;
            }
        }

        public void Update(TimeSpan elapsed, bool loop)
        {
            if (IsDisposed) throw new ObjectDisposedException("AvatarAnimation");
            cursor += (float)elapsed.TotalSeconds;
            if ((double)(cursor * 1000f) >= length.TotalMilliseconds) cursor = loop ? cursor % (float)length.TotalSeconds : (float)length.TotalSeconds;
            else if (cursor < 0f) cursor = 0f;
            Pose();
        }

        private void Pose()
        {
            position = TimeSpan.FromSeconds(cursor);
            Clip.GetAvatarPose(cursor, AnimationPlayMode.Once, 1f, poses);
            for (int i = 0; i < bones.Length; i++)
            {
                Matrix m = Matrix.CreateFromQuaternion(new Quaternion(poses[i].rotation.X, poses[i].rotation.Y, poses[i].rotation.Z, poses[i].rotation.W));
                m = Matrix.Multiply(m, Matrix.CreateScale(poses[i].scale.X, poses[i].scale.Y, poses[i].scale.Z));
                m.M41 = poses[i].position.X; m.M42 = poses[i].position.Y; m.M43 = poses[i].position.Z;
                bones[i] = m;
            }
        }

        public void Dispose() { IsDisposed = true; }
    }
}
