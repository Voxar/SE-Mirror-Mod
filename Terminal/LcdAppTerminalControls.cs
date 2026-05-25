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
    /// Per-block terminal-controls dispatcher. Subscribes to SE's
    /// <see cref="IMyTerminalControls.CustomControlGetter"/> event and,
    /// for each block whose active surface is running one of this mod's
    /// scripts, calls the script's own
    /// <c>InsertCustomControls(controls, anchorIdx)</c> to inject its
    /// own controls just before the LCD's color pickers (so the
    /// camera-source / mirror-tilt sliders appear at the most useful
    /// place in the terminal stack instead of at the bottom).
    ///
    /// <para>This dispatcher ALSO removes the LCD's font / background
    /// color pickers from the list when our script is active — those
    /// controls are no-ops when a TSS replaces the surface's content,
    /// so showing them just adds noise.</para>
    ///
    /// <para>Pure <c>CustomControlGetter</c> path on purpose: per-block
    /// runtime mutation, doesn't touch
    /// <see cref="MyAPIGateway.TerminalControls.AddControl{TBlock}"/>'s
    /// per-type list, can't trip
    /// <c>AreControlsCreated&lt;MyTextPanel&gt;</c> and wipe defaults.</para>
    /// </summary>
    public sealed class LcdAppTerminalControls
    {
        // Preferred insertion anchor: directly AFTER the "Script"
        // listbox (= the app-selection list). This is where the user
        // expects our app-specific controls to land — right under the
        // dropdown that activated us.
        const string ScriptListId = "Script";

        // Fallback insertion anchors (used only if "Script" isn't in
        // the callback list for some reason — e.g. SE's order-of-events
        // changed, or a multi-callback subscriber adds Script after us).
        // We insert BEFORE whichever of these appears first.
        static readonly string[] s_insertBeforeFallback =
        {
            "ScriptForegroundColor", "ScriptBackgroundColor",
            "FontColor",             "BackgroundColor",
        };

        // Controls REMOVED from the list when our script is active.
        // Same set as the insertion anchors: when a TSS is driving the
        // surface, the color pickers don't affect anything.
        static readonly System.Collections.Generic.HashSet<string> s_removeWhenActive =
            new System.Collections.Generic.HashSet<string>
            {
                "ScriptForegroundColor", "ScriptBackgroundColor",
                "FontColor",             "BackgroundColor",
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

        // ── Dispatch ───────────────────────────────────────────────────

        static void OnCustomControlGetter(IMyTerminalBlock block, List<IMyTerminalControl> controls)
        {
            if (block == null) return;
            var provider = block as IMyTextSurfaceProvider;
            if (provider == null || provider.SurfaceCount <= 0) return;

            int idx  = ActiveSurfaceIndex(block);
            if (idx < 0 || idx >= provider.SurfaceCount) return;

            var surf = provider.GetSurface(idx);
            if (surf == null) return;
            // Surface must actually be running a script (ContentType.SCRIPT)
            // for our controls to mean anything — surf.Script holds the
            // last-selected script id even when the surface has been
            // switched back to TEXT_AND_IMAGE or NONE, which would
            // otherwise leak Mirror/Camera controls onto non-app surfaces.
            if (surf.ContentType != ContentType.SCRIPT) return;

            IReadOnlyList<IMyTerminalControl> appControls;
            if (surf.Script == MirrorSession.MirrorScriptId)
                appControls = MirrorScript.GetCustomControls();
            else if (surf.Script == MirrorSession.CameraScriptId)
                appControls = CameraScript.GetCustomControls();
            else
                return;

            int insertAt = FindInsertIndex(controls);
            for (int i = 0; i < appControls.Count; i++)
                controls.Insert(insertAt + i, appControls[i]);

            // Remove the now-unused color pickers — TSS-driven surfaces
            // don't render text the FontColor would apply to, and the
            // ScriptColor pickers only matter when the user is editing
            // the script's text settings (which our scripts don't use).
            for (int i = controls.Count - 1; i >= 0; i--)
                if (s_removeWhenActive.Contains(controls[i].Id))
                    controls.RemoveAt(i);
        }

        static int FindInsertIndex(List<IMyTerminalControl> controls)
        {
            // Primary: right after the LAST "Script" id in the list.
            // There are TWO controls with this id for text panels — one
            // typed MyFunctionalBlock (inherited via MyMultiTextPanelComponent.
            // CreateTerminalControls<MyFunctionalBlock>) at a low index,
            // and one typed MyTextPanel (added by MyLcdSurfaceComponent's
            // per-component loop in MyTextPanel.CreateTerminalControls)
            // at a high index. The high one is the user-visible "App"
            // listbox; the low one is shadowed. Searching backwards
            // lands us right after the visible one. (Verified by
            // dumping MyTerminalControlFactory.m_controls via raw_batch:
            // both "Script" ids present, differ by generic block type.)
            for (int i = controls.Count - 1; i >= 0; i--)
                if (controls[i].Id == ScriptListId) return i + 1;
            // Fallback: before the first color picker (also searched
            // last-first, for the same reason).
            for (int i = controls.Count - 1; i >= 0; i--)
            {
                var id = controls[i].Id;
                for (int a = 0; a < s_insertBeforeFallback.Length; a++)
                    if (id == s_insertBeforeFallback[a]) return i;
            }
            return controls.Count;
        }

        // ── Custom action getter ──────────────────────────────────────

        // Fires whenever SE enumerates per-block actions for the toolbar
        // (via MyTerminalControls.Static.GetActions(block), called from
        // MyToolbarItemTerminalBlock during binding / activation). Same
        // dispatch model as controls: detect active script, append the
        // script's owned actions to the list.
        static void OnCustomActionGetter(IMyTerminalBlock block, List<IMyTerminalAction> actions)
        {
            if (block == null) return;
            var provider = block as IMyTextSurfaceProvider;
            if (provider == null || provider.SurfaceCount <= 0) return;

            int idx  = ActiveSurfaceIndex(block);
            if (idx < 0 || idx >= provider.SurfaceCount) return;

            var surf = provider.GetSurface(idx);
            if (surf == null) return;
            if (surf.ContentType != ContentType.SCRIPT) return;

            IReadOnlyList<IMyTerminalAction> appActions;
            if (surf.Script == MirrorSession.MirrorScriptId)
                appActions = MirrorScript.GetCustomActions();
            else if (surf.Script == MirrorSession.CameraScriptId)
                appActions = CameraScript.GetCustomActions();
            else
                return;

            for (int i = 0; i < appActions.Count; i++) actions.Add(appActions[i]);
        }

        // ── Shared helpers ─────────────────────────────────────────────

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
