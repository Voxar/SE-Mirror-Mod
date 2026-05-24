using MirrorCameraMod.Settings;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using VRage.ModAPI;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;
using IMyCubeBlock   = VRage.Game.ModAPI.Ingame.IMyCubeBlock;

namespace MirrorCameraMod
{
    /// <summary>
    /// LCD app that registers its surface as a Mirror panel. All the
    /// lifecycle plumbing lives on <see cref="PanelTss"/>; this class
    /// just supplies the mirror-specific registration arguments (mode
    /// = Mirror, no camera, mirror-angle yaw/pitch read from the
    /// per-surface storage) and the splash title.
    /// </summary>
    [MyTextSurfaceScript(MirrorSession.MirrorScriptId, "Mirror")]
    public class MirrorScript : PanelTss
    {
        public MirrorScript(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        protected override string Title => "Mirror";

        protected override bool TryBuildRegistration(out PanelRegistration reg)
        {
            reg = default(PanelRegistration);
            if (!IsBlockGoodState()) return false;

            int idx = ResolveSurfaceIdx();
            var entity = m_block as IMyEntity;
            reg = new PanelRegistration
            {
                Mode            = PanelRegistry.PanelMode.Mirror,
                CameraBlock     = null,
                Zoom            = 1f,
                MirrorAngleDegX = entity != null ? MirrorStorage.GetMirrorAngleX(entity, idx) : 0f,
                MirrorAngleDegY = entity != null ? MirrorStorage.GetMirrorAngleY(entity, idx) : 0f,
            };
            return true;
        }
    }
}
