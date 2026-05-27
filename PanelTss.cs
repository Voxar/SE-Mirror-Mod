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
            // No SyncRegistration() here. The TSS ctor runs during
            // entity init on worker threads while the session is still
            // loading — grid block lists are half-populated, MirrorStorage
            // writes are being applied in parallel, and there's no
            // consumer of the registration yet because rendering hasn't
            // started. SyncRegistration runs on the first Run() (i.e.,
            // first render frame, which can only happen after the world
            // is loaded enough to render) and via the storage / event
            // hooks. Net cost: panels appear in the registry one render
            // frame after construction instead of zero — invisible
            // because no render is happening before that anyway.
            //
            // Earlier shape called SyncRegistration() here to skip the
            // Update100 backstop's ~1.67s latency, but that reasoning
            // doesn't apply during world load (nothing's looking at
            // registry state) and produced "world is corrupted" errors
            // when GatherCameras (auto-pick path inside
            // SyncRegistration) raced with concurrent grid block init.
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

        // s_byLock guards s_byKey. World load instantiates grids on
        // parallel threads, so multiple PanelTss ctors fire concurrently
        // and touch s_byKey. Without the lock, Dictionary.Insert NREs
        // mid-resize when two threads write at once — and SE reports
        // the propagated TargetInvocationException as "world corrupt".
        // Key format: MirrorStorage.MakeKey (shared with the storage
        // and network layers).
        static readonly Dictionary<long, PanelTss> s_byKey =
            new Dictionary<long, PanelTss>();
        static readonly object s_byLock = new object();

        static long MakeKey(long blockId, int surfaceIdx)
            => Settings.MirrorStorage.MakeKey(blockId, surfaceIdx);

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
                return;
            }

            // No PanelTss is keyed by this entity — likely a camera
            // block (storage written by the camera-zoom slider, the
            // CameraOwnZoom-changed PB property, or remote sync).
            // Notify every panel so any that's displaying this camera
            // re-syncs immediately rather than waiting up to one
            // Update100 cycle (~1.67 s) for its periodic re-sync.
            // SyncRegistration is idempotent — panels not affected
            // see no observable change.
            VRage.ModAPI.IMyEntity ent;
            if (Sandbox.ModAPI.MyAPIGateway.Entities != null
                && Sandbox.ModAPI.MyAPIGateway.Entities.TryGetEntityById(blockId, out ent)
                && ent is Sandbox.ModAPI.IMyCameraBlock)
            {
                NotifyAllPanels();
            }
        }

        static void NotifyAllPanels()
        {
            PanelTss[] all;
            lock (s_byLock)
            {
                all = new PanelTss[s_byKey.Count];
                s_byKey.Values.CopyTo(all, 0);
            }
            for (int i = 0; i < all.Length; i++)
            {
                try { all[i].SyncRegistration(); }
                catch (Exception ex)
                {
                    MyLog.Default.WriteLine("[MirrorMod] NotifyAllPanels sync failed: " + ex);
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
                    reg.MirrorAngleDegX, reg.MirrorAngleDegY, reg.MirrorAngleDegZ);
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

        // Latch so the surface's text content is written exactly once
        // for this TSS instance. The content stays in place as long as
        // our app owns the surface — toggling the LCD to Text-and-Image
        // then surfaces our welcome / install-the-plugin explainer
        // regardless of whether the plugin is currently loaded. Cleared
        // in Dispose when the TSS is torn down (user switches apps, or
        // block is removed). Without the latch, every Run() tick would
        // clobber any text the user typed on the surface.
        bool m_wroteNoPluginText;

        void DrawStub()
        {
            // Surface text-content fallback: write the welcome /
            // install-the-plugin explainer to the surface's text
            // content once per TSS instance. The user only sees this
            // if they switch the surface OFF our app (TEXT_AND_IMAGE
            // mode); they get a pointer to where the plugin lives
            // whether or not the plugin is loaded right now. Plugin
            // state is not consulted — the explainer is the only
            // useful thing to put here while our app owns the surface.
            // Skip the WriteText if the surface already has our
            // message (MP sync, prior session, another TSS already
            // wrote it): WriteText would propagate a redundant edit
            // through the surface's sync.
            if (!m_wroteNoPluginText)
            {
                string current = null;
                try { current = m_surface.GetText(); } catch { /* defensive */ }
                if (current != MirrorSession.NoPluginMessage)
                {
                    try { m_surface.WriteText(MirrorSession.NoPluginMessage); } catch { }
                }
                m_wroteNoPluginText = true;
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
        /// <summary>Mirror mode: roll around the screen-normal axis
        /// (degrees, in-plane rotation). 0 for non-mirror modes.</summary>
        public float MirrorAngleDegZ;
    }
}
