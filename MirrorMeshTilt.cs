using System;
using System.Collections.Generic;
using MirrorCameraMod.Settings;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace MirrorCameraMod
{
    /// <summary>
    /// Mod-side visible-mesh tilt for LCDs — per-block
    /// <see cref="MyGameLogicComponent"/> bound to every
    /// <c>MyObjectBuilder_TextPanel</c>.
    ///
    /// <para>The mod API does not expose the screen-plane geometry
    /// (mesh material filtering lives on internal types
    /// <c>VRage.Game.Models.MyModel.GetMeshList</c>), so screen-corner-
    /// pivot tilt isn't available here. Instead we rotate the block's
    /// own local matrix around its own translation, using the block's
    /// Right (pitch axis) and Up (yaw axis). This matches the visible
    /// behaviour of dedicated LCD-tilt mods.</para>
    ///
    /// <para>Eligibility check (block must be thin) is reused from
    /// <see cref="MirrorScript.IsTiltEligible"/> via the public surface
    /// so the slider gate and the tilt gate stay in lockstep.</para>
    ///
    /// <para>Visible-mesh refresh after <c>SetLocalMatrix</c> requires
    /// power-cycling <c>block.Enabled</c> — the cube grid renderer
    /// otherwise leaves the mesh at its old position. Empirically
    /// verified, no cleaner alternative known from the mod side.</para>
    /// </summary>
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_TextPanel), false)]
    public class MirrorMeshTiltTextPanel : MirrorMeshTilt<IMyTextPanel> { }


    public class MirrorMeshTilt<T> : MyGameLogicComponent where T : IMyFunctionalBlock
    {
        private const float Deg2Rad                 = (float)(Math.PI / 180.0);
        private const float ThinDepthFractionOfGrid = 0.4f;

        private T       _block;
        private Matrix  _baseLocal;            // engine-supplied un-tilted matrix
        private bool    _hasBaseline;
        private bool    _eligible;             // passed AABB thinness gate
        // Screen-frame basis in BLOCK-local coordinates. Yaw rotates
        // around _localUpUnit, pitch around _localRightUnit. The pivot
        // is at the screen-corner in the lean direction so the mesh
        // stays inside the cube footprint (see Apply).
        private Vector3 _localRightUnit;       // screen right (block-local, unit)
        private Vector3 _localUpUnit;          // screen up (block-local, unit)
        private Vector3 _localCenter;          // block-local AABB centre
        private float   _halfRight;            // half-extent along screen right
        private float   _halfUp;               // half-extent along screen up
        // +1 or -1: which side of the AABB along the screen-normal
        // axis is the visible screen face. Determined by AABB asymmetry
        // around block origin — the mesh extends farther toward the
        // screen than the mount. Folded into the rotation sign so the
        // panel always opens outward regardless of which way the block
        // faces in its local frame.
        private float   _outwardSign;
        private float   _lastDegX, _lastDegY;
        private bool    _tilted;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            _block = (T)(object)Entity;
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();

            // Register the block-level yaw/pitch sliders on first call.
            // Idempotent (static flag on MirrorScript).
            try { MirrorScript.RegisterBlockLevelControls(); }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] RegisterBlockLevelControls failed: " + ex);
            }

            if (_block?.CubeGrid?.Physics == null) return; // projected / preview
            if (Entity?.PositionComp == null) return;

            try
            {
                _baseLocal   = Matrix.Normalize(Entity.PositionComp.LocalMatrixRef);
                _hasBaseline = true;
                ResolveAxes();
                NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] Baseline capture failed for "
                    + Entity.EntityId + ": " + ex);
            }
        }

        public override void UpdateBeforeSimulation10()
        {
            base.UpdateBeforeSimulation10();
            if (!_hasBaseline) return;

            // Vanilla IMyTextPanel = single surface.
            float degX = MirrorStorage.GetMirrorAngleX(Entity, 0);
            float degY = MirrorStorage.GetMirrorAngleY(Entity, 0);
            Apply(degX, degY);
        }

        public override void Close()
        {
            // Restore baseline on entity teardown so the mesh doesn't
            // briefly flash leaning on respawn.
            if (_tilted && _hasBaseline && Entity?.PositionComp != null)
            {
                try
                {
                    var restore = _baseLocal;
                    Entity.PositionComp.SetLocalMatrix(ref restore, source: null);
                }
                catch (Exception ex)
                {
                    MyLog.Default.WriteLine("[MirrorMod] Close restore failed for "
                        + Entity.EntityId + ": " + ex);
                }
            }
            base.Close();
        }

        private void Apply(float degX, float degY)
        {
            if (!_eligible) return;
            if (degX == _lastDegX && degY == _lastDegY) return;

            if (degX == 0f && degY == 0f)
            {
                if (_tilted)
                {
                    var restore = _baseLocal;
                    WriteAndRefresh(ref restore);
                    _tilted = false;
                }
                _lastDegX = 0f;
                _lastDegY = 0f;
                return;
            }

            // Build tilt in BLOCK-LOCAL frame, then left-multiply with
            // baseLocal. Sign / order copied from the plugin's
            // ModelTiltApplier — pitch is NEGATED so positive degY
            // pitches the top toward the viewer, and rYaw * rPitch
            // means yaw's axis stays the ORIGINAL screen-Up, not the
            // post-pitch rotated up. Math.Sign(0) puts the pivot at
            // the centre along that axis — harmless because the
            // rotation amount on that axis is also zero.
            Vector3 pivot = _localCenter
                          + Math.Sign(degX) * _halfRight * _localRightUnit
                          + Math.Sign(degY) * _halfUp    * _localUpUnit;

            float yawRad   = degX * Deg2Rad;
            float pitchRad = degY * Deg2Rad;

            Matrix rYaw   = Matrix.CreateFromAxisAngle(_localUpUnit,    -yawRad   * _outwardSign);
            Matrix rPitch = Matrix.CreateFromAxisAngle(_localRightUnit, pitchRad * _outwardSign);
            Matrix tilt   = Matrix.CreateTranslation(-pivot)
                          * rPitch * rYaw
                          * Matrix.CreateTranslation(pivot);

            Matrix tilted = Matrix.Normalize(tilt * _baseLocal);

            WriteAndRefresh(ref tilted);
            _tilted   = true;
            _lastDegX = degX;
            _lastDegY = degY;
        }

        // ── Resolve ──────────────────────────────────────────────────────

        // Vanilla LCDs whose screen face is at 45° (corner LCD top/bottom
        // variants). They don't reveal the screen orientation through the
        // AABB — the cube footprint hides the slant — but they're known
        // to be tilt-friendly. Apply default block-local axes.
        private static readonly HashSet<string> CornerLcdSubtypes =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "LargeBlockCorner_LCD_1",
                "LargeBlockCorner_LCD_2",
                "SmallBlockCorner_LCD_1",
                "SmallBlockCorner_LCD_2",
            };

        private void ResolveAxes()
        {
            _eligible = false;
            if (_block?.CubeGrid == null) return;

            string subtype = _block.BlockDefinition.SubtypeName;
            bool whitelisted = subtype != null && CornerLcdSubtypes.Contains(subtype);

            // Screen-normal axis = thinnest local AABB extent. LCDs are
            // flat by definition. Corner LCD subtypes have 45° screens
            // that don't manifest in the AABB — they're whitelisted and
            // fall back to block-local Z (mwm convention default).
            var aabb = Entity.PositionComp.LocalAABB;
            float dx = aabb.Max.X - aabb.Min.X;
            float dy = aabb.Max.Y - aabb.Min.Y;
            float dz = aabb.Max.Z - aabb.Min.Z;
            float thinThreshold = _block.CubeGrid.GridSize * ThinDepthFractionOfGrid;

            bool thinX = dx < thinThreshold;
            bool thinY = dy < thinThreshold;
            bool thinZ = dz < thinThreshold;

            if (!whitelisted && !(thinX || thinY || thinZ))
            {
                // Full-cube LCD (Inset LCD, etc.) — tilting would
                // intersect neighbours.
                return;
            }

            // Choose the screen-normal axis.
            float minD = Math.Min(dx, Math.Min(dy, dz));
            int normalAxis;  // 0=X, 1=Y, 2=Z
            if (whitelisted && !(thinX || thinY || thinZ))      normalAxis = 2;
            else if (dx == minD)                                normalAxis = 0;
            else if (dy == minD)                                normalAxis = 1;
            else                                                normalAxis = 2;

            // Assign screen Right and Up to the two remaining block axes.
            // Convention: yaw around the axis closer to "up" (block Y
            // when available, else Z) and pitch around the other. The
            // half-extents along Right/Up come straight from the AABB.
            _localCenter = (aabb.Min + aabb.Max) * 0.5f;
            switch (normalAxis)
            {
                case 0: // screen normal = block X
                    _localRightUnit = new Vector3(0, 0, 1);   // block Z
                    _localUpUnit    = new Vector3(0, 1, 0);   // block Y
                    _halfRight      = dz * 0.5f;
                    _halfUp         = dy * 0.5f;
                    _outwardSign    = aabb.Max.X >= -aabb.Min.X ? +1f : -1f;
                    break;
                case 1: // screen normal = block Y
                    _localRightUnit = new Vector3(1, 0, 0);   // block X
                    _localUpUnit    = new Vector3(0, 0, 1);   // block Z
                    _halfRight      = dx * 0.5f;
                    _halfUp         = dz * 0.5f;
                    _outwardSign    = aabb.Max.Y >= -aabb.Min.Y ? +1f : -1f;
                    break;
                default: // screen normal = block Z (vanilla LCDs)
                    _localRightUnit = new Vector3(1, 0, 0);   // block X
                    _localUpUnit    = new Vector3(0, 1, 0);   // block Y
                    _halfRight      = dx * 0.5f;
                    _halfUp         = dy * 0.5f;
                    _outwardSign    = aabb.Max.Z >= -aabb.Min.Z ? +1f : -1f;
                    break;
            }
            _eligible = true;
        }

        private void WriteAndRefresh(ref Matrix local)
        {
            try
            {
                Entity.PositionComp.SetLocalMatrix(ref local, source: null);
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] SetLocalMatrix failed for "
                    + Entity.EntityId + ": " + ex);
                return;
            }

            // Power-cycle Enabled to force the cube grid renderer to
            // pick up the new matrix. Without this the matrix changes
            // but the visible mesh stays put.
            try
            {
                bool wasEnabled = _block.Enabled;
                _block.Enabled = !wasEnabled;
                _block.Enabled = wasEnabled;
            }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] Enabled-toggle refresh failed for "
                    + Entity.EntityId + ": " + ex);
            }
        }
    }
}
