using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EosaBlade
{
    public enum PlayerState
    {
        Idle,
        AttackWindup,
        AttackRecovery,
        SpecialMapae,
        Dodging,
        GuardBroken,
        HitFlinch,
        Executing,
        Dead
    }

    /// <summary>
    /// 2.5D X-axis Player Controller implementing Sekiro-style Parry (0.18s / 0.08s spam penalty),
    /// Guard (never breaks on Perfect Parry), 3-hit Attack / Hold Heavy / Execution,
    /// Special/Ganpa counter, and Dodge/Leap with i-frames.
    /// </summary>
    public class PlayerController : MonoBehaviour
    {
        [Header("Movement (X-Axis Only)")]
        [SerializeField] private float moveSpeed = 5.8f;
        [SerializeField] private float arenaHalfWidth = 6.2f;
        [SerializeField] private float minBossSeparation = 1.55f;

        [Header("Parry & Guard Tuning (60fps Mobile)")]
        [SerializeField] private float parryWindow = 0.20f;
        [SerializeField] private float spamParryWindow = 0.09f;
        [SerializeField] private float spamPenaltyThreshold = 0.45f;
        [SerializeField] private float guardBreakDuration = 0.75f;

        [Header("Attack & Special Tuning")]
        [SerializeField] private float attackRange = 2.65f;
        [SerializeField] private float executionRange = 3.80f;
        [SerializeField] private float ganpaRange = 4.00f;
        [SerializeField] private float heavyHoldThreshold = 0.28f;
        [SerializeField] private float specialCooldown = 1.15f;

        [Header("Dodge / Leap Tuning")]
        [SerializeField] private float dodgeDuration = 0.32f;
        [SerializeField] private float dodgeIFrameDuration = 0.30f;
        [SerializeField] private float dodgeSpeed = 9.2f;
        [SerializeField] private float dodgeCooldown = 0.58f;
        [SerializeField] private float dodgeHopHeight = 0.95f;

        [Header("References")]
        [SerializeField] private CombatantVisual visual;

        public CombatStats Stats { get; } = new CombatStats
        {
            MaxHp = 100f,
            MaxPosture = 100f,
            MaxLives = 1,
            PostureRecoveryDelay = 1.5f,
            BasePostureRecoveryPerSec = 16f
        };

        public PlayerState State { get; private set; } = PlayerState.Idle;
        public int ConsecutiveParries { get; private set; }
        public bool IsMapaeEmpowered { get; private set; }
        public bool IsGuarding => _parryHeld && (State == PlayerState.Idle || State == PlayerState.AttackRecovery);
        public bool IsParryWindowActive => Time.time <= _parryWindowEndTime;
        public bool IsDodging => State == PlayerState.Dodging && _dodgeElapsed <= dodgeIFrameDuration;
        public float FacingSign { get; private set; } = 1f;
        public float SpecialCooldownNormalized => Mathf.Clamp01((_nextSpecialTime - Time.time) / specialCooldown);
        public float DodgeCooldownNormalized => Mathf.Clamp01((_nextDodgeTime - Time.time) / dodgeCooldown);

        public event Action<string, Color> OnCombatFeedback;

        private CombatDuelManager _duel;
        private GeneralBossController _boss;
        private CombatJuiceController _juice;

        // Touch + Keyboard unified input state
        private bool _touchLeft;
        private bool _touchRight;
        private bool _parryHeld;
        private bool _attackHeld;
        private float _attackPressTime;
        private bool _heavyTriggered;

        // Parry spam penalty tracking
        private float _lastParryPressTime = -10f;
        private float _parryWindowEndTime = -10f;
        private bool _lastParryDidNotConnect;

        // Action timers
        private float _stateTimer;
        private float _attackHitDelay;
        private bool _attackHitFired;
        private int _comboStep;
        private float _comboResetTime;
        private AttackKind _currentAttackKind;

        private float _dodgeElapsed;
        private float _dodgeDir;
        private float _nextDodgeTime = -10f;
        private float _nextSpecialTime = -10f;
        private float _ganpaActiveUntil = -10f;
        private float _parryFlashTimer;
        private float _counterSlashReadyUntil = -10f;
        private float _attackFollowThroughTimer;
        private float _attackHopDuration;
        private float _attackHopElapsed;
        private int _multiHitRemaining;
        private float _nextMultiHitDelay;

        // Smooth physical lunge/step velocity (eliminates 1-frame teleportation!)
        private float _lungeVelocity;
        private float _lungeRemaining;

        public void Bind(CombatDuelManager duel, GeneralBossController boss, CombatJuiceController juice, CombatantVisual vis)
        {
            _duel = duel;
            _boss = boss;
            _juice = juice;
            if (vis != null) visual = vis;
        }

        public void ResetForDuel(Vector3 spawnPos)
        {
            transform.position = spawnPos;
            Stats.ResetAll();
            State = PlayerState.Idle;
            ConsecutiveParries = 0;
            IsMapaeEmpowered = false;
            _touchLeft = false;
            _touchRight = false;
            _parryHeld = false;
            _attackHeld = false;
            _heavyTriggered = false;
            _lastParryPressTime = -10f;
            _parryWindowEndTime = -10f;
            _lastParryDidNotConnect = false;
            _stateTimer = 0f;
            _attackHitFired = false;
            _comboStep = 0;
            _nextDodgeTime = -10f;
            _nextSpecialTime = -10f;
            _ganpaActiveUntil = -10f;
            _parryFlashTimer = 0f;
            _counterSlashReadyUntil = -10f;
            _attackFollowThroughTimer = 0f;
            _attackHopDuration = 0f;
            _attackHopElapsed = 0f;
            _multiHitRemaining = 0;
            _nextMultiHitDelay = 0f;
            _lungeRemaining = 0f;
            _lungeVelocity = 0f;

            if (visual != null)
            {
                visual.SetDead(false);
                visual.SetMoveSpeed(0f);
                visual.SetVerticalHop(0f);
                visual.SetFacing(1f);
                visual.SetPose(WeaponPose.Idle);
            }
        }

        // --- Touch UI Input Hooks ---

        public void SetMoveLeft(bool held) => _touchLeft = held;
        public void SetMoveRight(bool held) => _touchRight = held;

        public void OnParryDown()
        {
            if (!_duel || !_duel.IsDuelActive) return;
            if (State == PlayerState.Dead || State == PlayerState.GuardBroken || State == PlayerState.Executing) return;

            _parryHeld = true;

            // Sekiro responsiveness: Parry cancels attack recovery / early windup cleanly
            if (State == PlayerState.AttackRecovery || (State == PlayerState.AttackWindup && !_attackHitFired))
            {
                State = PlayerState.Idle;
                _attackHitFired = true;
            }

            float now = Time.time;
            bool isSpam = _lastParryDidNotConnect && (now - _lastParryPressTime < spamPenaltyThreshold);
            float window = isSpam ? spamParryWindow : parryWindow;

            _lastParryPressTime = now;
            _parryWindowEndTime = now + window;
            _lastParryDidNotConnect = true;
            _attackFollowThroughTimer = 0f;
            // Guarantee even a quick 40ms tap on the Parry button plays the full 0.24s dynamic sword-parry whip (never cancelled by OnParryUp!)
            _parryFlashTimer = Mathf.Max(_parryFlashTimer, 0.24f);

            // Every Parry press triggers a dynamic full-body sword-whip parry motion into guard + subtle step!
            float room = Mathf.Max(0f, DistanceToBoss() - minBossSeparation);
            if (room > 0.05f) StartSmoothLunge(FacingSign * Mathf.Min(0.14f, room), 0.09f);

            if (visual != null)
            {
                visual.TriggerParryAttempt();
            }
        }

        public void OnParryUp()
        {
            _parryHeld = false;
            if (State == PlayerState.Idle && _parryFlashTimer <= 0f && visual != null)
            {
                visual.SetPose(WeaponPose.Idle);
            }
        }

        public void OnAttackDown()
        {
            if (!_duel || !_duel.IsDuelActive) return;
            if (State == PlayerState.Dead || State == PlayerState.GuardBroken || State == PlayerState.HitFlinch || State == PlayerState.Executing) return;

            // Priority 1: Execution (인살) when Boss is staggered & within range
            if (_boss != null && _boss.IsExecutable && DistanceToBoss() <= executionRange)
            {
                StartExecution();
                return;
            }

            _attackHeld = true;
            _attackPressTime = Time.time;
            _heavyTriggered = false;
        }

        public void OnAttackUp()
        {
            if (!_attackHeld) return;
            _attackHeld = false;

            if (!_duel || !_duel.IsDuelActive) return;
            if (_heavyTriggered) return;

            if (State == PlayerState.Idle || State == PlayerState.AttackRecovery)
            {
                StartLightAttack();
            }
        }

        public void OnSpecialDown()
        {
            if (!_duel || !_duel.IsDuelActive) return;
            if (State == PlayerState.Dead || State == PlayerState.GuardBroken || State == PlayerState.Executing) return;

            // Priority 1: Ganpa Counter (간파) against Boss's Perilous Thrust (殺)
            if (_boss != null && _boss.IsPerilousCounterable && DistanceToBoss() <= ganpaRange)
            {
                PerformGanpaCounter();
                return;
            }

            // Priority 2: Mapae Strike (삼마패 일격 — posture-focused strike)
            if (Time.time < _nextSpecialTime) return;
            if (State == PlayerState.Idle || State == PlayerState.AttackRecovery)
            {
                StartMapaeStrike();
            }
        }

        public void OnDodgeDown()
        {
            if (!_duel || !_duel.IsDuelActive) return;
            if (State == PlayerState.Dead || State == PlayerState.GuardBroken || State == PlayerState.Executing) return;
            if (Time.time < _nextDodgeTime) return;

            StartDodge();
        }

        // --- Update Loop ---

        private void Update()
        {
            if (_duel == null) return;

            PollEditorKeyboard();

            if (!_duel.IsDuelActive || State == PlayerState.Dead)
            {
                return;
            }

            float dt = Time.deltaTime;
            if (_parryFlashTimer > 0f) _parryFlashTimer = Mathf.Max(0f, _parryFlashTimer - dt);
            if (_attackFollowThroughTimer > 0f) _attackFollowThroughTimer = Mathf.Max(0f, _attackFollowThroughTimer - dt);
            UpdateFacing();
            UpdateSmoothLunge(dt);
            UpdateAttackHop(dt);

            // Check hold-for-heavy thrust while Attack button is held
            if (_attackHeld && !_heavyTriggered && (State == PlayerState.Idle || State == PlayerState.AttackRecovery))
            {
                if (Time.time - _attackPressTime >= heavyHoldThreshold)
                {
                    _heavyTriggered = true;
                    StartHeavyThrust();
                }
            }

            switch (State)
            {
                case PlayerState.Idle:
                    HandleMovement(dt);
                    Stats.TickPostureRecovery(dt, isGuardingBonus: _parryHeld);
                    if (visual != null)
                    {
                        if (_attackHopDuration <= 0f) visual.SetVerticalHop(0f);
                        bool isMoving = Mathf.Abs(GetMoveAxis()) > 0.05f;
                        if (_parryFlashTimer <= 0f && (_parryHeld || isMoving || _attackFollowThroughTimer <= 0f))
                        {
                            visual.SetPose(_parryHeld ? WeaponPose.Guard : WeaponPose.Idle);
                        }
                    }
                    break;

                case PlayerState.AttackWindup:
                    _stateTimer -= dt;
                    _attackHitDelay -= dt;
                    if (!_attackHitFired && _attackHitDelay <= 0f)
                    {
                        _attackHitFired = true;
                        ResolvePlayerStrike();
                    }
                    if (_multiHitRemaining > 0)
                    {
                        _nextMultiHitDelay -= dt;
                        if (_nextMultiHitDelay <= 0f)
                        {
                            _multiHitRemaining--;
                            _nextMultiHitDelay = 0.08f;
                            ResolvePlayerStrike();
                        }
                    }
                    if (_stateTimer <= 0f)
                    {
                        State = PlayerState.AttackRecovery;
                        _stateTimer = _currentAttackKind == AttackKind.PlayerHeavy ? 0.18f : 0.11f;
                        // Allow the full dynamic sword follow-through animation to finish smoothly after recovery!
                        _attackFollowThroughTimer = 0.20f;
                    }
                    break;

                case PlayerState.AttackRecovery:
                    _stateTimer -= dt;
                    if (_stateTimer <= 0f)
                    {
                        State = PlayerState.Idle;
                    }
                    break;

                case PlayerState.SpecialMapae:
                    _stateTimer -= dt;
                    _attackHitDelay -= dt;
                    if (!_attackHitFired && _attackHitDelay <= 0f)
                    {
                        _attackHitFired = true;
                        ResolvePlayerStrike();
                    }
                    if (_multiHitRemaining > 0)
                    {
                        _nextMultiHitDelay -= dt;
                        if (_nextMultiHitDelay <= 0f)
                        {
                            _multiHitRemaining--;
                            _nextMultiHitDelay = 0.075f;
                            if (visual != null)
                            {
                                visual.SetAttackComboVariant(4 - _multiHitRemaining);
                                visual.SetPose(_multiHitRemaining == 0 ? WeaponPose.ExecutionFinish : WeaponPose.SpinSlash);
                            }
                            ResolvePlayerStrike();
                        }
                    }
                    if (_stateTimer <= 0f)
                    {
                        State = PlayerState.Idle;
                    }
                    break;

                case PlayerState.Dodging:
                    _dodgeElapsed += dt;
                    MoveX(_dodgeDir * dodgeSpeed * dt, allowPassThrough: false);
                    float hopT = Mathf.Clamp01(_dodgeElapsed / dodgeDuration);
                    float hopY = Mathf.Sin(hopT * Mathf.PI) * dodgeHopHeight;
                    if (visual != null) visual.SetVerticalHop(hopY);
                    if (_dodgeElapsed >= dodgeDuration)
                    {
                        if (visual != null) visual.SetVerticalHop(0f);
                        State = PlayerState.Idle;
                    }
                    break;

                case PlayerState.GuardBroken:
                    _stateTimer -= dt;
                    if (visual != null) visual.SetPose(WeaponPose.StanceBrokenKneel);
                    if (_stateTimer <= 0f)
                    {
                        Stats.SetPosture(Stats.MaxPosture * 0.42f);
                        State = PlayerState.Idle;
                    }
                    break;

                case PlayerState.HitFlinch:
                    _stateTimer -= dt;
                    if (visual != null) visual.SetPose(WeaponPose.Staggered);
                    if (_stateTimer <= 0f)
                    {
                        State = PlayerState.Idle;
                    }
                    break;

                case PlayerState.Executing:
                    _stateTimer -= dt;
                    if (_stateTimer <= 0f)
                    {
                        State = PlayerState.Idle;
                    }
                    break;
            }
        }

        private void PollEditorKeyboard()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return;

            if (kb.rKey.wasPressedThisFrame)
            {
                _duel.ResetDuel();
                return;
            }

            if (kb.kKey.wasPressedThisFrame || kb.xKey.wasPressedThisFrame) OnParryDown();
            if (kb.kKey.wasReleasedThisFrame || kb.xKey.wasReleasedThisFrame) OnParryUp();

            if (kb.jKey.wasPressedThisFrame || kb.zKey.wasPressedThisFrame) OnAttackDown();
            if (kb.jKey.wasReleasedThisFrame || kb.zKey.wasReleasedThisFrame) OnAttackUp();

            if (kb.lKey.wasPressedThisFrame || kb.cKey.wasPressedThisFrame) OnSpecialDown();
            if (kb.spaceKey.wasPressedThisFrame || kb.leftShiftKey.wasPressedThisFrame) OnDodgeDown();
        }

        private float GetMoveAxis()
        {
            float axis = 0f;
            if (_touchLeft) axis -= 1f;
            if (_touchRight) axis += 1f;

            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) axis -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) axis += 1f;
            }

            return Mathf.Clamp(axis, -1f, 1f);
        }

        private void HandleMovement(float dt)
        {
            float axis = GetMoveAxis();
            // Pass signed movement relative to FacingSign (+1 = advancing forward, -1 = retreating backward!)
            if (visual != null) visual.SetMoveSpeed(axis * FacingSign);
            if (Mathf.Abs(axis) < 0.01f) return;

            float speed = _parryHeld ? moveSpeed * 0.55f : (axis * FacingSign < 0f ? moveSpeed * 0.85f : moveSpeed);
            MoveX(axis * speed * dt, allowPassThrough: false);
        }

        private void MoveX(float deltaX, bool allowPassThrough)
        {
            Vector3 p = transform.position;
            float nextX = Mathf.Clamp(p.x + deltaX, -arenaHalfWidth, arenaHalfWidth);

            if (!allowPassThrough && _boss != null)
            {
                // Strict 1:1 duel spacing: Player stays cleanly separated from Boss (never clips or passes through)
                float bx = _boss.transform.position.x;
                float maxAllowedX = bx - minBossSeparation;
                if (nextX > maxAllowedX)
                    nextX = maxAllowedX;
                nextX = Mathf.Clamp(nextX, -arenaHalfWidth, arenaHalfWidth);
            }

            p.x = nextX;
            p.y = 0f;
            p.z = 0f;
            transform.position = p;
        }

        private void UpdateFacing()
        {
            if (_boss == null) return;
            float dx = _boss.transform.position.x - transform.position.x;
            if (Mathf.Abs(dx) > 0.05f)
            {
                FacingSign = dx >= 0f ? 1f : -1f;
                if (visual != null) visual.SetFacing(FacingSign);
            }
        }

        private float DistanceToBoss()
        {
            if (_boss == null) return 999f;
            return Mathf.Abs(_boss.transform.position.x - transform.position.x);
        }

        // --- Actions ---

        private void StartSmoothLunge(float signedDistance, float duration)
        {
            if (duration <= 0.01f) return;
            _lungeVelocity = signedDistance / duration;
            _lungeRemaining = duration;
        }

        private void UpdateSmoothLunge(float dt)
        {
            if (_lungeRemaining <= 0f) return;
            float step = Mathf.Min(dt, _lungeRemaining);
            _lungeRemaining -= step;
            MoveX(_lungeVelocity * step, allowPassThrough: false);
        }

        private void StartAttackHop(float duration)
        {
            _attackHopDuration = duration;
            _attackHopElapsed = 0f;
        }

        private void UpdateAttackHop(float dt)
        {
            if (_attackHopDuration <= 0f || State == PlayerState.Dodging) return;
            _attackHopElapsed += dt;
            if (_attackHopElapsed >= _attackHopDuration)
            {
                _attackHopDuration = 0f;
                if (visual != null) visual.SetVerticalHop(0f);
                return;
            }
            float t = Mathf.Clamp01(_attackHopElapsed / _attackHopDuration);
            if (visual != null) visual.SetVerticalHop(Mathf.Sin(t * Mathf.PI) * 0.26f);
        }

        private void StartLightAttack()
        {
            bool isCounterFlash = Time.time <= _counterSlashReadyUntil;
            _counterSlashReadyUntil = -10f;

            if (Time.time > _comboResetTime) _comboStep = 0;
            _comboStep = isCounterFlash ? 4 : (_comboStep % 4) + 1;
            _comboResetTime = Time.time + 1.05f;

            _currentAttackKind = AttackKind.PlayerLight;
            State = PlayerState.AttackWindup;
            // Blazing 4-hit hack-and-slash rhythm: 1타(신속베기) -> 2타(치켜베기) -> 3타(회전 2연참!) -> 4타(어사 일섬 검기 폭발!)
            _attackHitDelay = _comboStep == 4 ? 0.10f : 0.07f;
            _stateTimer = _comboStep == 4 ? 0.26f : (_comboStep == 3 ? 0.24f : 0.18f);
            _attackHitFired = false;
            _multiHitRemaining = _comboStep == 3 ? 1 : 0;
            _nextMultiHitDelay = 0.14f;

            // Controlled forward step on light attack (preserves 2.5D footsies & spacing; no 3.2m across-screen teleport!)
            float room = Mathf.Max(0f, DistanceToBoss() - minBossSeparation);
            if (room > 0.02f)
            {
                float maxStep = isCounterFlash ? 1.35f : (_comboStep == 4 ? 1.10f : 0.85f);
                StartSmoothLunge(FacingSign * Mathf.Min(maxStep, room), 0.08f);
            }
            if (_comboStep >= 3) StartAttackHop(_comboStep == 4 ? 0.26f : 0.18f);

            _juice?.TriggerSlashSwing(heavy: _comboStep >= 3);
            if (visual != null)
            {
                visual.SetAttackComboVariant(Mathf.Min(_comboStep, 3));
                if (_comboStep == 2) visual.SetPose(WeaponPose.SlashSwing);
                else if (_comboStep == 3) visual.SetPose(WeaponPose.SpinSlash);
                else if (_comboStep == 4)
                {
                    visual.SetPose(WeaponPose.ExecutionFinish);
                    visual.TriggerFlash(isCounterFlash ? new Color(1.0f, 0.80f, 0.15f, 1f) : new Color(1.0f, 0.35f, 0.18f, 1f), 0.24f);
                }
                else visual.SetPose(WeaponPose.SlashSwing);
            }

            if (isCounterFlash)
            {
                OnCombatFeedback?.Invoke("역습 일섬!", new Color(1.0f, 0.84f, 0.16f, 1f));
            }
        }

        private void StartHeavyThrust()
        {
            _currentAttackKind = AttackKind.PlayerHeavy;
            State = PlayerState.AttackWindup;
            _attackHitDelay = 0.11f;
            _stateTimer = 0.28f;
            _attackHitFired = false;
            _multiHitRemaining = 0;

            float room = Mathf.Max(0f, DistanceToBoss() - minBossSeparation);
            if (room > 0.02f) StartSmoothLunge(FacingSign * Mathf.Min(1.35f, room), 0.10f);

            _juice?.TriggerSlashSwing(heavy: true);
            if (visual != null)
            {
                visual.SetPose(WeaponPose.ThrustExtend);
                visual.TriggerFlash(new Color(1.0f, 0.65f, 0.20f, 1f), 0.20f);
            }
        }

        private void StartMapaeStrike()
        {
            // 어사 비검: 마패 검풍 (3-Hit High-Speed Golden Storm Slash Skill!)
            _nextSpecialTime = Time.time + specialCooldown;
            _currentAttackKind = AttackKind.PlayerMapae;
            State = PlayerState.SpecialMapae;
            _attackHitDelay = 0.06f;
            _stateTimer = 0.30f;
            _attackHitFired = false;
            _multiHitRemaining = 2; // 3 rapid hits total (0.06s, 0.135s, 0.21s)!
            _nextMultiHitDelay = 0.135f;
            _ganpaActiveUntil = Time.time + 0.26f;

            float room = Mathf.Max(0f, DistanceToBoss() - minBossSeparation);
            if (room > 0.02f) StartSmoothLunge(FacingSign * Mathf.Min(1.25f, room), 0.09f);
            StartAttackHop(0.24f);

            _juice?.TriggerSlashSwing(heavy: true);
            if (visual != null)
            {
                visual.SetPose(WeaponPose.GanpaDeflect);
                visual.TriggerFlash(new Color(1.0f, 0.78f, 0.08f, 1f), 0.28f);
            }
            OnCombatFeedback?.Invoke(IsMapaeEmpowered ? "진(眞) 어사비검: 천벌 3연참!" : "어사비검: 마패 3연참!", new Color(1.0f, 0.80f, 0.12f, 1f));
        }

        private void StartDodge()
        {
            _nextDodgeTime = Time.time + dodgeCooldown;
            State = PlayerState.Dodging;
            _dodgeElapsed = 0f;

            float inputAxis = GetMoveAxis();
            // Default to retreating dodge if no direction held (GDD: 후방 회피)
            _dodgeDir = Mathf.Abs(inputAxis) > 0.1f ? Mathf.Sign(inputAxis) : -FacingSign;

            if (visual != null)
            {
                visual.SetMoveSpeed(0f);
                visual.SetPose(WeaponPose.DodgeLeap);
                visual.TriggerFlash(new Color(0.75f, 0.85f, 0.95f, 1f), 0.16f);
            }
        }

        private void PerformGanpaCounter()
        {
            _nextSpecialTime = Time.time + 0.45f;
            State = PlayerState.AttackRecovery;
            _stateTimer = 0.28f;
            _parryHeld = false;

            // Smooth rapid step into the boss's spear (no 1-frame teleport)
            if (_boss != null)
            {
                float targetX = _boss.transform.position.x - FacingSign * (minBossSeparation + 0.25f);
                float dx = Mathf.Clamp(targetX, -arenaHalfWidth, arenaHalfWidth) - transform.position.x;
                StartSmoothLunge(dx, 0.12f);
                Vector3 clashPos = visual != null ? visual.GetClashPoint() : transform.position + Vector3.up * 1.2f;
                _boss.ReceiveGanpaCounter(clashPos);
                _juice?.TriggerGanpaSuccess(clashPos);
            }

            if (visual != null)
            {
                visual.SetPose(WeaponPose.GanpaDeflect);
                visual.TriggerFlash(new Color(0.95f, 0.75f, 0.05f, 1f), 0.28f);
            }

            OnCombatFeedback?.Invoke("간파!", new Color(0.95f, 0.75f, 0.08f, 1f));
        }

        private void StartExecution()
        {
            State = PlayerState.Executing;
            _stateTimer = 0.52f;
            _attackHeld = false;

            if (_boss != null)
            {
                float targetX = _boss.transform.position.x - FacingSign * (minBossSeparation + 0.15f);
                float dx = Mathf.Clamp(targetX, -arenaHalfWidth, arenaHalfWidth) - transform.position.x;
                StartSmoothLunge(dx, 0.14f);
                Vector3 clashPos = (_boss.transform.position + transform.position) * 0.5f + Vector3.up * 1.25f;
                _juice?.TriggerExecution(clashPos);
                _boss.ReceiveExecution();
            }

            if (visual != null)
            {
                visual.SetPose(WeaponPose.ExecutionFinish);
                visual.TriggerFlash(new Color(0.88f, 0.14f, 0.14f, 1f), 0.35f);
            }

            OnCombatFeedback?.Invoke("인살!", new Color(0.92f, 0.16f, 0.16f, 1f));
        }

        private void ResolvePlayerStrike()
        {
            if (_boss == null) return;
            float range = _currentAttackKind == AttackKind.PlayerHeavy ? attackRange + 0.35f : attackRange;
            if (DistanceToBoss() > range) return;

            float hpDmg;
            float postureDmg;
            bool empoweredMapaeFinisher = false;

            if (_currentAttackKind == AttackKind.PlayerMapae)
            {
                bool empowered = IsMapaeEmpowered;
                if (_multiHitRemaining == 0)
                {
                    empoweredMapaeFinisher = empowered;
                    IsMapaeEmpowered = false;
                }
                hpDmg = empowered ? 10f : 6f;
                postureDmg = empowered ? 16f : 10f;
            }
            else if (_currentAttackKind == AttackKind.PlayerHeavy)
            {
                hpDmg = 26f;
                postureDmg = 22f;
            }
            else
            {
                hpDmg = _comboStep == 4 ? 24f : (_comboStep == 3 ? 11f : 14f);
                postureDmg = _comboStep == 4 ? 22f : (_comboStep == 3 ? 10f : 12f);
            }

            // Spawn hit sparks & ring right in the visible blade-clash gap in front of the Boss's chest!
            Vector3 impactPos = _boss.transform.position - Vector3.right * (FacingSign * 0.45f) + Vector3.up * 1.32f;
            if (visual != null)
            {
                Vector3 bladePos = visual.GetBladeMidpoint();
                impactPos.x = Mathf.Lerp(bladePos.x, _boss.transform.position.x - FacingSign * 0.38f, 0.55f);
                impactPos.y = Mathf.Clamp(bladePos.y, 1.18f, 1.55f);
            }
            // Only Heavy Thrust, 4th Combo Finisher, or the final hit of Empowered Mapae counts as a heavy hit!
            bool isHeavyHit = _currentAttackKind == AttackKind.PlayerHeavy ||
                              (_currentAttackKind == AttackKind.PlayerLight && _comboStep >= 4) ||
                              empoweredMapaeFinisher;
            bool wasDeflected = _boss.ReceivePlayerHit(hpDmg, postureDmg, _currentAttackKind, impactPos, isHeavyHit);
            if (wasDeflected)
            {
                OnCombatFeedback?.Invoke("캉! 적 튕겨내기! 패링 대비!", new Color(1.0f, 0.82f, 0.22f, 1f));
            }
            else if (_currentAttackKind == AttackKind.PlayerHeavy)
            {
                OnCombatFeedback?.Invoke("강격 일섬!", new Color(1.0f, 0.72f, 0.22f, 1f));
            }
            else if (_currentAttackKind == AttackKind.PlayerLight && _comboStep == 4)
            {
                OnCombatFeedback?.Invoke("4단 어사 일섬!", new Color(1.0f, 0.78f, 0.18f, 1f));
            }
        }

        // --- Receiving Boss Strikes ---

        public DefenseOutcome ReceiveBossStrike(IncomingStrike strike, Vector3 clashPoint, out bool staggerBoss)
        {
            staggerBoss = false;
            if (State == PlayerState.Dead || State == PlayerState.Executing)
                return DefenseOutcome.Dodged;

            // 1. Dodge / Leap i-frames (evades thrusts and Phase 2 ground shockwave)
            if (IsDodging)
            {
                bool jumpedShockwave = strike.Kind == AttackKind.GroundShockwave;
                OnCombatFeedback?.Invoke(
                    jumpedShockwave ? "도약 회피!" : "회피",
                    jumpedShockwave ? new Color(0.95f, 0.78f, 0.18f, 1f) : new Color(0.82f, 0.88f, 0.95f, 1f));
                return DefenseOutcome.Dodged;
            }

            // 2. Reactive Ganpa window if Special was just pressed on a Perilous Thrust
            if (strike.Kind == AttackKind.PerilousThrust && Time.time <= _ganpaActiveUntil)
            {
                PerformGanpaCounter();
                return DefenseOutcome.GanpaCounter;
            }

            // 3. Perfect Parry (non-perilous strikes within 0.18s or 0.08s spam window)
            if (!strike.IsPerilous && IsParryWindowActive && State != PlayerState.GuardBroken)
            {
                _lastParryDidNotConnect = false;
                ConsecutiveParries++;
                if (ConsecutiveParries >= 3)
                {
                    IsMapaeEmpowered = true;
                }

                // Rule: Perfect Parry NEVER breaks guard even if posture is at 99%
                Stats.AddPosture(strike.ParrySelfPostureDamage, canBreak: false);
                _parryFlashTimer = 0.30f;
                // Unlock Counter-Flash Slash (역습 일섬!) for the next 0.85s after any Perfect Parry!
                _counterSlashReadyUntil = Time.time + 0.85f;
                StartSmoothLunge(-FacingSign * 0.14f, 0.12f);

                if (visual != null)
                {
                    visual.SetPose(WeaponPose.ParryFlash);
                    visual.TriggerFlash(new Color(0.96f, 0.74f, 0.05f, 1f), 0.24f);
                }

                // Emit golden clash ring & sparks directly at the exact point where the two 3D sword blades cross!
                Vector3 bladeClashPos = visual != null ? visual.GetBladeMidpoint() : clashPoint;
                _juice?.TriggerPerfectParry(bladeClashPos, ConsecutiveParries);

                staggerBoss = (strike.Kind == AttackKind.DelayedHeavySlam || strike.StaggerBossOnParry);
                OnCombatFeedback?.Invoke(
                    staggerBoss ? "튕겨내기! 빈틈!" : (ConsecutiveParries >= 3 ? $"튕겨내기! x{ConsecutiveParries}" : "튕겨내기!"),
                    new Color(0.96f, 0.74f, 0.05f, 1f));

                return DefenseOutcome.PerfectParry;
            }

            // 4. Regular Guard (blocks non-perilous strikes, builds heavy player posture)
            if (!strike.IsPerilous && IsGuarding)
            {
                ConsecutiveParries = 0;
                bool broke = Stats.AddPosture(strike.GuardPostureDamage, canBreak: true);
                if (broke)
                {
                    EnterGuardBreak(clashPoint);
                    return DefenseOutcome.GuardBreak;
                }

                Vector3 guardBladePos = visual != null ? visual.GetBladeMidpoint() : clashPoint;
                _juice?.TriggerGuardBlock(guardBladePos);
                if (visual != null)
                {
                    visual.TriggerGuardImpact();
                    visual.TriggerFlash(new Color(0.85f, 0.88f, 0.92f, 1f), 0.12f);
                }
                OnCombatFeedback?.Invoke("막기", new Color(0.80f, 0.84f, 0.90f, 1f));
                return DefenseOutcome.Guard;
            }

            // 5. Direct Hit (unblocked or Perilous Thrust piercing guard)
            ConsecutiveParries = 0;
            Stats.TakeHpDamage(strike.HpDamage);
            bool postureBroke = Stats.AddPosture(strike.HitPostureDamage, canBreak: true);

            if (Stats.IsHpDepleted)
            {
                State = PlayerState.Dead;
                _juice?.TriggerGuardBreak(clashPoint);
                if (visual != null) visual.SetDead(true);
                _duel?.NotifyPlayerDefeated();
                return DefenseOutcome.Hit;
            }

            if (postureBroke)
            {
                EnterGuardBreak(clashPoint);
                return DefenseOutcome.GuardBreak;
            }

            State = PlayerState.HitFlinch;
            _stateTimer = 0.28f;
            StartSmoothLunge(-FacingSign * 0.48f, 0.14f);
            _juice?.TriggerNormalHit(clashPoint);
            if (visual != null)
            {
                visual.SetPose(WeaponPose.Staggered);
                visual.TriggerFlash(new Color(0.95f, 0.16f, 0.16f, 1f), 0.24f);
            }

            if (strike.Kind == AttackKind.GroundShockwave)
                OnCombatFeedback?.Invoke("바닥 충격파! 회피/도약", new Color(0.92f, 0.18f, 0.18f, 1f));
            else if (strike.IsPerilous)
                OnCombatFeedback?.Invoke("가드 불가! 간파/회피", new Color(0.92f, 0.18f, 0.18f, 1f));
            else
                OnCombatFeedback?.Invoke("피격! 자세 무너짐!", new Color(0.92f, 0.22f, 0.18f, 1f));

            return DefenseOutcome.Hit;
        }

        private void EnterGuardBreak(Vector3 clashPoint)
        {
            State = PlayerState.GuardBroken;
            _stateTimer = guardBreakDuration;
            _parryHeld = false;
            _attackHeld = false;
            StartSmoothLunge(-FacingSign * 0.60f, 0.18f);
            _juice?.TriggerGuardBreak(clashPoint);
            if (visual != null)
            {
                visual.SetPose(WeaponPose.StanceBrokenKneel);
                visual.TriggerFlash(new Color(0.92f, 0.20f, 0.12f, 1f), 0.35f);
            }
            OnCombatFeedback?.Invoke("가드 붕괴!", new Color(0.92f, 0.22f, 0.16f, 1f));
        }
    }
}
