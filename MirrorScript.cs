using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MirrorCameraMod.Settings;
using MirrorCameraMod.Terminal;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using IMyTextSurface   = Sandbox.ModAPI.Ingame.IMyTextSurface;
using IMyCubeBlock     = VRage.Game.ModAPI.Ingame.IMyCubeBlock;
using IMyTerminalBlock = Sandbox.ModAPI.IMyTerminalBlock;

namespace MirrorCameraMod
{
    /// <summary>
    /// LCD app that registers its surface as a Mirror panel. All the
    /// lifecycle plumbing lives on <see cref="PanelTss"/>; this class
    /// just supplies the mirror-specific registration arguments (mode
    /// = Mirror, no camera, mirror-angle yaw/pitch read from the
    /// per-surface storage) and the splash title.
    ///
    /// <para>This class also owns the <b>Mirror Yaw</b> and <b>Mirror
    /// Pitch</b> terminal sliders. <see cref="LcdAppTerminalControls"/>'s
    /// <c>CustomControlGetter</c> dispatcher calls
    /// <see cref="AppendCustomControls"/> for any block whose active
    /// surface is running this script — keeps each script's UI
    /// definitions adjacent to its rendering / registration logic
    /// rather than centralised in a foreign class.</para>
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

        // ── Terminal controls ─────────────────────────────────────────
        //
        // Lazy-init singletons. The slider instances are created on
        // first AppendCustomControls call (which only happens after a
        // surface picks our script, so we know SE's terminal subsystem
        // is fully up). After creation they're reused for every block —
        // Getter/Setter dispatch to the active surface index via
        // LcdAppTerminalControls.ActiveSurfaceIndex.

        static List<IMyTerminalControl> s_controls;
        static List<IMyTerminalAction>  s_actions;

        /// <summary>Return this script's controls (Yaw + Pitch sliders).
        /// Called by <see cref="LcdAppTerminalControls"/>'s
        /// <c>CustomControlGetter</c> dispatcher when the active surface
        /// on the queried block is running this script. The dispatcher
        /// inserts the returned controls at the right position in the
        /// terminal list (before the LCD color pickers).</summary>
        public static IReadOnlyList<IMyTerminalControl> GetCustomControls()
        {
            EnsureBuilt();
            return s_controls;
        }

        /// <summary>Return this script's toolbar actions (Increase /
        /// Decrease / Reset for each slider). Called by
        /// <see cref="LcdAppTerminalControls"/>'s
        /// <c>CustomActionGetter</c> dispatcher.</summary>
        public static IReadOnlyList<IMyTerminalAction> GetCustomActions()
        {
            EnsureBuilt();
            return s_actions;
        }

        static void EnsureBuilt()
        {
            // Yaw / Pitch are now block-level controls registered on
            // IMyTextPanel itself (see RegisterBlockLevelControls). The
            // Mirror script has no script-specific controls — its UI is
            // entirely the always-visible block-level sliders, which
            // work whether or not the Mirror app is the active script.
            if (s_controls != null) return;
            s_controls = new List<IMyTerminalControl>();
            s_actions  = new List<IMyTerminalAction>();
        }

        // ── Block-level tilt controls ────────────────────────────────────
        //
        // Registered once per session, from the per-block game-logic
        // component's first frame (see MirrorMeshTilt.UpdateOnceBeforeFrame).
        // Always-visible on text panels passing IsTiltEligible, regardless
        // of which LCD app (if any) the surface is running.

        static bool s_blockControlsRegistered;

        public static void RegisterBlockLevelControls()
        {
            if (s_blockControlsRegistered) return;
            s_blockControlsRegistered = true;

            var yaw   = CreateAngleSlider(yaw: true);
            var pitch = CreateAngleSlider(yaw: false);
            MyAPIGateway.TerminalControls.AddControl<IMyTextPanel>(yaw);
            MyAPIGateway.TerminalControls.AddControl<IMyTextPanel>(pitch);

            var actions = new List<IMyTerminalAction>();
            AddSliderActions(actions, "Mirror.CameraSource.MirrorYaw",   "Mirror Yaw",   yaw,   step: 5f, reset: 0f);
            AddSliderActions(actions, "Mirror.CameraSource.MirrorPitch", "Mirror Pitch", pitch, step: 5f, reset: 0f);
            foreach (var a in actions)
                MyAPIGateway.TerminalControls.AddAction<IMyTextPanel>(a);
        }


        // Helper shared by both axis sliders. Builds Increase and
        // Decrease toolbar actions wired through the slider's own
        // Getter/Setter so the action path goes through the same storage
        // write as a manual drag. (Reset was tried earlier but the
        // "Cancel" icon doesn't ship in vanilla content — caused render
        // errors. Increase/Decrease use Action_Increase / Action_Decrease
        // which are vanilla.)
        static void AddSliderActions(List<IMyTerminalAction> sink, string baseId, string baseName,
                                     IMyTerminalControlSlider sl, float step, float reset)
        {
            sink.Add(BuildSliderAction(baseId + ".Increase", "Increase " + baseName, "Increase",
                sl, b => Clamp(sl, b, sl.Getter(b) + step)));
            sink.Add(BuildSliderAction(baseId + ".Decrease", "Decrease " + baseName, "Decrease",
                sl, b => Clamp(sl, b, sl.Getter(b) - step)));
        }

        static IMyTerminalAction BuildSliderAction(string id, string name, string icon,
                                                   IMyTerminalControlSlider sl,
                                                   System.Func<IMyTerminalBlock, float> compute)
        {
            var action = MyAPIGateway.TerminalControls.CreateAction<IMyTerminalBlock>(id);
            action.Name = new StringBuilder(name);
            action.Icon = "Textures\\GUI\\Icons\\Actions\\" + icon + ".dds";
            action.ValidForGroups = false;
            action.Enabled = b => true;
            action.Action  = b => sl.Setter(b, compute(b));
            action.Writer  = (b, sb) => sb.Append(sl.Getter(b).ToString("+0;-0;0", CultureInfo.InvariantCulture)).Append('°');
            return action;
        }

        static float Clamp(IMyTerminalControlSlider sl, IMyTerminalBlock b, float v)
        {
            var prop = (Sandbox.ModAPI.Interfaces.ITerminalProperty<float>)sl;
            float min = prop.GetMinimum(b);
            float max = prop.GetMaximum(b);
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        // Both axis sliders share the same shape: identical limits,
        // identical formatting, different storage axis. One factory
        // keeps the visible diff to the two function pointers.
        static IMyTerminalControlSlider CreateAngleSlider(bool yaw)
        {
            var id    = "Mirror.CameraSource" + (yaw ? ".MirrorYaw" : ".MirrorPitch");
            var title = yaw ? "Mirror Yaw"   : "Mirror Pitch";
            var tip   = yaw ? "Tilt mirror left/right."
                            : "Tilt mirror up/down.";

            // We don't know the TBlock at compile time here — the
            // CustomControlGetter callback gives us a generic
            // IMyTerminalBlock. Use that as TBlock; SE's IsTypeValid
            // accepts IMyTerminalBlock as a valid mod-API interface.
            var sl = MyAPIGateway.TerminalControls
                .CreateControl<IMyTerminalControlSlider, IMyTerminalBlock>(id);
            sl.Title   = MyStringId.GetOrCompute(title);
            sl.Tooltip = MyStringId.GetOrCompute(tip);
            // Per-block getters because the dynamic-SetLimits overload
            // signature requires per-block functions, even though the
            // value is the same global cap for every block.
            sl.SetLimits(
                _ => -PanelRegistry.MirrorMaxTiltDeg,
                _ => +PanelRegistry.MirrorMaxTiltDeg);
            sl.Getter = b => yaw
                ? MirrorStorage.GetMirrorAngleX(b, LcdAppTerminalControls.ActiveSurfaceIndex(b))
                : MirrorStorage.GetMirrorAngleY(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
            sl.Setter = (b, v) =>
            {
                int idx = LcdAppTerminalControls.ActiveSurfaceIndex(b);
                if (yaw) MirrorStorage.SetMirrorAngleX(b, idx, v);
                else     MirrorStorage.SetMirrorAngleY(b, idx, v);
            };
            sl.Writer = (b, sb) =>
            {
                float v = yaw
                    ? MirrorStorage.GetMirrorAngleX(b, LcdAppTerminalControls.ActiveSurfaceIndex(b))
                    : MirrorStorage.GetMirrorAngleY(b, LcdAppTerminalControls.ActiveSurfaceIndex(b));
                sb.Append(v.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)).Append('°');
            };
            sl.Visible = IsTiltEligible;
            sl.Enabled = b => true;
            return sl;
        }

        // ── Tilt eligibility ──────────────────────────────────────────
        //
        // Mirrors the plugin's MirrorCameraPlugin.Render.ModelTiltApplier.
        // IsEligibleForMeshTilt rules:
        //   1. AlwaysEligibleSubtypes whitelist (Corner LCD top/bottom variants)
        //   2. block's local AABB depth on its narrowest axis < 0.4 * gridSize
        // The plugin's check also requires the screen normal to be
        // axis-aligned — we can't easily compute that from the mod side
        // without the plugin's ScreenPlaneResolver, so we approximate
        // by taking the smallest extent of the LocalAABB. A block thin
        // in any axis is almost always thin on its screen face too,
        // which is the case that matters.

        static readonly System.Collections.Generic.HashSet<string> s_alwaysEligibleSubtypes
            = new System.Collections.Generic.HashSet<string>
        {
            "LargeBlockCorner_LCD_1",
            "LargeBlockCorner_LCD_2",
            "SmallBlockCorner_LCD_1",
            "SmallBlockCorner_LCD_2",
        };

        const float ThinDepthFractionOfGrid = 0.4f;

        static bool IsTiltEligible(IMyTerminalBlock block)
        {
            if (block == null) return false;
            if (s_alwaysEligibleSubtypes.Contains(block.BlockDefinition.SubtypeName)) return true;

            var cubeBlock = block as VRage.Game.ModAPI.IMyCubeBlock;
            var grid = cubeBlock?.CubeGrid;
            if (grid == null) return false;

            var aabb = block.PositionComp.LocalAABB;
            float dx = aabb.Max.X - aabb.Min.X;
            float dy = aabb.Max.Y - aabb.Min.Y;
            float dz = aabb.Max.Z - aabb.Min.Z;
            float minDepth = System.Math.Min(dx, System.Math.Min(dy, dz));

            return minDepth < grid.GridSize * ThinDepthFractionOfGrid;
        }
    }
}
