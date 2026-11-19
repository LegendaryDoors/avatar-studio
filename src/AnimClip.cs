// One animation played as the console's avatar library plays it.

using System;
using AvatarStudio.Avatars;
using JointPose = AvatarStudio.Avatars.Pose;
using Inner = AvatarStudio.Avatars.KeyframeAnimation;

namespace AvatarStudio
{
    sealed class AnimClip
    {
        private readonly Inner inner;
        private readonly int frames;
        private readonly float rate;

        public float Time;
        /// <summary>(frames - 1) / rate</summary>
        public readonly float Length;

        private AnimClip(Inner a)
        {
            inner = a;
            frames = a.FrameCount;
            rate = a.Fps;
            Length = frames > 1 && rate > 0f ? (frames - 1) / rate : 0f;
        }

        public static AnimClip Of(AvatarAnimation a)
        {
            Inner i = a == null ? null : a.Clip;
            return i == null || i.AvatarKeyframes == null || i.FrameCount == 0 ? null : new AnimClip(i);
        }

        /// <summary>Once: clamped to [0, Length]; looped: wrapped by Length.</summary>
        public void Advance(float dt, bool loop)
        {
            Time += dt;
            if (loop && Length > 0f)
            {
                while (Time >= Length) Time -= Length;
                while (Time < 0f) Time += Length;
            }
            else Time = Math.Max(0f, Math.Min(Time, Length));
        }

        private void Frame(out int f, out int next, out float frac)
        {
            float x = Time * rate;
            f = Math.Max(0, Math.Min((int)x, frames - 1));
            frac = x - f;
            if (frac < 0f) frac = 0f;
            next = (f + 1) % frames;
        }

        public void Sample(JointPose[] into) { Sample(inner.AvatarKeyframes, into); }

        /// <summary>The carryable's joints at the cursor; false when the animation keys none.</summary>
        public bool SampleCarryable(JointPose[] into)
        {
            if (inner.CarryableKeyframes == null) return false;
            Sample(inner.CarryableKeyframes, into);
            return true;
        }

        public int CarryableJoints { get { return inner.CarryableJointsCount; } }

        public int Frames { get { return frames; } }
        public float Rate { get { return rate; } }

        public int Joints { get { return inner.AvatarJointsCount; } }

        private void Sample(JointPose[][] keys, JointPose[] into)
        {
            int f, next; float frac;
            Frame(out f, out next, out frac);
            JointPose[] a = keys[f], b = keys[next];
            for (int j = 0; j < into.Length && j < a.Length; j++)
                into[j] = frac > 0f ? RestGaze.Mix(a[j], b[j], frac) : a[j];
        }

        /// <summary>The frame's expression; the left eye's LookLeft and LookRight swap.</summary>
        public AvatarExpression Expression
        {
            get
            {
                AvatarExpression e = default(AvatarExpression);
                if (inner.Facial == null || inner.Facial.Length == 0) return e;
                int f, next; float frac;
                Frame(out f, out next, out frac);
                FaceLayers x = inner.Facial[Math.Min(f, inner.Facial.Length - 1)];
                AvatarEye left = (AvatarEye)x.LeftEyeLayer;
                e.LeftEye = left == AvatarEye.LookLeft ? AvatarEye.LookRight : left == AvatarEye.LookRight ? AvatarEye.LookLeft : left;
                e.RightEye = (AvatarEye)x.RightEyeLayer;
                e.LeftEyebrow = (AvatarEyebrow)x.LeftEyebrowLayer;
                e.RightEyebrow = (AvatarEyebrow)x.RightEyebrowLayer;
                e.Mouth = (AvatarMouth)x.MouthLayer;
                return e;
            }
        }
    }
}
