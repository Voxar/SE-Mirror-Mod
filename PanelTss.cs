using System;
using System.Collections.Generic;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.ModAPI;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
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
        public override ScriptUpdate NeedsUpdate => ScriptUpdate.Update100;

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
            RegisterForStorageNotify();
            HookEvents();
            SyncRegistration();
        }

        // ── Storage push notification ────────────────────────────────────
        //
        // MirrorStorage Set* writes don't fire any engine event, so the
        // Update100 backstop alone would mean ~1.67s latency between a
        // slider edit and the plugin seeing the new value. To make
        // sliders feel snappy, MirrorStorage calls NotifyStorageChanged
        // directly after each write, with leading-edge debounce to
        // coalesce drag-spam. The Update100 backstop still runs, so any
        // post-drag final value that lands in a debounce window is
        // caught within 1.67s.

        // (blockId << 4) | (surfaceIdx & 0xF) — surfaceIdx never exceeds
        // 16 on any vanilla LCD, and packing into one long avoids the
        // ValueTuple allocation of (long, int) dict keys.
        //
        // s_byLock guards s_byKey. World load instantiates grids on
        // parallel threads, so multiple PanelTss ctors fire concurrently
        // and touch s_byKey. Without the lock, Dictionary.Insert NREs
        // mid-resize when two threads write at once — and SE reports
        // the propagated TargetInvocationException as "world corrupt".
        static readonly Dictionary<long, PanelTss> s_byKey =
            new Dictionary<long, PanelTss>();
        static readonly object s_byLock = new object();

        static long MakeKey(long blockId, int surfaceIdx)
            => (blockId << 4) | (long)(surfaceIdx & 0xF);

        void RegisterForStorageNotify()
        {
            if (m_block == null) return;
            long key = MakeKey(m_block.EntityId, ResolveSurfaceIdx());
            lock (s_byLock) { s_byKey[key] = this; }
        }

        void UnregisterForStorageNotify()
        {
            if (m_block == null) return;
            long key = MakeKey(m_block.EntityId, ResolveSurfaceIdx());
            lock (s_byLock) { s_byKey.Remove(key); }
        }

        /// <summary>Called by <see cref="Settings.MirrorStorage"/> after
        /// a debounced write. Looks up the matching live TSS instance
        /// and triggers a re-sync, so the plugin sees the new slider
        /// value within ~100ms of the user dragging instead of waiting
        /// for the next Update100 tick.</summary>
        public static void NotifyStorageChanged(long blockId, int surfaceIdx)
        {
            PanelTss tss;
            long key = MakeKey(blockId, surfaceIdx);
            bool found;
            lock (s_byLock) { found = s_byKey.TryGetValue(key, out tss); }
            if (found)
            {
                try { tss.SyncRegistration(); }
                catch (Exception ex)
                {
                    MyLog.Default.WriteLine("[MirrorMod] NotifyStorageChanged sync failed: " + ex);
                }
            }
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
                    reg.Mode, reg.CameraBlock, reg.Zoom,
                    reg.MirrorAngleDegX, reg.MirrorAngleDegY);
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
            // changed since the last sync. Exceptions log unconditionally
            // (per CLAUDE.md log rule) — without this the panel silently
            // stops working with no diagnosable trace.
            try { SyncRegistration(); }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] SyncRegistration failed: " + ex);
            }
            try { DrawStub(); }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] DrawStub failed: " + ex);
            }
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

        // Latch so the surface's WriteText content is set exactly once
        // per "plugin missing" stretch and cleared exactly once when the
        // plugin shows up. Without it, every Run() tick would clobber
        // any text the user typed on the surface after we set the hint.
        bool m_wroteNoPluginText;

        void DrawStub()
        {
            // Has the plugin actually reported anything for this panel?
            // (GetStatus is null when the plugin isn't loaded or hasn't
            // processed this surface yet — distinct from Subtitle's
            // null-coalesce-to-"Plugin not loaded".)
            bool pluginReporting =
                PanelRegistry.GetStatus(m_block, ResolveSurfaceIdx()) != null;

            // Surface text-content fallback: when the plugin isn't
            // reporting, write the long welcome / install-the-plugin
            // explainer to the surface's text content. The user only
            // sees it if they switch this surface OFF our app
            // (TEXT_AND_IMAGE mode) — but when they do, they get a
            // pointer to where to install the plugin from. One-shot
            // per transition so subsequent user edits aren't clobbered.
            if (!pluginReporting && !m_wroteNoPluginText)
            {
                try { m_surface.WriteText(MirrorSession.NoPluginMessage); } catch { }
                m_wroteNoPluginText = true;
            }
            else if (pluginReporting && m_wroteNoPluginText)
            {
                // Plugin came online. Clear our explainer so a future
                // app-toggle-off doesn't surface stale "install the
                // plugin" text long after the plugin loaded.
                try { m_surface.WriteText(""); } catch { }
                m_wroteNoPluginText = false;
            }

            // Large grid LCDs have plenty of resolution at the
            // original 1.2 / 0.7 scale; doubling there left the text
            // comically big. Small grid LCDs are tiny on the panel and
            // need the 2x bump to be legible. Branch on grid size.
            bool smallGrid =
                m_block?.CubeGrid != null
                && m_block.CubeGrid.GridSizeEnum == VRage.Game.MyCubeSize.Small;
            float titleScale    = smallGrid ? 2.4f : 1.2f;
            float subtitleScale = smallGrid ? 1.4f : 0.7f;
            float titleOffsetY  = smallGrid ? 44f  : 22f;
            float subOffsetY    = smallGrid ? 16f  : 8f;

            using (var frame = m_surface.DrawFrame())
            {
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple",
                    m_halfSize, m_size, new Color(10, 10, 15)));
                frame.Add(new MySprite(SpriteType.TEXT, Title,
                    m_halfSize - new Vector2(0, titleOffsetY * m_scale.Y),
                    null, m_foregroundColor, "White", TextAlignment.CENTER, titleScale * m_scale.Y));
                frame.Add(new MySprite(SpriteType.TEXT, Subtitle,
                    m_halfSize + new Vector2(0, subOffsetY * m_scale.Y),
                    null, new Color(m_foregroundColor, 0.5f), "White", TextAlignment.CENTER, subtitleScale * m_scale.Y));
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

                // Clear our welcome-text injection so it doesn't linger
                // on the surface after the user switches to another app
                // / content type. Without this, the WriteText we did to
                // expose the install-the-plugin hint in TEXT_AND_IMAGE
                // mode persists indefinitely, polluting whatever the
                // user wants the surface to show next.
                if (m_wroteNoPluginText)
                {
                    try { m_surface?.WriteText(""); } catch { }
                    m_wroteNoPluginText = false;
                }

                UnregisterForStorageNotify();
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
        /// <summary>Mirror mode: yaw applied to the screen plane normal
        /// before reflection (degrees, positive = toward screen Right).
        /// 0 for non-mirror modes.</summary>
        public float MirrorAngleDegX;
        /// <summary>Mirror mode: pitch applied to the screen plane
        /// normal before reflection (degrees, positive = toward screen
        /// Up). 0 for non-mirror modes.</summary>
        public float MirrorAngleDegY;
    }
}
