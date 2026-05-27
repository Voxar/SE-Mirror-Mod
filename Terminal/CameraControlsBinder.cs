using System;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;

namespace MirrorCameraMod.Terminal
{
    /// <summary>
    /// Idempotent first-frame trigger for the Mirror mod's terminal-
    /// controls registration. Bound to every block type the mod adds
    /// controls to — text-surface providers for the Camera-app
    /// controls (<see cref="CameraScript.RegisterFor{TBlock}"/>), and
    /// camera blocks for the per-camera zoom slider
    /// (<see cref="CameraBlockControls.RegisterTerminalControls"/>).
    ///
    /// <para>Per-block-type registration is mandatory: SE invokes
    /// <c>MyTextPanel.CreateTerminalControls</c> (and equivalents) as
    /// an INSTANCE override from
    /// <c>MyTerminalBlock.BeforeGameLogicInit</c>, gated by
    /// <c>AreControlsCreated&lt;MyTextPanel&gt;()</c>. If the mod
    /// AddControls into <c>m_controls[MyTextPanel]</c> BEFORE the
    /// first text panel is constructed, that gate flips true and SE
    /// skips its native Title/Content controls. Binding per-block-
    /// type means our UpdateOnceBeforeFrame fires strictly AFTER the
    /// block's BeforeGameLogicInit — natives already in place.</para>
    /// </summary>
    public abstract class CameraControlsBinder : MyGameLogicComponent
    {
        protected abstract void DoRegister();

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();
            // CameraScript.RegisterFor and CameraBlockControls.
            // RegisterTerminalControls have their own per-type
            // idempotency guards, so re-calling per block is cheap.
            try { DoRegister(); }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] Terminal controls registration failed: " + ex);
            }
        }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_TextPanel), false)]
    public class CameraControlsBinderTextPanel : CameraControlsBinder
    {
        protected override void DoRegister() => CameraScript.RegisterFor<IMyTextPanel>();
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Cockpit), false)]
    public class CameraControlsBinderCockpit : CameraControlsBinder
    {
        protected override void DoRegister() => CameraScript.RegisterFor<IMyCockpit>();
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_MyProgrammableBlock), false)]
    public class CameraControlsBinderProgrammableBlock : CameraControlsBinder
    {
        protected override void DoRegister() => CameraScript.RegisterFor<IMyProgrammableBlock>();
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_CameraBlock), false)]
    public class CameraControlsBinderCameraBlock : CameraControlsBinder
    {
        protected override void DoRegister() => CameraBlockControls.RegisterTerminalControls();
    }
}
