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

    // Generic-LCD hosts: MyFunctionalBlock creates a multi-panel
    // component for any definition with ScreenAreas, so these blocks
    // carry text surfaces and can run the Camera app, but each is its
    // own terminal type and needs its own binder (see class doc).

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_ButtonPanel), false)]
    public class CameraControlsBinderButtonPanel : CameraControlsBinder
    {
        protected override void DoRegister() => CameraScript.RegisterFor<SpaceEngineers.Game.ModAPI.IMyButtonPanel>();
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_MedicalRoom), false)]
    public class CameraControlsBinderMedicalRoom : CameraControlsBinder
    {
        protected override void DoRegister() => CameraScript.RegisterFor<SpaceEngineers.Game.ModAPI.IMyMedicalRoom>();
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_StoreBlock), false)]
    public class CameraControlsBinderStoreBlock : CameraControlsBinder
    {
        protected override void DoRegister() => CameraScript.RegisterFor<IMyStoreBlock>();
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_TurretControlBlock), false)]
    public class CameraControlsBinderTurretControlBlock : CameraControlsBinder
    {
        protected override void DoRegister() => CameraScript.RegisterFor<SpaceEngineers.Game.ModAPI.IMyTurretControlBlock>();
    }

    // Only the Console subtype declares screens, but the binder is per
    // object-builder type; the Visible predicate hides the controls on
    // projectors without a surface.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Projector), false)]
    public class CameraControlsBinderProjector : CameraControlsBinder
    {
        protected override void DoRegister() => CameraScript.RegisterFor<IMyProjector>();
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_CameraBlock), false)]
    public class CameraControlsBinderCameraBlock : CameraControlsBinder
    {
        protected override void DoRegister() => CameraBlockControls.RegisterTerminalControls();
    }
}
