using Sandbox.Game.GameSystems.TextSurfaceScripts;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRageMath;
using IMyTextSurface         = Sandbox.ModAPI.Ingame.IMyTextSurface;
using IMyCubeBlock           = VRage.Game.ModAPI.Ingame.IMyCubeBlock;
using IMyTextSurfaceProvider = Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider;
using IMyTerminalBlock       = Sandbox.ModAPI.IMyTerminalBlock;
using IMyFunctionalBlock     = Sandbox.ModAPI.IMyFunctionalBlock;

namespace MirrorCameraMod
{
    /// <summary>
    /// Base text-surface script for any LCD app this mod exposes
    /// (currently Mirror and Camera). Concentrates two responsibilities
    /// neither subclass should re-implement:
    ///
    /// <list type="bullet">
    ///   <item><b>Lifecycle + registration</b>: hooks the block's state-
    ///         change events (Enabled, IsWorking, CubeGrid, Ownership,
    ///         Properties, MarkForClose), and calls
    ///         <see cref="SyncRegistration"/> in response. Subclasses
    ///         implement <see cref="TryBuildRegistration"/> to decide
    ///         whether the panel should be in <c>PanelRegistry</c> right
    ///         now and what arguments to register with.</item>
    ///   <item><b>Splash drawing</b>: every <c>Update10</c>, draws a
    ///         standard background + title + subtitle. Subclasses
    ///         override <see cref="Title"/> and <see cref="Subtitle"/>
    ///         to provide the mode-specific text. Subtitle defaults to
    ///         the plugin-reported status (e.g. "rendered",
    ///         "failed: ..."), falling back to "Plugin not loaded".</item>
    /// </list>
    ///
    /// <para>The base also caches the surface index, since enumerating
    /// the block's surfaces to find which slot <c>m_surface</c> sits in
    /// is the same lookup for every subclass.</para>
    ///
    /// <para><b>Inherited <see cref="MyTSSCommon"/> fields used here:</b>
    /// <c>m_block</c> (the cube block hosting this surface),
    /// <c>m_surface</c> (the text surface this script writes to),
    /// <c>m_size</c> / <c>m_halfSize</c> / <c>m_scale</c> (sprite
    /// layout helpers), <c>m_foregroundColor</c> (theme colour for
    /// title/subtitle text). All come from the base ctor and stay
    /// stable for the script's lifetime.</para>
    /// </summary>
    public abstract class PanelTss : MyTSSCommon
    {
        public override ScriptUpdate NeedsUpdate => ScriptUpdate.Update10;

        // -1 = not yet resolved. Filled lazily by ResolveSurfaceIdx the
        // first time it's needed and cached for the rest of the script's
        // lifetime (surface ordering on a block doesn't change at runtime).
        int m_surfaceIdx = -1;

        // True iff a PanelRegistry entry currently exists for this
        // (block, surfaceIdx). Tracked locally so we know whether to call
        // Remove (Remove on a never-Added key is a silent no-op, but we
        // also want to avoid spurious version-bumps mid-game).
        bool m_isRegistered;

        protected PanelTss(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size)
        {
            HookEvents();
            SyncRegistration();
        }

        // ── Event wiring ─────────────────────────────────────────────────

        // All six events route to SyncRegistration. The set covers every
        // engine-side trigger that could affect whether this panel
        // belongs in the registry or what arguments it registers with:
        //   - EnabledChanged / IsWorkingChanged: block on/off + power.
        //   - CubeGridChanged: block moved between grids (merge/split).
        //   - OwnershipChanged: faction/owner change can flip IsWorking.
        //   - PropertiesChanged: catch-all for terminal-property edits.
        //   - OnMarkForClose: block destroyed — tear ourselves down.
        // Hooking is via the IMyFunctionalBlock cast since that's the
        // narrowest interface that exposes every event we need
        // (it derives from IMyCubeBlock and IMyEntity, so the cube /
        // terminal / entity events are reachable through it). Blocks
        // that aren't IMyFunctionalBlock don't have an on/off toggle
        // anyway, so this is the right gate.
        void HookEvents()
        {
            var func = m_block as IMyFunctionalBlock;
            if (func != null)
            {
                func.EnabledChanged   += OnNeedsSync;
                func.IsWorkingChanged += OnNeedsSync;
                func.CubeGridChanged  += OnCubeGridChanged;
                func.OwnershipChanged += OnNeedsSync;
                func.OnMarkForClose   += OnMarkForClose;
            }
            var term = m_block as IMyTerminalBlock;
            if (term != null) term.PropertiesChanged += OnNeedsSync;
        }

        void UnhookEvents()
        {
            var func = m_block as IMyFunctionalBlock;
            if (func != null)
            {
                func.EnabledChanged   -= OnNeedsSync;
                func.IsWorkingChanged -= OnNeedsSync;
                func.CubeGridChanged  -= OnCubeGridChanged;
                func.OwnershipChanged -= OnNeedsSync;
                func.OnMarkForClose   -= OnMarkForClose;
            }
            var term = m_block as IMyTerminalBlock;
            if (term != null) term.PropertiesChanged -= OnNeedsSync;
        }

        // Overloads so the same handler body can be wired to events with
        // different signatures (IMyCubeBlock, IMyTerminalBlock).
        void OnNeedsSync(IMyCubeBlock _)     { SyncRegistration(); }
        void OnNeedsSync(IMyTerminalBlock _) { SyncRegistration(); }
        void OnCubeGridChanged(IMyCubeGrid _) { SyncRegistration(); }
        void OnMarkForClose(VRage.ModAPI.IMyEntity _) { Dispose(); }

        // ── Surface index ────────────────────────────────────────────────

        /// <summary>Index of this surface within the block's
        /// <see cref="IMyTextSurfaceProvider"/>. Cached after first
        /// lookup. Returns 0 if the block isn't a surface provider (the
        /// PanelRegistry key still needs a stable surfaceIdx).</summary>
        protected int ResolveSurfaceIdx()
        {
            if (m_surfaceIdx >= 0) return m_surfaceIdx;
            var provider = m_block as IMyTextSurfaceProvider;
            if (provider == null) return 0;
            for (int i = 0; i < provider.SurfaceCount; i++)
                if (object.ReferenceEquals(provider.GetSurface(i), m_surface))
                    return m_surfaceIdx = i;
            return m_surfaceIdx = 0;
        }

        // ── Registration ─────────────────────────────────────────────────

        /// <summary>True iff the block side of the registration gate is
        /// passed: block and surface present, block functional, block
        /// working. Subclasses combine this with mode-specific checks in
        /// <see cref="TryBuildRegistration"/>.</summary>
        protected bool IsBlockGoodState()
        {
            if (m_block == null || m_surface == null) return false;
            if (!m_block.IsFunctional) return false;
            if (!m_block.IsWorking)    return false;
            return true;
        }

        /// <summary>
        /// Subclass hook: decide whether this panel should be in the
        /// registry right now and, if so, with which arguments. Called
        /// from <see cref="SyncRegistration"/> on every state-change
        /// event AND every <c>Update10</c> tick (slider edits via mod-
        /// storage don't fire any of the engine events).
        ///
        /// <para>Implementations may have side effects (e.g. caching the
        /// resolved camera title for the splash subtitle) — they're
        /// called unconditionally, so they're the right place to refresh
        /// drawing state too.</para>
        ///
        /// <para>Return false to signal "this panel should not be
        /// registered right now". <see cref="SyncRegistration"/> will
        /// remove the existing entry if there was one.</para>
        /// </summary>
        protected abstract bool TryBuildRegistration(out PanelRegistration reg);

        void SyncRegistration()
        {
            PanelRegistration reg;
            if (TryBuildRegistration(out reg))
            {
                PanelRegistry.AddOrUpdate(
                    m_block, ResolveSurfaceIdx(), m_surface,
                    reg.Mode, reg.CameraBlock, reg.Zoom);
                m_isRegistered = true;
            }
            else if (m_isRegistered)
            {
                PanelRegistry.Remove(m_block, ResolveSurfaceIdx());
                m_isRegistered = false;
            }
        }

        // ── Tick + draw ──────────────────────────────────────────────────

        public override void Run()
        {
            base.Run();
            // Re-sync each Update10 in case settings the engine doesn't
            // fire events for (range slider, camera-list selection)
            // changed since the last sync.
            try { SyncRegistration(); } catch { }
            try { DrawStub(); }       catch { /* next tick gets a fresh chance */ }
        }

        /// <summary>Title text shown in the splash. Mirror/Camera supply
        /// their own — Mirror is a constant, Camera follows the selected
        /// camera block's CustomName.</summary>
        protected abstract string Title { get; }

        /// <summary>Subtitle text. Default = plugin-reported status (e.g.
        /// "rendered", "failed: ...") falling back to "Plugin not loaded"
        /// when the plugin isn't writing status. Camera overrides to
        /// gate on its source-camera state ("Camera offline").</summary>
        protected virtual string Subtitle
            => PanelRegistry.GetStatus(m_block, ResolveSurfaceIdx())
               ?? "Plugin not loaded";

        void DrawStub()
        {
            using (var frame = m_surface.DrawFrame())
            {
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple",
                    m_halfSize, m_size, new Color(10, 10, 15)));
                frame.Add(new MySprite(SpriteType.TEXT, Title,
                    m_halfSize - new Vector2(0, 22f * m_scale.Y),
                    null, m_foregroundColor, "White", TextAlignment.CENTER, 1.2f * m_scale.Y));
                frame.Add(new MySprite(SpriteType.TEXT, Subtitle,
                    m_halfSize + new Vector2(0, 8f * m_scale.Y),
                    null, new Color(m_foregroundColor, 0.5f), "White", TextAlignment.CENTER, 0.7f * m_scale.Y));
            }
        }

        // ── Dispose ──────────────────────────────────────────────────────

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

    /// <summary>
    /// Value-object describing the arguments needed to call
    /// <see cref="PanelRegistry.AddOrUpdate"/>. Constructed by a
    /// <see cref="PanelTss"/> subclass in
    /// <see cref="PanelTss.TryBuildRegistration"/>.
    /// </summary>
    public struct PanelRegistration
    {
        public PanelRegistry.PanelMode Mode;
        /// <summary>Camera block to render the view of. <c>null</c> for
        /// Mirror mode (the renderer uses the LCD's own plane).</summary>
        public IMyCubeBlock CameraBlock;
        public float Zoom;            // 1 for non-camera modes
    }
}
