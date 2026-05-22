using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;
using IMyCubeBlock   = VRage.Game.ModAPI.Ingame.IMyCubeBlock;
using IMyCameraBlock = Sandbox.ModAPI.IMyCameraBlock;

namespace MirrorCameraMod
{
    /// <summary>
    /// LCD app that registers its surface as a Camera panel showing the
    /// view from a chosen camera block. All lifecycle plumbing is on
    /// <see cref="PanelTss"/>; this class supplies camera-mode
    /// registration arguments and the splash title/subtitle.
    ///
    /// <para>The source camera state is resolved via
    /// <see cref="MirrorSession"/> each sync: a missing/non-working
    /// source camera makes the registration return false (panel
    /// removed from PanelRegistry) and flips <see cref="m_sourceOk"/>
    /// so the splash subtitle reads "Camera offline" instead of the
    /// plugin status.</para>
    /// </summary>
    [MyTextSurfaceScript(MirrorSession.CameraScriptId, "Camera")]
    public class CameraScript : PanelTss
    {
        bool   m_sourceOk;          // last sync saw a working source camera
        string m_title = "Camera";  // last sync's resolved camera CustomName

        public CameraScript(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        protected override string Title => m_title;

        protected override string Subtitle
            => !m_sourceOk ? "Camera offline" : base.Subtitle;

        protected override bool TryBuildRegistration(out PanelRegistration reg)
        {
            reg = default(PanelRegistration);

            // Always refresh drawing state, even when we'll fail the
            // gate — the subtitle needs m_sourceOk regardless of whether
            // we register.
            long camId; float zoom; float range; string title;
            bool srcOk = ResolveCameraState(out camId, out zoom, out range, out title);
            m_sourceOk = srcOk;
            m_title    = title;

            if (!IsBlockGoodState() || !srcOk) return false;

            reg = new PanelRegistration
            {
                Mode            = PanelRegistry.PanelMode.Camera,
                CameraId        = camId,
                Zoom            = zoom,
                MaxViewDistance = range,
            };
            return true;
        }

        /// <summary>
        /// Reads current camera selection / zoom / range from
        /// <see cref="MirrorSession"/>'s per-entity storage. Returns
        /// true when a camera is selected AND its block is working.
        /// </summary>
        bool ResolveCameraState(out long camId, out float zoom,
                                out float range, out string title)
        {
            camId = 0; zoom = 1f; range = MirrorSession.DefaultRange; title = "Camera";

            int idx = ResolveSurfaceIdx();
            var entity = m_block as IMyEntity;
            if (entity == null) return false;

            // Effective id (stored if set, else first camera on grid) so
            // a freshly-selected Camera app renders without the user
            // having to open the listbox and pick.
            camId = MirrorSession.GetEffectiveCameraId(entity, idx);
            zoom  = MirrorSession.GetSelectedZoom(entity, idx);
            range = MirrorSession.GetSelectedRange(entity, idx);
            if (camId == 0L) return false;

            IMyEntity camEnt;
            if (!MyAPIGateway.Entities.TryGetEntityById(camId, out camEnt))
                return false;

            var cam = camEnt as IMyCameraBlock;
            if (cam != null && !string.IsNullOrEmpty(cam.CustomName))
                title = cam.CustomName;

            var src = camEnt as Sandbox.ModAPI.IMyFunctionalBlock;
            return src != null && src.IsWorking;
        }
    }
}
