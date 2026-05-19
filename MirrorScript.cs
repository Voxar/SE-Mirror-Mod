using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.ModAPI;
using System;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRageMath;
using Mirror;                          // MirrorSession lives in this namespace (will migrate in Phase 1.4)
using IMyTextSurface = Sandbox.ModAPI.Ingame.IMyTextSurface;
using IMyCubeBlock = VRage.Game.ModAPI.Ingame.IMyCubeBlock;
using IMyTextSurfaceProvider = Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider;
using IMyTerminalBlock = Sandbox.ModAPI.IMyTerminalBlock;

namespace MirrorCameraMod
{
    [MyTextSurfaceScript("Mirror", "Mirror")]
    public class MirrorScript : MyTSSCommon
    {
        public override ScriptUpdate NeedsUpdate => ScriptUpdate.Update10;

        int m_surfaceIdx = -1;
        bool m_isRegistered;

        public MirrorScript(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
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

        bool IsGoodState()
        {
            if (m_block == null || m_surface == null) return false;
            var cube = m_block as IMyCubeBlock;
            if (cube == null || !cube.IsFunctional) return false;
            var func = m_block as Sandbox.ModAPI.IMyFunctionalBlock;
            if (func != null && !func.IsWorking) return false;
            return true;
        }

        void SyncRegistration()
        {
            bool good = IsGoodState();
            if (good)
            {
                int idx = ResolveSurfaceIdx();
                float range = (m_block is VRage.ModAPI.IMyEntity)
                    ? MirrorSession.GetSelectedRange((VRage.ModAPI.IMyEntity)m_block, idx)
                    : MirrorSession.DefaultRange;
                PanelRegistry.AddOrUpdate(m_block, idx, m_surface,
                    PanelRegistry.PanelMode.Mirror, cameraId: 0L, zoom: 1f, maxViewDistance: range);
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
            try { DrawStub(); } catch { /* swallow — next tick gets a fresh chance */ }
        }

        void DrawStub()
        {
            using (var frame = m_surface.DrawFrame())
            {
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple",
                    m_halfSize, m_size, new Color(10, 10, 15)));
                frame.Add(new MySprite(SpriteType.TEXT, "Mirror",
                    m_halfSize - new Vector2(0, 22f * m_scale.Y),
                    null, m_foregroundColor, "White", TextAlignment.CENTER, 1.2f * m_scale.Y));
                frame.Add(new MySprite(SpriteType.TEXT, "Plugin not loaded",
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
            catch { /* Dispose must not throw or SE leaks the surface */ }
            base.Dispose();
        }
    }
}
