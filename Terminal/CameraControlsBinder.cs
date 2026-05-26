using System;
using Sandbox.Common.ObjectBuilders;
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
    /// controls (<see cref="CameraScript.RegisterTerminalControls"/>),
    /// and camera blocks for the per-camera zoom slider
    /// (<see cref="CameraBlockControls.RegisterTerminalControls"/>).
    /// Picking a per-block-type lifecycle (rather than
    /// <c>LoadData</c>) keeps registration scoped to "only when blocks
    /// of that type exist in the world".
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
            // Each DoRegister implementation has its own static-flag
            // idempotency guard, so re-calling here per block is cheap
            // (one bool check + return).
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
        protected override void DoRegister() => CameraScript.RegisterTerminalControls();
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Cockpit), false)]
    public class CameraControlsBinderCockpit : CameraControlsBinder
    {
        protected override void DoRegister() => CameraScript.RegisterTerminalControls();
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_MyProgrammableBlock), false)]
    public class CameraControlsBinderProgrammableBlock : CameraControlsBinder
    {
        protected override void DoRegister() => CameraScript.RegisterTerminalControls();
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_CameraBlock), false)]
    public class CameraControlsBinderCameraBlock : CameraControlsBinder
    {
        protected override void DoRegister() => CameraBlockControls.RegisterTerminalControls();
    }
}
