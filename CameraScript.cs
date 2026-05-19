using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.ModAPI;
using System;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;
using Mirror;                          // MirrorSession lives here (migrates in Phase 1.4)
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;
using IMyCubeBlock = VRage.Game.ModAPI.Ingame.IMyCubeBlock;
using IMyTextSurfaceProvider = Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider;
using IMyTerminalBlock = Sandbox.ModAPI.IMyTerminalBlock;
using IMyCameraBlock = Sandbox.ModAPI.IMyCameraBlock;

namespace MirrorCameraMod
{
    [MyTextSurfaceScript("Camera", "Camera")]
    public class CameraScript : MyTSSCommon
    {
        public override ScriptUpdate NeedsUpdate => ScriptUpdate.Update10;

        int m_surfaceIdx = -1;
        bool m_isRegistered;
        bool m_sourceOk;     // tracked for stub subtitle
        string m_title = "Camera";

        public CameraScript(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size)
        {
            HookEvents();
            SyncRegistration();
        }

        void HookEvents()
        {
            var func = m_block as Sandbox.ModAPI.IMyFunctionalBlock;
            if (func != null) func.IsWorkingChanged += OnIsWorkingChanged;
            var term = m_block as IMyTerminalBlock;
            if (term != null) term.PropertiesChanged += OnPropertiesChanged;
        }

        void UnhookEvents()
        {
            var func = m_block as Sandbox.ModAPI.IMyFunctionalBlock;
            if (func != null) func.IsWorkingChanged -= OnIsWorkingChanged;
            var term = m_block as IMyTerminalBlock;
            if (term != null) term.PropertiesChanged -= OnPropertiesChanged;
        }

        void OnIsWorkingChanged(IMyCubeBlock _) { SyncRegistration(); }
        void OnPropertiesChanged(IMyTerminalBlock _) { SyncRegistration(); }

        int ResolveSurfaceIdx()
        {
            if (m_surfaceIdx >= 0) return m_surfaceIdx;
            var provider = m_block as IMyTextSurfaceProvider;
            if (provider == null) return 0;
            for (int i = 0; i < provider.SurfaceCount; i++)
                if (object.ReferenceEquals(provider.GetSurface(i), m_surface))
                    return m_surfaceIdx = i;
            return m_surfaceIdx = 0;
        }

        // Reads current camera selection / zoom / range from MirrorSession.
        // Returns true when a camera is selected AND its block is functional+working.
        bool ResolveCameraState(out long camId, out float zoom, out float range, out string title)
        {
            camId = 0; zoom = 1f; range = MirrorSession.DefaultRange; title = "Camera";
            int idx = ResolveSurfaceIdx();
            var entity = m_block as IMyEntity;
            if (entity == null) return false;
            // Use effective id (stored if set, else first camera on grid) so a
            // freshly-selected Camera app renders without the user having to
            // open the listbox and pick.
            camId = MirrorSession.GetEffectiveCameraId(entity, idx);
            zoom = MirrorSession.GetSelectedZoom(entity, idx);
            range = MirrorSession.GetSelectedRange(entity, idx);
            if (camId == 0L) return false;
            IMyEntity camEnt;
            if (!MyAPIGateway.Entities.TryGetEntityById(camId, out camEnt)) return false;
            var cam = camEnt as IMyCameraBlock;
            if (cam != null && !string.IsNullOrEmpty(cam.CustomName)) title = cam.CustomName;
            var src = camEnt as Sandbox.ModAPI.IMyFunctionalBlock;
            return src != null && src.IsWorking;
        }

        void SyncRegistration()
        {
            if (m_block == null || m_surface == null) return;
            var cube = m_block as IMyCubeBlock;
            bool blockOk = cube != null && cube.IsFunctional;
            var func = m_block as Sandbox.ModAPI.IMyFunctionalBlock;
            if (func != null && !func.IsWorking) blockOk = false;

            long camId; float zoom; float range; string title;
            bool srcOk = ResolveCameraState(out camId, out zoom, out range, out title);
            m_sourceOk = srcOk;
            m_title = title;

            bool good = blockOk && srcOk;
            if (good)
            {
                PanelRegistry.AddOrUpdate(m_block, ResolveSurfaceIdx(), m_surface,
                    PanelRegistry.PanelMode.Camera, camId, zoom, range);
                m_isRegistered = true;
            }
            else if (m_isRegistered)
            {
                PanelRegistry.Remove(m_block, ResolveSurfaceIdx());
                m_isRegistered = false;
            }
        }

        public override void Run()
        {
            base.Run();
            try { DrawStub(); } catch { }
        }

        void DrawStub()
        {
            string subtitle = m_sourceOk ? "Plugin not loaded" : "Camera offline";
            using (var frame = m_surface.DrawFrame())
            {
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple",
                    m_halfSize, m_size, new Color(10, 10, 15)));
                frame.Add(new MySprite(SpriteType.TEXT, m_title,
                    m_halfSize - new Vector2(0, 22f * m_scale.Y),
                    null, m_foregroundColor, "White", TextAlignment.CENTER, 1.2f * m_scale.Y));
                frame.Add(new MySprite(SpriteType.TEXT, subtitle,
                    m_halfSize + new Vector2(0, 8f * m_scale.Y),
                    null, new Color(m_foregroundColor, 0.5f), "White", TextAlignment.CENTER, 0.7f * m_scale.Y));
            }
        }

        public override void Dispose()
        {
            try
            {
                if (m_isRegistered && m_block != null)
                {
                    PanelRegistry.Remove(m_block, ResolveSurfaceIdx());
                    m_isRegistered = false;
                }
                UnhookEvents();
            }
            catch { }
            base.Dispose();
        }
    }
}
