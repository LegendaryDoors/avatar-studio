// The avatar being edited: Committed (what A confirmed) and Working (plus the highlighted preview).

using System;
using System.Collections.Generic;
using AvatarStudio.Avatars;

namespace AvatarStudio
{
    sealed class EditSession
    {
        public AvatarManifest Committed { get; private set; }
        public AvatarManifest Working { get; private set; }
        public string Gamertag = "Player";
        public event Action Changed;
        public int Version { get; private set; }          // bumps on every change (thumbnail keys)

        public EditSession(AvatarManifest start)
        {
            Committed = (AvatarManifest)start.Clone();
            Working = (AvatarManifest)start.Clone();
        }

        public AvatarGender Body { get { return Working.BodyType; } }
        public byte[] WorkingBytes { get { return Working.SaveToBinary(); } }
        public byte[] CommittedBytes { get { return Committed.SaveToBinary(); } }

        public void Replace(AvatarManifest m)
        {
            Committed = (AvatarManifest)m.Clone();
            Working = (AvatarManifest)m.Clone();
            Bump();
        }

        public void Preview(Action<AvatarManifestEditor> op)
        {
            PreviewManifest(delegate(AvatarManifest w) { op(AvatarStack.Editor(w)); });
        }

        /// <summary>For edits where the console's editor bypasses AvatarManifestEditor (eye shadow).</summary>
        public void PreviewManifest(Action<AvatarManifest> op)
        {
            AvatarManifest w = (AvatarManifest)Committed.Clone();
            try
            {
                op(w);
            }
            catch (Exception e)
            {
                Console.WriteLine("[studio] edit rejected by AvatarManifestEditor: " + e.Message);
                return;
            }
            if (!w.Validate()) { Console.WriteLine("[studio] edit produced an invalid manifest; ignored"); return; }
            Working = w;
            Bump();
        }

        public void Commit()
        {
            Committed = (AvatarManifest)Working.Clone();
        }

        public void Revert()
        {
            Working = (AvatarManifest)Committed.Clone();
            Bump();
        }

        private void Bump() { Version++; if (Changed != null) Changed(); }

        // ---- convenience operations, all through the Microsoft editor ---------------------------
        public void PreviewItem(Guid asset)                          { Preview(delegate(AvatarManifestEditor e) { e.ReplaceAsset(new EditorAssetInfo(asset)); }); }
        public void PreviewTexture(DynamicTextureType t, Guid asset) { Preview(delegate(AvatarManifestEditor e) { e.ReplaceAsset(new EditorAssetInfo(t, asset)); }); }
        public void PreviewShape(BlendShapeType t, Guid asset)       { Preview(delegate(AvatarManifestEditor e) { e.ReplaceAsset(new EditorAssetInfo(t, asset)); }); }
        public void PreviewRemove(AvatarManifestEditor.RemovableComponents kind) { Preview(delegate(AvatarManifestEditor e) { e.RemoveComponents(kind); }); }
        public void PreviewColor(DynamicColorType t, Colorb c)       { Preview(delegate(AvatarManifestEditor e) { e.SetAvatarColor(t, c); }); }
        public void PreviewItemColors(Guid asset, ComponentColors colors) { Preview(delegate(AvatarManifestEditor e) { e.ReplaceAsset(new EditorAssetInfo(asset, colors)); }); }
        // ---- eye shadow, as the console's Eye Shadow screen does it (sub_920DD370) ----
        public void PreviewEyeShadowRemove()
        {
            PreviewManifest(delegate(AvatarManifest m)
            {
                m.SetDynamicColor(DynamicColorType.EyeShadow, new Colorb(0, 0, 0));
                ReplacementTexture t = m.GetReplacementTexture(DynamicTextureType.EyeShadow);
                t.TextureAssetId = Guid.Empty;
                m.SetReplacementTexture(DynamicTextureType.EyeShadow, t);
            });
        }

        public void PreviewEyeShadowColor(Colorb c)
        {
            PreviewManifest(delegate(AvatarManifest m)
            {
                m.SetDynamicColor(DynamicColorType.EyeShadow, c);
                Guid eye = m.GetReplacementTexture(DynamicTextureType.Eye).TextureAssetId;
                AvatarAssetDependency d = Dependencies.Get(eye);
                if (d == null || d.DependentAssetId == Guid.Empty) return;
                ReplacementTexture t = m.GetReplacementTexture(DynamicTextureType.EyeShadow);
                t.TextureAssetId = d.DependentAssetId;
                m.SetReplacementTexture(DynamicTextureType.EyeShadow, t);
            });
        }

        public void PreviewSize(float height, float width)
        {
            Preview(delegate(AvatarManifestEditor e) { e.AvatarHeightFactor = height; e.AvatarWidthFactor = width; });
        }

        /// <summary>A saved outfit put on the avatar as it stands.</summary>
        public void PreviewOutfit(byte[] outfit)
        {
            AvatarManifest o;
            try { o = (AvatarManifest)AvatarManifest.Create(outfit); }
            catch (Exception e) { Console.WriteLine("[studio] outfit unreadable: " + e.Message); return; }
            PreviewManifest(delegate(AvatarManifest m) { OutfitOps.Wear(m, o); });
        }

        // ---- readers -----------------------------------------------------------------------------
        public Guid EquippedComponent(AvatarComponentMasks mask)
        {
            return Committed.GetComponentInfo(mask, AssetMatchingMode.Any).AssetId;
        }
        public ComponentInfo EquippedComponentInfo(AvatarComponentMasks mask)
        {
            return Committed.GetComponentInfo(mask, AssetMatchingMode.Any);
        }
        public Guid EquippedTexture(DynamicTextureType t) { return Committed.GetReplacementTexture(t).TextureAssetId; }
        /// <summary>Whether the avatar on screen wears the asset.</summary>
        public bool Wears(Guid asset) { return Working.IsAssetPresent(asset); }
        public Guid EquippedShape(BlendShapeType t) { return Committed.GetBlendShape(t); }
        public Colorb Color(DynamicColorType t) { return Committed.GetDynamicColor(t); }
    }
}
