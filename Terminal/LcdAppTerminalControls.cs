using System.Collections.Generic;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.GUI.TextPanel;
using IMyTerminalBlock       = Sandbox.ModAPI.IMyTerminalBlock;
using IMyTextSurfaceProvider = Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider;

namespace MirrorCameraMod.Terminal
{
    /// <summary>
    /// Per-block terminal-controls dispatcher. Controls and actions are
    /// registered block-level by each owning class's static
    /// <c>RegisterTerminalControls()</c> via
    /// <see cref="MyAPIGateway.TerminalControls.AddControl{TBlock}"/>
    /// — so SE's terminal property table sees them. This dispatcher's
    /// only jobs are:
    /// <list type="bullet">
    ///   <item><b>Color-picker strip + Camera reorder</b> via
    ///         <see cref="IMyTerminalControls.CustomControlGetter"/>:
    ///         removes <c>FontColor</c>, <c>BackgroundColor</c>, and
    ///         their <c>ScriptForeground/Background</c> siblings when
    ///         our script is active, since TSS-driven surfaces don't
    ///         render text the FontColor would apply to. Also moves
    ///         <see cref="CameraScript.ListboxId"/> and
    ///         <see cref="CameraScript.ZoomId"/> from their
    ///         end-of-list <c>AddControl</c> position to directly under
    ///         the "Script" listbox. Pure list mutation only — no new
    ///         control instances created here, as creating-via-getter
    ///         caused other issues.</item>
    ///   <item><b>Camera action gating</b> via
    ///         <see cref="IMyTerminalControls.CustomActionGetter"/>:
    ///         emits the Camera-app action set on single-surface
    ///         blocks (i.e. text panels) when their surface is running
    ///         the Camera script. Multi-surface providers (cockpits,
    ///         programmable blocks) get no per-screen camera actions —
    ///         the menu got cluttered in playtest, and the camera
    ///         block's own zoom slider plus the global Mirror.Camera
    ///         property cover everything they'd want.</item>
    /// </list>
    /// </summary>
    public sealed class LcdAppTerminalControls
    {
        const string ScriptListId = "Script";

        static readonly HashSet<string> s_removeWhenActive = new HashSet<string>
        {
            "ScriptForegroundColor", "ScriptBackgroundColor",
            "FontColor",             "BackgroundColor",
        };

        static readonly HashSet<string> s_cameraOwnedIds = new HashSet<string>
        {
            CameraScript.RemoteCamerasId,
            CameraScript.ListboxId,
            CameraScript.OverrideCameraZoomId,
            CameraScript.ZoomId,
        };

        bool _hooked;

        public void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            MyAPIGateway.TerminalControls.CustomControlGetter += OnCustomControlGetter;
            MyAPIGateway.TerminalControls.CustomActionGetter  += OnCustomActionGetter;
        }

        public void Unhook()
        {
            if (!_hooked) return;
            MyAPIGateway.TerminalControls.CustomControlGetter -= OnCustomControlGetter;
            MyAPIGateway.TerminalControls.CustomActionGetter  -= OnCustomActionGetter;
            _hooked = false;
        }

        static void OnCustomControlGetter(IMyTerminalBlock block, List<IMyTerminalControl> controls)
        {
            var scriptId = GetActiveSurfaceScriptId(block);
            if (scriptId != MirrorSession.MirrorScriptId
             && scriptId != MirrorSession.CameraScriptId) return;

            if (scriptId == MirrorSession.CameraScriptId)
                RelocateUnderScript(controls, s_cameraOwnedIds);

            for (int i = controls.Count - 1; i >= 0; i--)
                if (s_removeWhenActive.Contains(controls[i].Id))
                    controls.RemoveAt(i);
        }

        // Move every control whose Id is in idsToMove from its current
        // position to directly after the LAST "Script" id in the list
        // (text panels carry two "Script"s — an inherited
        // MyFunctionalBlock one at a low index and the user-visible
        // MyTextPanel one at a high index; we want the latter).
        // Preserves the moved controls' relative order.
        static void RelocateUnderScript(List<IMyTerminalControl> controls, HashSet<string> idsToMove)
        {
            // Pull in document order so re-insertion keeps it.
            var moving = new List<IMyTerminalControl>(idsToMove.Count);
            for (int i = 0; i < controls.Count; i++)
                if (idsToMove.Contains(controls[i].Id))
                    moving.Add(controls[i]);
            if (moving.Count == 0) return;
            for (int i = controls.Count - 1; i >= 0; i--)
                if (idsToMove.Contains(controls[i].Id))
                    controls.RemoveAt(i);

            int insertAt = controls.Count;
            for (int i = controls.Count - 1; i >= 0; i--)
                if (controls[i].Id == ScriptListId) { insertAt = i + 1; break; }

            for (int i = 0; i < moving.Count; i++)
                controls.Insert(insertAt + i, moving[i]);
        }

        static void OnCustomActionGetter(IMyTerminalBlock block, List<IMyTerminalAction> actions)
        {
            // Single-surface only — multi-surface providers (cockpits,
            // PBs) skip the per-screen camera actions on purpose.
            var provider = block as IMyTextSurfaceProvider;
            if (provider == null || provider.SurfaceCount != 1) return;
            if (GetActiveSurfaceScriptId(block) != MirrorSession.CameraScriptId) return;

            var appActions = CameraScript.GetCustomActions();
            if (appActions == null) return;
            for (int i = 0; i < appActions.Count; i++) actions.Add(appActions[i]);
        }

        // Returns the script id (e.g. MirrorSession.CameraScriptId) of
        // whatever script is driving the block's currently-edited
        // surface, or null when the block is not a surface provider,
        // the surface is not in SCRIPT content mode, or no script is set.
        internal static string GetActiveSurfaceScriptId(IMyTerminalBlock block)
        {
            if (block == null) return null;
            var provider = block as IMyTextSurfaceProvider;
            if (provider == null || provider.SurfaceCount <= 0) return null;
            int idx = ActiveSurfaceIndex(block);
            if (idx < 0 || idx >= provider.SurfaceCount) return null;
            var surf = provider.GetSurface(idx);
            if (surf == null) return null;
            if (surf.ContentType != ContentType.SCRIPT) return null;
            return surf.Script;
        }

        /// <summary>Resolve the surface the user is currently editing.
        /// Multi-surface blocks expose this via
        /// <c>MyMultiTextPanelComponent.SelectedPanelIndex</c>; single-
        /// surface blocks or non-owners always resolve to 0.</summary>
        public static int ActiveSurfaceIndex(IMyTerminalBlock block)
        {
            var provider = block as IMyTextSurfaceProvider;
            if (provider == null || provider.SurfaceCount <= 1) return 0;
            var owner = block as IMyMultiTextPanelComponentOwner;
            var multi = owner?.MultiTextPanel;
            if (multi == null) return 0;
            int idx = multi.SelectedPanelIndex;
            return (idx >= 0 && idx < provider.SurfaceCount) ? idx : 0;
        }
    }
}
