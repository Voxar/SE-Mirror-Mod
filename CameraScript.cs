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
        IMyCameraBlock m_cameraBlock;       // last sync's resolved camera (null when offline)
        string         m_title = "Camera";  // last sync's resolved camera CustomName

        public CameraScript(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        protected override string Title => m_title;

        protected override string Subtitle
            => m_cameraBlock == null ? "Camera offline" : base.Subtitle;

        protected override bool TryBuildRegistration(out PanelRegistration reg)
        {
            reg = default(PanelRegistration);

            // Always refresh drawing state, even when we'll fail the
            // gate — the subtitle needs m_cameraBlock / m_title
            // regardless of whether we register.
            float zoom; float range; string title;
            var cam = ResolveCameraState(out zoom, out range, out title);
            m_cameraBlock = cam;
            m_title       = title;

            if (!IsBlockGoodState() || cam == null) return false;

            reg = new PanelRegistration
            {
                Mode            = PanelRegistry.PanelMode.Camera,
                CameraBlock     = cam as IMyCubeBlock,
                Zoom            = zoom,
                MaxViewDistance = range,
            };
            return true;
        }

        /// <summary>
        /// Reads current camera selection / zoom / range from
        /// <see cref="MirrorSession"/>'s per-entity storage. Returns
        /// the resolved <see cref="IMyCameraBlock"/> when a camera is
        /// selected AND its block is working; <c>null</c> otherwise.
        /// </summary>
        IMyCameraBlock ResolveCameraState(out float zoom, out float range, out string title)
        {
            zoom = 1f; range = MirrorSession.DefaultRange; title = "Camera";

            int idx = ResolveSurfaceIdx();
            var entity = m_block as IMyEntity;
            if (entity == null) return null;

            // Effective id (stored if set, else first camera on grid) so
            // a freshly-selected Camera app renders without the user
            // having to open the listbox and pick.
            long camId = MirrorSession.GetEffectiveCameraId(entity, idx);
            zoom  = MirrorSession.GetSelectedZoom(entity, idx);
            range = MirrorSession.GetSelectedRange(entity, idx);
            if (camId == 0L) return null;

            IMyEntity camEnt;
            if (!MyAPIGateway.Entities.TryGetEntityById(camId, out camEnt))
                return null;

            var cam = camEnt as IMyCameraBlock;
            if (cam == null) return null;
            if (!string.IsNullOrEmpty(cam.CustomName)) title = cam.CustomName;

            var func = camEnt as Sandbox.ModAPI.IMyFunctionalBlock;
            if (func == null || !func.IsWorking) return null;

            return cam;
        }
    }
}
