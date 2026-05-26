using System;
using System.Collections.Generic;
using MirrorCameraMod.Settings;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
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
    /// <para>Eligibility (block must be thin / whitelisted) delegates
    /// to <see cref="TiltTerminalControls.IsTiltEligible"/> so the
    /// slider gate and the tilt gate are the same check.</para>
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
        private const float Deg2Rad = (float)(Math.PI / 180.0);

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
        private float   _lastDegX, _lastDegY, _lastDegZ;
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

            // Tilt controls register on first text-panel frame.
            // Idempotent (static flag inside TiltTerminalControls).
            try { TiltTerminalControls.RegisterTerminalControls(); }
            catch (Exception ex)
            {
                MyLog.Default.WriteLine("[MirrorMod] Tilt controls registration failed: " + ex);
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
            float degZ = MirrorStorage.GetMirrorAngleZ(Entity, 0);
            Apply(degX, degY, degZ);
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

        private void Apply(float degX, float degY, float degZ)
        {
            if (!_eligible) return;
            if (degX == _lastDegX && degY == _lastDegY && degZ == _lastDegZ) return;

            if (degX == 0f && degY == 0f && degZ == 0f)
            {
                if (_tilted)
                {
                    var restore = _baseLocal;
                    WriteAndRefresh(ref restore);
                    _tilted = false;
                }
                _lastDegX = 0f;
                _lastDegY = 0f;
                _lastDegZ = 0f;
                return;
            }

            // Slider semantics: increasing pitch tilts the screen TOP
            // toward the viewer; increasing yaw swings the screen to
            // the viewer's right; increasing roll banks the screen
            // clockwise from the viewer's POV. Internal rotation math
            // produces the opposite direction by default, so apply
            // the negated slider values. The originals remain in the
            // cache below so the equality check matches slider values
            // one-to-one.
            float applyX = -degX;
            float applyY = -degY;
            float applyZ = -degZ;

            // Yaw/pitch pivot: opposite side from the lean direction.
            Vector3 yawPitchPivot = _localCenter
                          - Math.Sign(applyX) * _halfRight * _localRightUnit
                          - Math.Sign(applyY) * _halfUp    * _localUpUnit;

            // Roll pivot: for "edge-mounted" panels (mesh hugs one
            // side of the block along screen Up or Right), roll
            // pivots at the corner where the panel meets the block
            // boundary — sign(degZ) picks the left vs right corner
            // along screen Right. For centred panels, roll uses
            // screen centre (an in-plane spin around the middle).
            const float EdgeOffsetThreshold = 0.05f;
            float meshUpProj    = Vector3.Dot(_localCenter, _localUpUnit);
            float meshRightProj = Vector3.Dot(_localCenter, _localRightUnit);
            float upEdgeSign    = Math.Abs(meshUpProj)    > EdgeOffsetThreshold ? Math.Sign(meshUpProj)    : 0f;
            float rightEdgeSign = Math.Abs(meshRightProj) > EdgeOffsetThreshold ? Math.Sign(meshRightProj) : 0f;
            Vector3 rollPivot;
            if (upEdgeSign != 0f || rightEdgeSign != 0f)
            {
                // Anchor at the block-boundary edge on whichever axis
                // the mesh is offset; flip across screen-right based
                // on sign(degZ) so roll+ → left corner, roll- → right.
                rollPivot = _localCenter
                          + upEdgeSign    * _halfUp    * _localUpUnit
                          + rightEdgeSign * _halfRight * _localRightUnit
                          + (-Math.Sign(degZ)) * _halfRight * _localRightUnit;
            }
            else
            {
                rollPivot = _localCenter;
            }

            Vector3 screenNormal = Vector3.Cross(_localRightUnit, _localUpUnit);

            float yawRad   = applyX * Deg2Rad;
            float pitchRad = applyY * Deg2Rad;
            float rollRad  = applyZ * Deg2Rad;

            Matrix rYaw   = Matrix.CreateFromAxisAngle(_localUpUnit,    -yawRad   * _outwardSign);
            Matrix rPitch = Matrix.CreateFromAxisAngle(_localRightUnit, pitchRad * _outwardSign);
            Matrix rRoll  = Matrix.CreateFromAxisAngle(screenNormal,    rollRad);

            // Roll first (in-plane around rollPivot), then yaw/pitch
            // (3D tilt around the lean corner). Row-vector convention:
            // M1 * M2 applies M1 first.
            Matrix rollMtx = Matrix.CreateTranslation(-rollPivot)
                           * rRoll
                           * Matrix.CreateTranslation(rollPivot);
            Matrix ypMtx   = Matrix.CreateTranslation(-yawPitchPivot)
                           * rPitch * rYaw
                           * Matrix.CreateTranslation(yawPitchPivot);

            Matrix tilted = Matrix.Normalize(rollMtx * ypMtx * _baseLocal);

            WriteAndRefresh(ref tilted);
            _tilted   = true;
            _lastDegX = degX;
            _lastDegY = degY;
            _lastDegZ = degZ;
        }

        // ── Resolve ──────────────────────────────────────────────────────

        // Subtypes where screen-right points along block -X instead of
        // block +X. Empirically determined; the universal rule is that
        // the pitch axis is always block-local X, but the SIGN of
        // "screen right" differs for these three. Visual effect: pitch
        // appears inverted on these blocks — user-acknowledged.
        private static readonly HashSet<string> ScreenRightFlipSubtypes =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "LargeBlockCorner_LCD_1",       // LG Corner LCD Top
                "SmallBlockCorner_LCD_1",       // SG Corner LCD Top
                "SmallBlockCorner_LCD_2",       // SG Corner LCD Bottom
                "LargeBlockCorner_LCD_Flat_2",  // LG Corner LCD Flat Bottom
            };

        // Subtypes whose mount-based screen-out rule gets the wrong
        // axis. Transparent LCD's default mount is on the bottom face
        // (-Y) but the screen actually faces +Z — so the standard
        // negate-default-mount-normal rule yields +Y instead of +Z.
        // Hardcode the screen-out direction for these.
        private static readonly Dictionary<string, Vector3> ScreenOutOverrides =
            new Dictionary<string, Vector3>(StringComparer.Ordinal)
            {
                { "TransparentLCDLarge", new Vector3(0, 0, 1) },
                { "TransparentLCDSmall", new Vector3(0, 0, 1) },
            };

        // Reused across resolves to avoid per-call allocation.
        private static readonly Dictionary<string, IMyModelDummy> s_dummyBuf =
            new Dictionary<string, IMyModelDummy>();

        private void ResolveAxes()
        {
            _eligible = false;
            if (_block?.CubeGrid == null) return;

            if (!TiltTerminalControls.IsTiltEligible(_block as Sandbox.ModAPI.IMyTerminalBlock))
                return;

            string subtype = _block.BlockDefinition.SubtypeName;

            var aabb = Entity.PositionComp.LocalAABB;
            float dx = aabb.Max.X - aabb.Min.X;
            float dy = aabb.Max.Y - aabb.Min.Y;
            float dz = aabb.Max.Z - aabb.Min.Z;

            // ── Screen-outward direction ──
            // Default: derived from MountPoint.Normal — each Normal
            // points OUT of a mount face, so the screen sits opposite
            // the "default" mount (or the LAST mount when no default
            // is set). Cardinal axis only.
            //
            // Override: subtypes in ScreenOutOverrides hardcode the
            // direction because their mount layout doesn't match
            // their actual screen face (e.g. Transparent LCD's
            // default mount is on -Y but the screen faces +Z).
            Vector3 screenOut;
            if (subtype != null && ScreenOutOverrides.TryGetValue(subtype, out screenOut))
            {
                screenOut = Vector3.Normalize(screenOut);
            }
            else
            {
                Vector3I mountNormal = FindScreenOutMountNormal();
                screenOut = new Vector3(-mountNormal.X, -mountNormal.Y, -mountNormal.Z);
                if (screenOut.LengthSquared() < 1e-6f) return;
                screenOut = Vector3.Normalize(screenOut);
            }

            // ── Screen-right direction ──
            // Pitch axis is ALWAYS block-local X. Screen-right = +X for
            // most LCDs; 3 subtypes have it flipped to -X (see
            // ScreenRightFlipSubtypes). Pitch direction inverts visually
            // on those — user-acknowledged trade.
            float rightSign = (subtype != null && ScreenRightFlipSubtypes.Contains(subtype)) ? -1f : +1f;
            Vector3 screenRight = new Vector3(rightSign, 0, 0);

            // ── Screen-up direction ──
            // Cross product gives the magnitude direction; the SIGN is
            // refined by projecting the detector translation onto the
            // candidate. When the detector sits strongly on one side
            // of the yaw axis, that side is "up". When the detector is
            // centred along the yaw axis (e.g. Text Panel LG with
            // detectorY=0), the projection is ~0 and we trust the
            // cross product result.
            Vector3 screenUp = Vector3.Cross(screenOut, screenRight);
            if (screenUp.LengthSquared() < 1e-6f) return;
            screenUp = Vector3.Normalize(screenUp);

            Vector3 detectorTranslation = (aabb.Min + aabb.Max) * 0.5f; // fallback
            Vector3 dt;
            if (TryGetDetectorTranslation(out dt))
            {
                detectorTranslation = dt;
                float proj = Vector3.Dot(detectorTranslation, screenUp);
                if (proj < -0.05f) screenUp = -screenUp;
            }

            // ── Half-extents from AABB projection, clamped to block ──
            // Block cube footprint is block.Size * gridSize. The pivot
            // must not extend past the cube boundary, so the half-
            // extents along screen-right / screen-up are clamped to
            // the block's projected half-size along those directions.
            float blockHalfX = 0.5f * _block.CubeGrid.GridSize;
            float blockHalfY = 0.5f * _block.CubeGrid.GridSize;
            float blockHalfZ = 0.5f * _block.CubeGrid.GridSize;
            MyCubeBlockDefinition cubeDef;
            if (MyDefinitionManager.Static.TryGetDefinition<MyCubeBlockDefinition>(
                    _block.BlockDefinition, out cubeDef) && cubeDef != null)
            {
                blockHalfX = cubeDef.Size.X * _block.CubeGrid.GridSize * 0.5f;
                blockHalfY = cubeDef.Size.Y * _block.CubeGrid.GridSize * 0.5f;
                blockHalfZ = cubeDef.Size.Z * _block.CubeGrid.GridSize * 0.5f;
            }

            float aabbHalfRight  = ProjectHalfExtent(screenRight, dx, dy, dz);
            float aabbHalfUp     = ProjectHalfExtent(screenUp,    dx, dy, dz);
            float blockHalfRight = ProjectHalfExtent(screenRight, blockHalfX * 2f, blockHalfY * 2f, blockHalfZ * 2f);
            float blockHalfUp    = ProjectHalfExtent(screenUp,    blockHalfX * 2f, blockHalfY * 2f, blockHalfZ * 2f);

            _localCenter    = detectorTranslation;
            _localRightUnit = screenRight;
            _localUpUnit    = screenUp;
            _halfRight      = Math.Min(aabbHalfRight, blockHalfRight);
            _halfUp         = Math.Min(aabbHalfUp,    blockHalfUp);
            _outwardSign    = +1f; // signs already baked into screenRight / screenUp
            _eligible       = true;
        }

        // Pick the mount-point Normal whose negation gives the screen-
        // outward direction. Prefer mount.Default=true; otherwise fall
        // back to the LAST mount in the array (SE's own auto-rotate
        // selection when no default is marked).
        private Vector3I FindScreenOutMountNormal()
        {
            MyCubeBlockDefinition cubeDef;
            if (!MyDefinitionManager.Static.TryGetDefinition<MyCubeBlockDefinition>(
                    _block.BlockDefinition, out cubeDef) || cubeDef == null)
                return new Vector3I(0, 0, -1);

            var mounts = cubeDef.MountPoints;
            if (mounts == null || mounts.Length == 0)
                return new Vector3I(0, 0, -1);

            for (int i = 0; i < mounts.Length; i++)
                if (mounts[i].Default) return mounts[i].Normal;
            return mounts[mounts.Length - 1].Normal;
        }

        // Detector dummy's translation is the trusted screen-centre
        // anchor (the rest of the matrix is unreliable per empirical
        // tests). Looks for detector_textpanel* first, falls back to
        // detector_terminal* (HoloLCD / ConsoleModule variants).
        private bool TryGetDetectorTranslation(out Vector3 translation)
        {
            translation = Vector3.Zero;
            IMyModelDummy dummy;
            if (!TryGetDetectorDummy(out dummy)) return false;
            translation = dummy.Matrix.Translation;
            return true;
        }

        // Direction-only signal from the detector dummy's Forward axis.
        // Used only for the 4 non-Flat corner LCDs whose screen normal
        // is genuinely diagonal — the dummy's Forward direction is the
        // diagonal screen-outward direction in block-local frame.
        // Magnitude unreliable, so callers normalize.
        private bool TryGetDetectorForward(out Vector3 forward)
        {
            forward = Vector3.Zero;
            IMyModelDummy dummy;
            if (!TryGetDetectorDummy(out dummy)) return false;
            forward = dummy.Matrix.Forward;
            return true;
        }

        private bool TryGetDetectorDummy(out IMyModelDummy dummy)
        {
            dummy = null;
            var model = Entity?.Model;
            if (model == null) return false;

            s_dummyBuf.Clear();
            try { model.GetDummies(s_dummyBuf); }
            catch { return false; }
            if (s_dummyBuf.Count == 0) return false;

            foreach (var kv in s_dummyBuf)
            {
                string n = kv.Key;
                if (n == null) continue;
                if (n.StartsWith("detector_textpanel", StringComparison.OrdinalIgnoreCase)
                 || n.StartsWith("detector_terminal",  StringComparison.OrdinalIgnoreCase))
                {
                    dummy = kv.Value;
                    return true;
                }
            }
            return false;
        }

        // Max |dot(v, d)| over the AABB vertices = |d.X|*halfX + |d.Y|*halfY + |d.Z|*halfZ.
        private static float ProjectHalfExtent(Vector3 d, float dx, float dy, float dz)
        {
            return Math.Abs(d.X) * dx * 0.5f
                 + Math.Abs(d.Y) * dy * 0.5f
                 + Math.Abs(d.Z) * dz * 0.5f;
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
