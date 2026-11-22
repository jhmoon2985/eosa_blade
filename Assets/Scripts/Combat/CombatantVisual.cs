using UnityEngine;

namespace EosaBlade
{
    public enum WeaponPose
    {
        Idle,
        Guard,
        ParryFlash,
        WindupForward,
        WindupOverhead,
        WindupThrustPerilous,
        SlashSwing,
        ThrustExtend,
        GanpaDeflect,
        Staggered,
        ExecutionFinish,
        DodgeLeap,
        SpinSlash
    }

    /// <summary>
    /// Drives either a Mixamo Humanoid Animator + 3D weapon TrailRenderer (Step 2 real art)
    /// or the legacy greybox capsule pivot fallback, plus zero-GC MaterialPropertyBlock flashes.
    /// </summary>
    public class CombatantVisual : MonoBehaviour
    {
        [Header("3D Humanoid Model (Step 2)")]
        [SerializeField] private Animator humanoidAnimator;
        [SerializeField] private Transform modelRoot;
        [SerializeField] private Renderer bodyRenderer;
        [SerializeField] private Renderer weaponRenderer;
        [SerializeField] private Transform clashAnchor;
        [SerializeField] private TrailRenderer swordTrail;

        [Header("Legacy Greybox Fallback (Optional)")]
        [SerializeField] private Transform legacyWeaponPivot;

        [Header("Tint & Flash Settings")]
        [SerializeField] private Color baseBodyColor = Color.white;
        [SerializeField] private Color baseWeaponColor = Color.white;
        [SerializeField] private bool isTexturedModel = true;

        private MaterialPropertyBlock _bodyMpb;
        private MaterialPropertyBlock _weaponMpb;

        private WeaponPose _pose = WeaponPose.Idle;
        private float _facingSign = 1f; // +1 faces right (+X), -1 faces left (-X)
        private float _moveSpeed;
        private bool _isDead;
        private float _flashTimer;
        private Color _flashColor;
        private bool _perilousGlow;
        private bool _staggerPulse;
        private float _verticalHop;
        private Vector3 _baseModelLocalPos;

        // Cached Humanoid bones for procedural Sword Guard / Sword Parry (replaces shield-arm block animations!)
        private Transform _hipsBone;
        private Transform _spineBone;
        private Transform _chestBone;
        private Transform _headBone;
        private Transform _rightUpperArm;
        private Transform _rightLowerArm;
        private Transform _rightHand;
        private Transform _leftUpperArm;
        private Transform _leftLowerArm;
        private Transform _leftHand;
        private Transform _weaponRoot;
        private bool _bonesCached;

        private float _swordDefenseBlend;
        private float _swordAttackBlend;
        private float _parryAnimElapsed;
        private float _attackAnimElapsed;
        private float _staggerAnimElapsed;
        private float _parryAttemptTimer;
        private float _guardImpactTimer;
        private int _parryVariant;
        private int _attackVariant = 1;
        private const float ParryAnimDuration = 0.28f;
        private const float ParryAttemptDuration = 0.22f;
        private const float GuardImpactDuration = 0.18f;
        private const float AttackAnimDuration = 0.30f;

        // Cached Animator state hashes (zero string GC)
        private static readonly int HashIdle = Animator.StringToHash("Idle");
        private static readonly int HashRun = Animator.StringToHash("Run");
        private static readonly int HashBlockIdle = Animator.StringToHash("BlockIdle");
        private static readonly int HashParry = Animator.StringToHash("Parry");
        private static readonly int HashSlash = Animator.StringToHash("Slash");
        private static readonly int HashHeavySlash = Animator.StringToHash("HeavySlash");
        private static readonly int HashSpinSlash = Animator.StringToHash("SpinSlash");
        private static readonly int HashThrust = Animator.StringToHash("Thrust");
        private static readonly int HashImpact = Animator.StringToHash("Impact");
        private static readonly int HashJump = Animator.StringToHash("Jump");
        private static readonly int HashPowerUp = Animator.StringToHash("PowerUp");
        private static readonly int HashDeath = Animator.StringToHash("Death");

        private int _currentAnimHash;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private static readonly Color TrailNormalColor = new Color(0.91f, 0.94f, 0.97f, 0.85f);
        private static readonly Color TrailGoldColor = new Color(0.98f, 0.78f, 0.12f, 0.95f);
        private static readonly Color TrailCrimsonColor = new Color(0.92f, 0.16f, 0.16f, 0.95f);

        public void BindHumanoid(
            Animator anim,
            Transform root,
            Renderer bodyRend,
            Renderer weaponRend,
            Transform clashPoint,
            TrailRenderer trail,
            Color weaponTint)
        {
            humanoidAnimator = anim;
            modelRoot = root;
            bodyRenderer = bodyRend;
            weaponRenderer = weaponRend;
            clashAnchor = clashPoint;
            swordTrail = trail;
            baseBodyColor = Color.white;
            baseWeaponColor = weaponTint;
            isTexturedModel = true;
            legacyWeaponPivot = null;
        }

        public void BindParts(Transform body, Transform pivot, Renderer bodyRend, Renderer weaponRend, Color bodyCol, Color weaponCol)
        {
            modelRoot = body;
            legacyWeaponPivot = pivot;
            bodyRenderer = bodyRend;
            weaponRenderer = weaponRend;
            baseBodyColor = bodyCol;
            baseWeaponColor = weaponCol;
            isTexturedModel = false;
            humanoidAnimator = null;
        }

        private void Awake()
        {
            _bodyMpb = new MaterialPropertyBlock();
            _weaponMpb = new MaterialPropertyBlock();
            if (modelRoot != null) _baseModelLocalPos = modelRoot.localPosition;
            if (swordTrail != null) swordTrail.emitting = false;
            CacheHumanoidBones();
            ApplyColors(baseBodyColor, Color.black, baseWeaponColor, Color.black);
        }

        private void CacheHumanoidBones()
        {
            if (humanoidAnimator == null) return;
            _hipsBone = humanoidAnimator.GetBoneTransform(HumanBodyBones.Hips);
            _spineBone = humanoidAnimator.GetBoneTransform(HumanBodyBones.Spine);
            _chestBone = humanoidAnimator.GetBoneTransform(HumanBodyBones.Chest);
            _headBone = humanoidAnimator.GetBoneTransform(HumanBodyBones.Head);
            _rightUpperArm = humanoidAnimator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            _rightLowerArm = humanoidAnimator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            _rightHand = humanoidAnimator.GetBoneTransform(HumanBodyBones.RightHand);
            _leftUpperArm = humanoidAnimator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            _leftLowerArm = humanoidAnimator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            _leftHand = humanoidAnimator.GetBoneTransform(HumanBodyBones.LeftHand);
            _weaponRoot = clashAnchor != null && clashAnchor.parent != null
                ? clashAnchor.parent
                : (weaponRenderer != null ? weaponRenderer.transform : null);
            _bonesCached = (_rightUpperArm != null && _rightLowerArm != null && _rightHand != null);
        }

        public void SetFacing(float sign)
        {
            _facingSign = sign >= 0f ? 1f : -1f;
            if (modelRoot == null) return;

            if (humanoidAnimator != null)
            {
                // Clean 3D Y-rotation: +90 faces +X (right), -90 faces -X (left)
                modelRoot.localRotation = Quaternion.Euler(0f, _facingSign > 0f ? 90f : -90f, 0f);
            }
            else
            {
                Vector3 s = modelRoot.localScale;
                s.x = Mathf.Abs(s.x) * _facingSign;
                modelRoot.localScale = s;
            }
        }

        public void SetMoveSpeed(float speed01)
        {
            _moveSpeed = Mathf.Clamp01(speed01);
        }

        public void SetDead(bool dead)
        {
            if (_isDead == dead) return;
            _isDead = dead;
            if (dead)
            {
                PlayAnim(HashDeath, 0.08f, forceRestart: true);
                SetTrail(false, TrailNormalColor);
            }
            else
            {
                _currentAnimHash = 0;
                PlayAnim(HashIdle, 0.05f, forceRestart: true);
            }
        }

        public void SetAttackComboVariant(int variant)
        {
            _attackVariant = Mathf.Clamp(variant, 1, 3);
        }

        public void SetPose(WeaponPose pose)
        {
            bool changed = (_pose != pose);
            bool wasWindup = (_pose == WeaponPose.WindupForward ||
                              _pose == WeaponPose.WindupOverhead ||
                              _pose == WeaponPose.WindupThrustPerilous);
            _pose = pose;
            _perilousGlow = (pose == WeaponPose.WindupThrustPerilous);
            _staggerPulse = (pose == WeaponPose.Staggered);

            if (_isDead || humanoidAnimator == null) return;

            switch (pose)
            {
                case WeaponPose.Idle:
                    humanoidAnimator.speed = 1f;
                    SetTrail(false, TrailNormalColor);
                    break;

                case WeaponPose.Guard:
                    humanoidAnimator.speed = _moveSpeed > 0.1f ? 0.85f : 1.05f;
                    PlayAnim(_moveSpeed > 0.1f ? HashRun : HashBlockIdle, 0.06f, forceRestart: false);
                    _swordDefenseBlend = 1f;
                    _swordAttackBlend = 0f;
                    if (!_bonesCached) CacheHumanoidBones();
                    ApplySwordDefensePose(1f);
                    if (_parryAttemptTimer <= 0f)
                        SetTrail(false, TrailNormalColor);
                    break;

                case WeaponPose.ParryFlash:
                    humanoidAnimator.speed = 1.45f;
                    // Start right at the blade-crossing impact frame (0.06s) so during Parry Hitstop freeze, the blades are visibly locked together!
                    _parryAnimElapsed = 0.06f;
                    _parryAttemptTimer = 0f;
                    _parryVariant = (_parryVariant + 1) & 1;
                    _swordDefenseBlend = 1f;
                    _swordAttackBlend = 0f;
                    PlayAnim(HashParry, 0.03f, forceRestart: true);
                    if (!_bonesCached) CacheHumanoidBones();
                    ApplySwordDefensePose(1f);
                    SetTrail(true, TrailGoldColor);
                    break;

                case WeaponPose.WindupForward:
                    humanoidAnimator.speed = 1.15f;
                    _attackAnimElapsed = 0f;
                    _swordAttackBlend = 1f;
                    _swordDefenseBlend = 0f;
                    PlayAnim(HashSlash, 0.06f, forceRestart: true);
                    if (!_bonesCached) CacheHumanoidBones();
                    ApplySwordAttackPose(1f);
                    SetTrail(false, TrailNormalColor);
                    break;

                case WeaponPose.WindupOverhead:
                    humanoidAnimator.speed = 0.65f;
                    _attackAnimElapsed = 0f;
                    _swordAttackBlend = 1f;
                    _swordDefenseBlend = 0f;
                    PlayAnim(HashHeavySlash, 0.08f, forceRestart: true);
                    if (!_bonesCached) CacheHumanoidBones();
                    ApplySwordAttackPose(1f);
                    SetTrail(false, TrailGoldColor);
                    break;

                case WeaponPose.WindupThrustPerilous:
                    humanoidAnimator.speed = 0.55f;
                    _attackAnimElapsed = 0f;
                    _swordAttackBlend = 1f;
                    _swordDefenseBlend = 0f;
                    PlayAnim(HashThrust, 0.06f, forceRestart: true);
                    if (!_bonesCached) CacheHumanoidBones();
                    ApplySwordAttackPose(1f);
                    SetTrail(true, TrailCrimsonColor);
                    break;

                case WeaponPose.SlashSwing:
                    humanoidAnimator.speed = 1.85f;
                    // If transitioning from Boss Windup, snap immediately to bite/clash apex (0.11s) on the Hitstop frame!
                    _attackAnimElapsed = wasWindup ? 0.11f : 0.025f;
                    _swordAttackBlend = 1f;
                    _swordDefenseBlend = 0f;
                    if (_currentAnimHash != HashSlash && _currentAnimHash != HashHeavySlash)
                        PlayAnim(HashSlash, 0.04f, forceRestart: true, normalizedTimeOffset: 0.34f);
                    if (!_bonesCached) CacheHumanoidBones();
                    ApplySwordAttackPose(1f);
                    SetTrail(true, TrailNormalColor);
                    break;

                case WeaponPose.SpinSlash:
                    humanoidAnimator.speed = 1.80f;
                    _attackAnimElapsed = wasWindup ? 0.11f : 0.025f;
                    _swordAttackBlend = 1f;
                    _swordDefenseBlend = 0f;
                    PlayAnim(HashSpinSlash, 0.04f, forceRestart: true, normalizedTimeOffset: 0.30f);
                    if (!_bonesCached) CacheHumanoidBones();
                    ApplySwordAttackPose(1f);
                    SetTrail(true, TrailGoldColor);
                    break;

                case WeaponPose.ThrustExtend:
                    humanoidAnimator.speed = 1.80f;
                    _attackAnimElapsed = wasWindup ? 0.11f : 0.025f;
                    _swordAttackBlend = 1f;
                    _swordDefenseBlend = 0f;
                    if (_currentAnimHash != HashThrust)
                        PlayAnim(HashThrust, 0.04f, forceRestart: true, normalizedTimeOffset: 0.36f);
                    if (!_bonesCached) CacheHumanoidBones();
                    ApplySwordAttackPose(1f);
                    SetTrail(true, _perilousGlow ? TrailCrimsonColor : TrailGoldColor);
                    break;

                case WeaponPose.GanpaDeflect:
                    humanoidAnimator.speed = 1.45f;
                    _parryAnimElapsed = 0.06f;
                    _parryAttemptTimer = 0f;
                    _swordDefenseBlend = 1f;
                    _swordAttackBlend = 0f;
                    PlayAnim(HashParry, 0.04f, forceRestart: true);
                    if (!_bonesCached) CacheHumanoidBones();
                    ApplySwordDefensePose(1f);
                    SetTrail(true, TrailGoldColor);
                    break;

                case WeaponPose.Staggered:
                    humanoidAnimator.speed = 0.95f;
                    // Start at 0.10s (90% peak recoil jolt) so on Frame 0 of Blade-Bite Hitstop, the enemy's chest visibly wrenches back around the buried blade!
                    if (changed) _staggerAnimElapsed = 0.10f;
                    _swordAttackBlend = 1f;
                    _swordDefenseBlend = 0f;
                    PlayAnim(HashImpact, 0.04f, forceRestart: changed);
                    if (!_bonesCached) CacheHumanoidBones();
                    ApplySwordAttackPose(1f);
                    SetTrail(false, TrailNormalColor);
                    break;

                case WeaponPose.ExecutionFinish:
                    humanoidAnimator.speed = 1.75f;
                    _attackAnimElapsed = wasWindup ? 0.11f : 0.025f;
                    _swordAttackBlend = 1f;
                    _swordDefenseBlend = 0f;
                    PlayAnim(HashHeavySlash, 0.04f, forceRestart: true, normalizedTimeOffset: 0.36f);
                    if (!_bonesCached) CacheHumanoidBones();
                    ApplySwordAttackPose(1f);
                    SetTrail(true, TrailCrimsonColor);
                    break;

                case WeaponPose.DodgeLeap:
                    humanoidAnimator.speed = 1.45f;
                    _swordDefenseBlend = 0f;
                    _swordAttackBlend = 0f;
                    PlayAnim(HashJump, 0.05f, forceRestart: true);
                    SetTrail(false, TrailNormalColor);
                    break;
            }
        }

        public void SetVerticalHop(float height)
        {
            _verticalHop = Mathf.Max(0f, height);
        }

        public void TriggerFlash(Color color, float duration)
        {
            _flashColor = color;
            _flashTimer = duration;
        }

        /// <summary>
        /// Triggered immediately when the player presses the Parry button (OnParryDown) so pressing Parry
        /// ALWAYS performs a dynamic full-body sword-whip parry motion into guard (never a static arm lift!).
        /// </summary>
        public void TriggerParryAttempt()
        {
            _parryAttemptTimer = ParryAttemptDuration;
            _pose = WeaponPose.Guard;
            _swordDefenseBlend = 1f;
            if (humanoidAnimator != null && !_isDead)
            {
                humanoidAnimator.speed = 1.35f;
                PlayAnim(HashParry, 0.03f, forceRestart: true);
                if (!_bonesCached) CacheHumanoidBones();
                ApplySwordDefensePose(1f);
                SetTrail(true, TrailNormalColor);
            }
        }

        public void TriggerGuardImpact()
        {
            _guardImpactTimer = GuardImpactDuration;
        }

        public Vector3 GetBladeMidpoint()
        {
            if (!_bonesCached && humanoidAnimator != null) CacheHumanoidBones();
            if (_weaponRoot != null && clashAnchor != null)
                return Vector3.Lerp(_weaponRoot.position, clashAnchor.position, 0.55f);
            return GetClashPoint();
        }

        public Vector3 GetClashPoint()
        {
            if (clashAnchor != null)
                return clashAnchor.position;
            if (legacyWeaponPivot != null)
                return legacyWeaponPivot.position + Vector3.right * (_facingSign * 0.65f);
            return transform.position + new Vector3(_facingSign * 0.85f, 1.30f, 0f);
        }

        /// <summary>
        /// Editor/preview helper so diagnostic camera captures render the exact LateUpdate sword guard/parry pose.
        /// </summary>
        public void ApplyPoseNowForPreview(WeaponPose pose, float parryProgress = 0.32f)
        {
            _pose = pose;
            _isDead = false;
            if (!_bonesCached) CacheHumanoidBones();
            _parryAnimElapsed = Mathf.Clamp01(parryProgress) * ParryAnimDuration;
            _parryAttemptTimer = 0f;
            _guardImpactTimer = 0f;
            _swordDefenseBlend = IsSwordDefensePose(pose) ? 1f : 0f;
            ClampHipsHorizontalDrift();
            if (_swordDefenseBlend > 0.001f)
                ApplySwordDefensePose(_swordDefenseBlend);
        }

        private static bool IsSwordDefensePose(WeaponPose pose)
        {
            return pose == WeaponPose.Guard || pose == WeaponPose.ParryFlash || pose == WeaponPose.GanpaDeflect;
        }

        private static bool IsSwordAttackOrRecoilPose(WeaponPose pose)
        {
            return pose == WeaponPose.WindupForward ||
                   pose == WeaponPose.WindupOverhead ||
                   pose == WeaponPose.WindupThrustPerilous ||
                   pose == WeaponPose.SlashSwing ||
                   pose == WeaponPose.SpinSlash ||
                   pose == WeaponPose.ThrustExtend ||
                   pose == WeaponPose.ExecutionFinish ||
                   pose == WeaponPose.Staggered;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (humanoidAnimator != null)
            {
                UpdateHumanoidLocomotion(dt);
                UpdateSwordDefenseTimers(dt);
            }
            else
            {
                UpdateLegacyGreyboxPose(dt);
            }
            UpdateColorFlash(dt);
        }

        private void LateUpdate()
        {
            if (humanoidAnimator == null) return;
            if (!_bonesCached) CacheHumanoidBones();

            // Hard In-Place Hips clamp: guarantees NO Mixamo animation (Run, Slash, Thrust) can ever drift the 3D mesh through the opponent!
            ClampHipsHorizontalDrift();

            if (_isDead || !_bonesCached) return;

            if (_swordDefenseBlend > 0.001f)
            {
                ApplySwordDefensePose(_swordDefenseBlend);
            }
            else if (_swordAttackBlend > 0.001f)
            {
                ApplySwordAttackPose(_swordAttackBlend);
            }
        }

        private void ClampHipsHorizontalDrift()
        {
            if (_hipsBone == null || modelRoot == null) return;
            Vector3 localHips = modelRoot.InverseTransformPoint(_hipsBone.position);
            localHips.x = Mathf.Clamp(localHips.x, -0.08f, 0.08f);
            localHips.z = Mathf.Clamp(localHips.z, -0.10f, 0.10f);
            _hipsBone.position = modelRoot.TransformPoint(localHips);
        }

        private void UpdateSwordDefenseTimers(float dt)
        {
            bool wantDefense = !_isDead && IsSwordDefensePose(_pose);
            float rate = wantDefense ? 28f : 18f;
            _swordDefenseBlend = Mathf.Lerp(_swordDefenseBlend, wantDefense ? 1f : 0f, 1f - Mathf.Exp(-rate * dt));
            if (!wantDefense && _swordDefenseBlend < 0.01f) _swordDefenseBlend = 0f;

            bool wantAttack = !_isDead && !wantDefense && IsSwordAttackOrRecoilPose(_pose);
            float atkRate = wantAttack ? 30f : 16f;
            _swordAttackBlend = Mathf.Lerp(_swordAttackBlend, wantAttack ? 1f : 0f, 1f - Mathf.Exp(-atkRate * dt));
            if (!wantAttack && _swordAttackBlend < 0.01f) _swordAttackBlend = 0f;

            if (_pose == WeaponPose.ParryFlash || _pose == WeaponPose.GanpaDeflect)
            {
                _parryAnimElapsed = Mathf.Min(ParryAnimDuration, _parryAnimElapsed + dt);
                if (_parryAnimElapsed > 0.22f && swordTrail != null && swordTrail.emitting)
                    swordTrail.emitting = false;
            }

            if (wantAttack)
            {
                _attackAnimElapsed = Mathf.Min(AttackAnimDuration, _attackAnimElapsed + dt);
                _staggerAnimElapsed = Mathf.Min(0.45f, _staggerAnimElapsed + dt);
            }

            if (_parryAttemptTimer > 0f)
            {
                _parryAttemptTimer = Mathf.Max(0f, _parryAttemptTimer - dt);
                if (_parryAttemptTimer <= 0.06f && _pose == WeaponPose.Guard && swordTrail != null && swordTrail.emitting)
                    swordTrail.emitting = false;
            }

            if (_guardImpactTimer > 0f)
            {
                _guardImpactTimer = Mathf.Max(0f, _guardImpactTimer - dt);
            }
        }

        private void ApplySwordDefensePose(float weight)
        {
            if (modelRoot == null || _rightUpperArm == null || _rightLowerArm == null || _rightHand == null) return;

            Vector3 fwd = modelRoot.forward;
            Vector3 up = modelRoot.up;
            Vector3 right = modelRoot.right;

            // Subtle idle guard breathing so holding Guard feels alive and coiled, never frozen
            float breath = Mathf.Sin(Time.time * 5.5f);

            // 1. Dynamic Hips Crouch + Pelvis Shift + Full Spine/Chest/Head Torsion!
            float hipsDrop = -0.06f + 0.01f * breath;
            float hipsFwd = 0.03f;
            float torsoYaw = 16f + 2f * breath;
            float torsoPitch = 8f;
            float headYaw = -12f;

            Vector3 rShoulder = _rightUpperArm.position;
            float rArmLen = Vector3.Distance(rShoulder, _rightLowerArm.position) +
                            Vector3.Distance(_rightLowerArm.position, _rightHand.position);
            if (rArmLen < 0.05f) rArmLen = 0.55f;

            // Base Ready Sword Guard (조선 환도 쌍수/중상단 방어 자세)
            Vector3 targetRHand = rShoulder + fwd * (0.76f * rArmLen) - up * ((0.06f - 0.02f * breath) * rArmLen) - right * (0.10f * rArmLen);
            Vector3 rPole = right * 0.80f - up * 0.70f - fwd * 0.15f;
            Vector3 desiredBladeDir = (up * 0.72f + fwd * 0.65f - right * 0.22f).normalized;

            if (_pose == WeaponPose.Guard && _parryAttemptTimer > 0f)
            {
                // Active Parry Button Press Whip (0.00s -> 0.22s):
                // Full-body crouch + torso rotation + sweeping upward diagonal sword parry across the chest!
                float t = 1f - (_parryAttemptTimer / ParryAttemptDuration); // 0 -> 1
                float whip = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / 0.55f), 2f); // fast whip in first 55%
                float settle = t > 0.55f ? (t - 0.55f) / 0.45f : 0f;

                hipsDrop = Mathf.Lerp(-0.11f, -0.06f, settle);
                hipsFwd = Mathf.Lerp(0.08f, 0.03f, settle);
                torsoYaw = Mathf.Lerp(-24f, 16f, whip);
                torsoPitch = Mathf.Lerp(14f, 8f, settle);
                headYaw = Mathf.Lerp(18f, -12f, whip);

                // Sweep right hand from low-inside across chest to high-front guard
                Vector3 startHand = rShoulder + fwd * (0.58f * rArmLen) - up * (0.28f * rArmLen) - right * (0.42f * rArmLen);
                Vector3 apexHand = rShoulder + fwd * (0.84f * rArmLen) + up * (0.08f * rArmLen) + right * (0.08f * rArmLen);
                Vector3 startBlade = (up * 0.25f + fwd * 0.72f - right * 0.65f).normalized;
                Vector3 apexBlade = (up * 0.84f + fwd * 0.48f + right * 0.24f).normalized;

                Vector3 whippedHand = Vector3.Lerp(startHand, apexHand, whip);
                Vector3 whippedBlade = Vector3.Slerp(startBlade, apexBlade, whip);
                targetRHand = Vector3.Lerp(whippedHand, targetRHand, settle);
                desiredBladeDir = Vector3.Slerp(whippedBlade, desiredBladeDir, settle);
            }
            else if (_pose == WeaponPose.Guard && _guardImpactTimer > 0f)
            {
                // Heavy Guard-Block Impact Recoil: whole body sinks into knees & braces the blade against the blow!
                float imp = Mathf.Sin((_guardImpactTimer / GuardImpactDuration) * Mathf.PI);
                hipsDrop -= 0.07f * imp;
                hipsFwd -= 0.08f * imp;
                torsoPitch -= 9f * imp;
                torsoYaw += 10f * imp;
                targetRHand -= fwd * (0.16f * rArmLen * imp) + up * (0.05f * rArmLen * imp);
                desiredBladeDir = Vector3.Slerp(desiredBladeDir, (up * 0.88f + fwd * 0.34f - right * 0.32f).normalized, imp * 0.90f);
            }
            else if (_pose == WeaponPose.ParryFlash)
            {
                // Explosive Full-Body Perfect Parry Deflection Slash (0.00s -> 0.28s):
                // Alternates between wide inside-out upward slash (Variant 0) and high-to-low diagonal crushing deflect (Variant 1)!
                float p = Mathf.Clamp01(_parryAnimElapsed / ParryAnimDuration);
                float slashT = 1f - Mathf.Pow(1f - Mathf.Clamp01(p / 0.42f), 2f); // 0 -> 1 fast deflection slash over first 115ms
                float recoverT = p > 0.55f ? (p - 0.55f) / 0.45f : 0f;

                hipsDrop = Mathf.Lerp(-0.12f, -0.06f, recoverT);
                hipsFwd = Mathf.Lerp(-0.06f, 0.02f, recoverT);

                Vector3 startHand, endHand, startBlade, endBlade;
                if (_parryVariant == 0)
                {
                    // Variant 0: Wide Inside-to-Outside Upward Cross Slash (치켜쳐내기 — 120° blade sweep!)
                    torsoYaw = Mathf.Lerp(26f, -30f, slashT);
                    torsoPitch = Mathf.Lerp(12f, -6f, slashT);
                    headYaw = Mathf.Lerp(-18f, 22f, slashT);

                    startHand = rShoulder + fwd * (0.64f * rArmLen) - up * (0.16f * rArmLen) - right * (0.36f * rArmLen);
                    endHand = rShoulder + fwd * (0.86f * rArmLen) + up * (0.24f * rArmLen) + right * (0.34f * rArmLen);
                    startBlade = (up * 0.45f + fwd * 0.68f - right * 0.58f).normalized;
                    endBlade = (up * 0.72f + fwd * 0.46f + right * 0.52f).normalized;
                }
                else
                {
                    // Variant 1: High-Outside to Low-Forward Diagonal Crushing Deflect (눌러쳐내기!)
                    torsoYaw = Mathf.Lerp(-26f, 28f, slashT);
                    torsoPitch = Mathf.Lerp(-4f, 16f, slashT);
                    headYaw = Mathf.Lerp(18f, -20f, slashT);

                    startHand = rShoulder + fwd * (0.72f * rArmLen) + up * (0.28f * rArmLen) + right * (0.28f * rArmLen);
                    endHand = rShoulder + fwd * (0.88f * rArmLen) - up * (0.08f * rArmLen) - right * (0.24f * rArmLen);
                    startBlade = (up * 0.85f + fwd * 0.38f + right * 0.36f).normalized;
                    endBlade = (up * 0.38f + fwd * 0.82f - right * 0.42f).normalized;
                }

                Vector3 activeHand = Vector3.Lerp(startHand, endHand, slashT);
                Vector3 activeBlade = Vector3.Slerp(startBlade, endBlade, slashT);
                targetRHand = Vector3.Lerp(activeHand, targetRHand, recoverT * 0.65f);
                desiredBladeDir = Vector3.Slerp(activeBlade, desiredBladeDir, recoverT * 0.65f);
                rPole = Vector3.Slerp(rPole, right * 0.90f - up * 0.35f - fwd * 0.15f, slashT);
            }
            else if (_pose == WeaponPose.GanpaDeflect)
            {
                // Ganpa / Mapae counter: deep lunge crouch + wide outward sword sweep + left-hand Mapae thrust!
                hipsDrop = -0.12f;
                hipsFwd = 0.09f;
                torsoYaw = -28f;
                torsoPitch = 14f;
                headYaw = 22f;
                targetRHand = rShoulder + fwd * (0.76f * rArmLen) + up * (0.22f * rArmLen) + right * (0.34f * rArmLen);
                desiredBladeDir = (up * 0.58f + fwd * 0.62f + right * 0.52f).normalized;
                rPole = right * 0.92f - up * 0.32f - fwd * 0.18f;
            }

            // Apply Hips crouch & pelvis shift
            if (_hipsBone != null)
            {
                _hipsBone.position += (up * hipsDrop + fwd * hipsFwd) * weight;
            }

            // Distribute torso twist across Spine + Chest + Head for natural athletic full-body movement
            if (_spineBone != null)
            {
                Quaternion spineTwist = Quaternion.AngleAxis(torsoYaw * 0.45f * weight, up) *
                                        Quaternion.AngleAxis(torsoPitch * 0.45f * weight, right);
                _spineBone.rotation = spineTwist * _spineBone.rotation;
            }
            if (_chestBone != null)
            {
                Quaternion chestTwist = Quaternion.AngleAxis(torsoYaw * 0.55f * weight, up) *
                                        Quaternion.AngleAxis(torsoPitch * 0.55f * weight, right);
                _chestBone.rotation = chestTwist * _chestBone.rotation;
            }
            if (_headBone != null)
            {
                // Counter-rotate head slightly so Hero's eyes stay locked fiercely onto the Boss!
                Quaternion headCounter = Quaternion.AngleAxis(headYaw * weight, up);
                _headBone.rotation = headCounter * _headBone.rotation;
            }

            // Re-read shoulder position after spine/chest/hips movement!
            rShoulder = _rightUpperArm.position;
            SolveTwoBoneArmIK(_rightUpperArm, _rightLowerArm, _rightHand, targetRHand, rPole, weight);
            AimWeaponBlade(_rightHand, desiredBladeDir, weight);

            // 3. Left Arm (Dynamic two-hand hilt brace during Guard/Parry, counter-whip on ParryFlash, forward thrust on Ganpa!)
            if (_leftUpperArm != null && _leftLowerArm != null && _leftHand != null)
            {
                Vector3 lShoulder = _leftUpperArm.position;
                float lArmLen = Vector3.Distance(lShoulder, _leftLowerArm.position) +
                                Vector3.Distance(_leftLowerArm.position, _leftHand.position);
                if (lArmLen < 0.05f) lArmLen = 0.55f;

                Vector3 targetLHand;
                Vector3 lPole = -right * 0.72f - up * 0.72f - fwd * 0.25f;

                if (_pose == WeaponPose.GanpaDeflect)
                {
                    // Thrust the golden Mapae medallion forward during Ganpa / 삼마패 일격!
                    targetLHand = lShoulder + fwd * (0.88f * lArmLen) + up * (0.05f * lArmLen) + right * (0.10f * lArmLen);
                }
                else if (_pose == WeaponPose.ParryFlash)
                {
                    // Dynamic counter-whip on Variant 0, or two-handed pommel drive on Variant 1!
                    if (_parryVariant == 0)
                        targetLHand = lShoulder - fwd * (0.16f * lArmLen) - up * (0.42f * lArmLen) - right * (0.22f * lArmLen);
                    else
                        targetLHand = targetRHand - desiredBladeDir * 0.14f - right * 0.06f;
                }
                else
                {
                    // Guard & Parry Press: Left hand braces dynamically right below the sword hilt/wrist (true two-handed Joseon 환도 guard!)
                    targetLHand = targetRHand - desiredBladeDir * 0.15f - fwd * 0.04f - right * 0.06f;
                }

                SolveTwoBoneArmIK(_leftUpperArm, _leftLowerArm, _leftHand, targetLHand, lPole, weight);
            }
        }

        /// <summary>
        /// Drives full-body kinetic sword windups, deep blade-biting slashes/thrusts, and visceral hit-recoil flinches
        /// so the 3D sword blade physically buries into the enemy torso on impact and crosses blades on parry!
        /// </summary>
        private void ApplySwordAttackPose(float weight)
        {
            if (modelRoot == null || _rightUpperArm == null || _rightLowerArm == null || _rightHand == null) return;

            Vector3 fwd = modelRoot.forward;
            Vector3 up = modelRoot.up;
            Vector3 right = modelRoot.right;

            Vector3 rShoulder = _rightUpperArm.position;
            float rArmLen = Vector3.Distance(rShoulder, _rightLowerArm.position) +
                            Vector3.Distance(_rightLowerArm.position, _rightHand.position);
            if (rArmLen < 0.05f) rArmLen = 0.55f;

            float hipsDrop = -0.07f;
            float hipsFwd = 0.08f;
            float torsoYaw = 0f;
            float torsoPitch = 14f;
            float headYaw = 0f;

            Vector3 targetRHand = rShoulder + fwd * (0.90f * rArmLen) - up * (0.04f * rArmLen);
            Vector3 rPole = right * 0.78f - up * 0.55f - fwd * 0.20f;
            Vector3 desiredBladeDir = (fwd * 0.92f + up * 0.20f).normalized;
            bool twoHandedGrip = true;

            // Progress t (0 -> 1 over 0.30s): fast snap into blade-bite impact in first 36% (0.11s), holds bite through hitstop, then follows through!
            float p = Mathf.Clamp01(_attackAnimElapsed / AttackAnimDuration);
            float snapT = 1f - Mathf.Pow(1f - Mathf.Clamp01(p / 0.38f), 2f);
            float followT = p > 0.55f ? (p - 0.55f) / 0.45f : 0f;

            switch (_pose)
            {
                case WeaponPose.WindupForward:
                    // Coiled windup before strike
                    hipsDrop = -0.08f;
                    hipsFwd = -0.05f;
                    if (_attackVariant == 2)
                    {
                        torsoYaw = 28f;
                        torsoPitch = 8f;
                        headYaw = -22f;
                        targetRHand = rShoulder + fwd * (0.38f * rArmLen) - up * (0.24f * rArmLen) - right * (0.48f * rArmLen);
                        desiredBladeDir = (-right * 0.75f - up * 0.38f + fwd * 0.52f).normalized;
                    }
                    else
                    {
                        torsoYaw = -30f;
                        torsoPitch = -6f;
                        headYaw = 24f;
                        targetRHand = rShoulder + fwd * (0.32f * rArmLen) + up * (0.32f * rArmLen) + right * (0.48f * rArmLen);
                        desiredBladeDir = (up * 0.72f + right * 0.56f - fwd * 0.38f).normalized;
                    }
                    break;

                case WeaponPose.WindupOverhead:
                    // High two-handed overhead raise ("하나, 둘... 쾅!")
                    hipsDrop = -0.05f;
                    hipsFwd = -0.06f;
                    torsoYaw = -12f;
                    torsoPitch = -16f;
                    headYaw = 10f;
                    targetRHand = rShoulder + fwd * (0.35f * rArmLen) + up * (0.64f * rArmLen) + right * (0.14f * rArmLen);
                    desiredBladeDir = (up * 0.76f - fwd * 0.58f + right * 0.28f).normalized;
                    break;

                case WeaponPose.WindupThrustPerilous:
                    // Low coiled spear/sword thrust telegraph (殺)
                    hipsDrop = -0.12f;
                    hipsFwd = -0.08f;
                    torsoYaw = -32f;
                    torsoPitch = 14f;
                    headYaw = 28f;
                    targetRHand = rShoulder - fwd * (0.28f * rArmLen) - up * (0.18f * rArmLen) + right * (0.36f * rArmLen);
                    desiredBladeDir = (fwd * 0.98f + up * 0.08f).normalized;
                    twoHandedGrip = false;
                    break;

                case WeaponPose.SlashSwing:
                    if (_attackVariant == 2)
                    {
                        // Hit 2: Inside-Left to Outside-Right Deep Horizontal/Upward Cleave (칼날이 보스 몸통을 대각선으로 깊숙이 파고듦!)
                        hipsDrop = -0.10f;
                        hipsFwd = Mathf.Lerp(0.02f, 0.14f, snapT);
                        torsoYaw = Mathf.Lerp(28f, -34f, snapT);
                        torsoPitch = Mathf.Lerp(8f, 18f, snapT);
                        headYaw = -torsoYaw * 0.65f;

                        Vector3 startH = rShoulder + fwd * (0.58f * rArmLen) - up * (0.20f * rArmLen) - right * (0.42f * rArmLen);
                        Vector3 biteH = rShoulder + fwd * (0.95f * rArmLen) + up * (0.04f * rArmLen) + right * (0.22f * rArmLen);
                        Vector3 endH = rShoulder + fwd * (0.78f * rArmLen) + up * (0.18f * rArmLen) + right * (0.48f * rArmLen);

                        Vector3 startB = (fwd * 0.76f - right * 0.58f - up * 0.25f).normalized;
                        Vector3 biteB = (fwd * 0.90f + right * 0.34f + up * 0.26f).normalized;
                        Vector3 endB = (fwd * 0.55f + right * 0.72f + up * 0.42f).normalized;

                        targetRHand = Vector3.Lerp(Vector3.Lerp(startH, biteH, snapT), endH, followT);
                        desiredBladeDir = Vector3.Slerp(Vector3.Slerp(startB, biteB, snapT), endB, followT);
                    }
                    else
                    {
                        // Hit 1: High-Right to Low-Left Deep Diagonal Torso Slash (칼날이 적 가슴/허리에 깊숙이 박힘!)
                        hipsDrop = -0.10f;
                        hipsFwd = Mathf.Lerp(0.02f, 0.15f, snapT);
                        torsoYaw = Mathf.Lerp(-28f, 34f, snapT);
                        torsoPitch = Mathf.Lerp(4f, 20f, snapT);
                        headYaw = -torsoYaw * 0.65f;

                        Vector3 startH = rShoulder + fwd * (0.55f * rArmLen) + up * (0.34f * rArmLen) + right * (0.38f * rArmLen);
                        Vector3 biteH = rShoulder + fwd * (0.96f * rArmLen) - up * (0.04f * rArmLen) - right * (0.16f * rArmLen);
                        Vector3 endH = rShoulder + fwd * (0.76f * rArmLen) - up * (0.22f * rArmLen) - right * (0.44f * rArmLen);

                        Vector3 startB = (fwd * 0.72f + up * 0.56f + right * 0.40f).normalized;
                        Vector3 biteB = (fwd * 0.92f - right * 0.32f - up * 0.22f).normalized;
                        Vector3 endB = (fwd * 0.58f - right * 0.72f - up * 0.38f).normalized;

                        targetRHand = Vector3.Lerp(Vector3.Lerp(startH, biteH, snapT), endH, followT);
                        desiredBladeDir = Vector3.Slerp(Vector3.Slerp(startB, biteB, snapT), endB, followT);
                    }
                    break;

                case WeaponPose.SpinSlash:
                    // Hit 2 / Phase 2 Spin Slash: Wide Low-to-High Sweeping Cleave that cuts deep across the chest!
                    hipsDrop = -0.11f;
                    hipsFwd = Mathf.Lerp(0.04f, 0.16f, snapT);
                    torsoYaw = Mathf.Lerp(32f, -36f, snapT);
                    torsoPitch = Mathf.Lerp(10f, 18f, snapT);
                    headYaw = -torsoYaw * 0.60f;

                    Vector3 spStartH = rShoulder + fwd * (0.62f * rArmLen) - up * (0.24f * rArmLen) - right * (0.44f * rArmLen);
                    Vector3 spBiteH = rShoulder + fwd * (0.96f * rArmLen) + up * (0.06f * rArmLen) + right * (0.24f * rArmLen);
                    Vector3 spEndH = rShoulder + fwd * (0.76f * rArmLen) + up * (0.24f * rArmLen) + right * (0.50f * rArmLen);

                    Vector3 spStartB = (fwd * 0.78f - right * 0.56f - up * 0.26f).normalized;
                    Vector3 spBiteB = (fwd * 0.90f + right * 0.34f + up * 0.26f).normalized;
                    Vector3 spEndB = (fwd * 0.52f + right * 0.72f + up * 0.45f).normalized;

                    targetRHand = Vector3.Lerp(Vector3.Lerp(spStartH, spBiteH, snapT), spEndH, followT);
                    desiredBladeDir = Vector3.Slerp(Vector3.Slerp(spStartB, spBiteB, snapT), spEndB, followT);
                    break;

                case WeaponPose.ExecutionFinish:
                    // Hit 3 / Delayed Heavy Slam / Execution: Fierce Two-Handed Overhead Cleave burying the blade deep downward-forward!
                    hipsDrop = Mathf.Lerp(-0.04f, -0.14f, snapT);
                    hipsFwd = Mathf.Lerp(0.04f, 0.18f, snapT);
                    torsoYaw = Mathf.Lerp(-18f, 24f, snapT);
                    torsoPitch = Mathf.Lerp(-12f, 26f, snapT);
                    headYaw = -torsoYaw * 0.60f;

                    Vector3 exStartH = rShoulder + fwd * (0.52f * rArmLen) + up * (0.56f * rArmLen) + right * (0.18f * rArmLen);
                    Vector3 exBiteH = rShoulder + fwd * (0.96f * rArmLen) - up * (0.12f * rArmLen) - right * (0.08f * rArmLen);
                    Vector3 exEndH = rShoulder + fwd * (0.84f * rArmLen) - up * (0.28f * rArmLen) - right * (0.20f * rArmLen);

                    Vector3 exStartB = (up * 0.78f + fwd * 0.58f + right * 0.22f).normalized;
                    Vector3 exBiteB = (fwd * 0.92f - up * 0.36f - right * 0.14f).normalized;
                    Vector3 exEndB = (fwd * 0.78f - up * 0.60f - right * 0.18f).normalized;

                    targetRHand = Vector3.Lerp(Vector3.Lerp(exStartH, exBiteH, snapT), exEndH, followT);
                    desiredBladeDir = Vector3.Slerp(Vector3.Slerp(exStartB, exBiteB, snapT), exEndB, followT);
                    break;

                case WeaponPose.ThrustExtend:
                    // Deep Piercing Lunge (강격 / 殺 찌르기): Full arm extension burying the blade straight through the opponent!
                    hipsDrop = -0.13f;
                    hipsFwd = Mathf.Lerp(0.05f, 0.20f, snapT);
                    torsoYaw = Mathf.Lerp(-20f, 28f, snapT);
                    torsoPitch = Mathf.Lerp(12f, 24f, snapT);
                    headYaw = -torsoYaw * 0.75f;

                    Vector3 thStartH = rShoulder + fwd * (0.45f * rArmLen) - up * (0.08f * rArmLen) + right * (0.20f * rArmLen);
                    Vector3 thBiteH = rShoulder + fwd * (0.98f * rArmLen) + up * (0.02f * rArmLen);
                    targetRHand = Vector3.Lerp(thStartH, thBiteH, snapT);
                    desiredBladeDir = (fwd * 0.99f + up * 0.04f).normalized;
                    twoHandedGrip = false;
                    break;

                case WeaponPose.Staggered:
                    // Visceral Hit / Parry-Stagger Recoil: Whole torso wrenches backward from the blade bite!
                    float st = Mathf.Clamp01(_staggerAnimElapsed / 0.28f);
                    float jolt = Mathf.Sin(st * Mathf.PI);
                    hipsDrop = -0.09f * jolt;
                    hipsFwd = -0.10f * jolt;
                    torsoYaw = -22f * jolt;
                    torsoPitch = -18f * jolt;
                    headYaw = 16f * jolt;
                    targetRHand = rShoulder + fwd * (0.35f * rArmLen) - up * (0.18f * rArmLen) + right * (0.42f * rArmLen);
                    desiredBladeDir = (fwd * 0.45f + up * 0.55f + right * 0.70f).normalized;
                    twoHandedGrip = false;
                    break;
            }

            if (_hipsBone != null)
            {
                _hipsBone.position += (up * hipsDrop + fwd * hipsFwd) * weight;
            }
            if (_spineBone != null)
            {
                Quaternion spineTwist = Quaternion.AngleAxis(torsoYaw * 0.45f * weight, up) *
                                        Quaternion.AngleAxis(torsoPitch * 0.45f * weight, right);
                _spineBone.rotation = spineTwist * _spineBone.rotation;
            }
            if (_chestBone != null)
            {
                Quaternion chestTwist = Quaternion.AngleAxis(torsoYaw * 0.55f * weight, up) *
                                        Quaternion.AngleAxis(torsoPitch * 0.55f * weight, right);
                _chestBone.rotation = chestTwist * _chestBone.rotation;
            }
            if (_headBone != null)
            {
                Quaternion headCounter = Quaternion.AngleAxis(headYaw * weight, up);
                _headBone.rotation = headCounter * _headBone.rotation;
            }

            rShoulder = _rightUpperArm.position;
            SolveTwoBoneArmIK(_rightUpperArm, _rightLowerArm, _rightHand, targetRHand, rPole, weight);
            AimWeaponBlade(_rightHand, desiredBladeDir, weight);

            if (_leftUpperArm != null && _leftLowerArm != null && _leftHand != null)
            {
                Vector3 lShoulder = _leftUpperArm.position;
                float lArmLen = Vector3.Distance(lShoulder, _leftLowerArm.position) +
                                Vector3.Distance(_leftLowerArm.position, _leftHand.position);
                if (lArmLen < 0.05f) lArmLen = 0.55f;

                Vector3 targetLHand = twoHandedGrip
                    ? targetRHand - desiredBladeDir * 0.14f - right * 0.06f
                    : lShoulder - fwd * (0.18f * lArmLen) - up * (0.36f * lArmLen) - right * (0.24f * lArmLen);
                Vector3 lPole = -right * 0.72f - up * 0.72f - fwd * 0.25f;
                SolveTwoBoneArmIK(_leftUpperArm, _leftLowerArm, _leftHand, targetLHand, lPole, weight);
            }
        }

        private static void SolveTwoBoneArmIK(
            Transform upper,
            Transform lower,
            Transform hand,
            Vector3 targetPos,
            Vector3 poleWorldDir,
            float weight)
        {
            Vector3 upperPos = upper.position;
            float a = Vector3.Distance(upperPos, lower.position);
            float b = Vector3.Distance(lower.position, hand.position);
            if (a < 0.005f || b < 0.005f) return;

            Vector3 toTarget = targetPos - upperPos;
            float rawDist = toTarget.magnitude;
            if (rawDist < 0.001f) return;

            float minDist = Mathf.Abs(a - b) + 0.01f;
            float maxDist = (a + b) * 0.985f;
            float dist = Mathf.Clamp(rawDist, minDist, maxDist);
            Vector3 dir = toTarget / rawDist;

            float cosAlpha = Mathf.Clamp((a * a + dist * dist - b * b) / (2f * a * dist), -1f, 1f);
            float sinAlpha = Mathf.Sqrt(Mathf.Max(0f, 1f - cosAlpha * cosAlpha));

            Vector3 bendDir = poleWorldDir - Vector3.Dot(poleWorldDir, dir) * dir;
            if (bendDir.sqrMagnitude < 1e-4f)
                bendDir = Vector3.Cross(dir, Vector3.up);
            bendDir.Normalize();

            Vector3 desiredElbow = upperPos + dir * (a * cosAlpha) + bendDir * (a * sinAlpha);

            Vector3 curUpperDir = lower.position - upperPos;
            if (curUpperDir.sqrMagnitude > 1e-6f)
            {
                Quaternion upperDelta = Quaternion.FromToRotation(curUpperDir.normalized, (desiredElbow - upperPos).normalized);
                upper.rotation = Quaternion.Slerp(upper.rotation, upperDelta * upper.rotation, weight);
            }

            Vector3 curLowerDir = hand.position - lower.position;
            Vector3 desiredLowerDir = targetPos - lower.position;
            if (curLowerDir.sqrMagnitude > 1e-6f && desiredLowerDir.sqrMagnitude > 1e-6f)
            {
                Quaternion lowerDelta = Quaternion.FromToRotation(curLowerDir.normalized, desiredLowerDir.normalized);
                lower.rotation = Quaternion.Slerp(lower.rotation, lowerDelta * lower.rotation, weight);
            }
        }

        private void AimWeaponBlade(Transform hand, Vector3 desiredBladeWorldDir, float weight)
        {
            if (hand == null || _weaponRoot == null || clashAnchor == null) return;
            Vector3 curBladeDir = clashAnchor.position - _weaponRoot.position;
            if (curBladeDir.sqrMagnitude < 1e-6f || desiredBladeWorldDir.sqrMagnitude < 1e-6f) return;

            Quaternion delta = Quaternion.FromToRotation(curBladeDir.normalized, desiredBladeWorldDir.normalized);
            hand.rotation = Quaternion.Slerp(hand.rotation, delta * hand.rotation, weight);
        }

        private void UpdateHumanoidLocomotion(float dt)
        {
            if (modelRoot != null)
            {
                Vector3 targetPos = _baseModelLocalPos;
                targetPos.y += _verticalHop;
                modelRoot.localPosition = Vector3.Lerp(modelRoot.localPosition, targetPos, 1f - Mathf.Exp(-22f * dt));
            }

            if (_isDead) return;

            if (_pose == WeaponPose.Idle || _pose == WeaponPose.Guard)
            {
                int desired = _moveSpeed > 0.1f ? HashRun : (_pose == WeaponPose.Guard ? HashBlockIdle : HashIdle);
                humanoidAnimator.speed = _moveSpeed > 0.1f ? (_pose == WeaponPose.Guard ? 0.85f : 1.15f) : 1f;
                PlayAnim(desired, 0.08f, forceRestart: false);
            }
        }

        private void PlayAnim(int stateHash, float fadeDuration, bool forceRestart, float normalizedTimeOffset = 0f)
        {
            if (humanoidAnimator == null) return;
            if (!forceRestart && _currentAnimHash == stateHash) return;
            _currentAnimHash = stateHash;
            humanoidAnimator.CrossFadeInFixedTime(stateHash, fadeDuration, 0, normalizedTimeOffset);
        }

        private void SetTrail(bool active, Color color)
        {
            if (swordTrail == null) return;
            if (active && !swordTrail.emitting)
            {
                // Clear stale vertices so TrailRenderer NEVER draws an ugly jump line from the previous idle spot
                swordTrail.Clear();
            }
            swordTrail.emitting = active;
            if (active)
            {
                swordTrail.startColor = color;
                swordTrail.endColor = new Color(color.r, color.g, color.b, 0f);
            }
        }

        private void UpdateLegacyGreyboxPose(float dt)
        {
            if (modelRoot == null || legacyWeaponPivot == null) return;

            float targetBodyTilt = 0f;
            Vector3 targetWeaponRot = new Vector3(0f, 0f, -28f);
            Vector3 targetWeaponPos = new Vector3(0.45f, 0.12f, -0.18f);

            switch (_pose)
            {
                case WeaponPose.Guard:
                    targetBodyTilt = 6f;
                    targetWeaponRot = new Vector3(0f, 0f, 14f);
                    targetWeaponPos = new Vector3(0.56f, 0.24f, -0.22f);
                    break;
                case WeaponPose.ParryFlash:
                    targetBodyTilt = -8f;
                    targetWeaponRot = new Vector3(0f, 0f, -62f);
                    targetWeaponPos = new Vector3(0.70f, 0.34f, -0.24f);
                    break;
                case WeaponPose.WindupForward:
                    targetBodyTilt = 14f;
                    targetWeaponRot = new Vector3(0f, 0f, 68f);
                    targetWeaponPos = new Vector3(0.18f, 0.38f, -0.22f);
                    break;
                case WeaponPose.WindupOverhead:
                    targetBodyTilt = 18f;
                    targetWeaponRot = new Vector3(0f, 0f, 125f);
                    targetWeaponPos = new Vector3(-0.05f, 0.78f, -0.20f);
                    break;
                case WeaponPose.WindupThrustPerilous:
                    targetBodyTilt = 12f;
                    targetWeaponRot = new Vector3(0f, 0f, -90f);
                    targetWeaponPos = new Vector3(-0.32f, 0.05f, -0.20f);
                    break;
                case WeaponPose.SlashSwing:
                    targetBodyTilt = -16f;
                    targetWeaponRot = new Vector3(0f, 0f, -122f);
                    targetWeaponPos = new Vector3(0.78f, -0.05f, -0.24f);
                    break;
                case WeaponPose.ThrustExtend:
                    targetBodyTilt = -20f;
                    targetWeaponRot = new Vector3(0f, 0f, -90f);
                    targetWeaponPos = new Vector3(1.12f, 0.06f, -0.20f);
                    break;
                case WeaponPose.GanpaDeflect:
                    targetBodyTilt = -18f;
                    targetWeaponRot = new Vector3(0f, 0f, -45f);
                    targetWeaponPos = new Vector3(0.86f, 0.28f, -0.22f);
                    break;
                case WeaponPose.Staggered:
                    targetBodyTilt = 26f;
                    targetWeaponRot = new Vector3(0f, 0f, 45f);
                    targetWeaponPos = new Vector3(0.25f, -0.22f, -0.18f);
                    break;
                case WeaponPose.ExecutionFinish:
                    targetBodyTilt = -26f;
                    targetWeaponRot = new Vector3(0f, 0f, -108f);
                    targetWeaponPos = new Vector3(1.05f, 0.14f, -0.24f);
                    break;
            }

            float lerp = 1f - Mathf.Exp(-18f * dt);
            modelRoot.localRotation = Quaternion.Slerp(modelRoot.localRotation, Quaternion.Euler(0f, 0f, targetBodyTilt * _facingSign), lerp);
            Vector3 bp = _baseModelLocalPos;
            bp.y += _verticalHop;
            modelRoot.localPosition = Vector3.Lerp(modelRoot.localPosition, bp, lerp);
            legacyWeaponPivot.localPosition = Vector3.Lerp(legacyWeaponPivot.localPosition, targetWeaponPos, lerp);
            legacyWeaponPivot.localRotation = Quaternion.Slerp(legacyWeaponPivot.localRotation, Quaternion.Euler(targetWeaponRot), lerp);
        }

        private void UpdateColorFlash(float dt)
        {
            Color bodyCol = baseBodyColor;
            Color bodyEmit = Color.black;
            Color weaponCol = baseWeaponColor;
            Color weaponEmit = Color.black;

            if (_perilousGlow)
            {
                float pulse = 0.55f + 0.45f * Mathf.Sin(Time.time * 24f);
                weaponEmit = new Color(1.6f, 0.14f, 0.14f, 1f) * pulse;
            }
            else if (_staggerPulse)
            {
                float pulse = 0.35f + 0.65f * Mathf.Sin(Time.time * 10f);
                bodyEmit = new Color(0.55f, 0.08f, 0.06f, 1f) * pulse;
            }

            if (_flashTimer > 0f)
            {
                _flashTimer -= dt;
                float t = Mathf.Clamp01(_flashTimer * 6f);
                if (!isTexturedModel)
                    bodyCol = Color.Lerp(bodyCol, _flashColor, t * 0.65f);
                bodyEmit += _flashColor * (t * 0.65f);
                weaponEmit += _flashColor * (t * 1.15f);
            }

            ApplyColors(bodyCol, bodyEmit, weaponCol, weaponEmit);
        }

        private void ApplyColors(Color bodyCol, Color bodyEmit, Color weaponCol, Color weaponEmit)
        {
            if (bodyRenderer != null)
            {
                bodyRenderer.GetPropertyBlock(_bodyMpb);
                if (!isTexturedModel)
                {
                    _bodyMpb.SetColor(BaseColorId, bodyCol);
                    _bodyMpb.SetColor(ColorId, bodyCol);
                }
                _bodyMpb.SetColor(EmissionColorId, bodyEmit);
                bodyRenderer.SetPropertyBlock(_bodyMpb);
            }

            if (weaponRenderer != null)
            {
                weaponRenderer.GetPropertyBlock(_weaponMpb);
                if (!isTexturedModel)
                {
                    _weaponMpb.SetColor(BaseColorId, weaponCol);
                    _weaponMpb.SetColor(ColorId, weaponCol);
                }
                _weaponMpb.SetColor(EmissionColorId, weaponEmit);
                weaponRenderer.SetPropertyBlock(_weaponMpb);
            }
        }
    }
}
