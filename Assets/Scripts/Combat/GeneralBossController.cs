using System;
using UnityEngine;

namespace EosaBlade
{
    public enum BossState
    {
        IdleRecover,
        Approach,
        Combo2Hit_Windup1,
        Combo2Hit_Impact1,
        Combo2Hit_Windup2,
        Combo2Hit_Impact2,
        Combo2Hit_Windup3Phase2,
        DelayedSlam_Windup,
        PerilousThrust_Telegraph,
        AttackRecovery,
        ParriedStagger,
        Executable,
        PhaseTransition,
        Defeated
    }

    /// <summary>
    /// Boss 1 (타락한 조선 대장군) temporary combat AI:
    /// 2 Lives (목숨 2줄), Regular 2-hit slash -> Delayed overhead slam -> Perilous Thrust (殺, 0.6s telegraph, Ganpa counterable).
    /// </summary>
    public class GeneralBossController : MonoBehaviour
    {
        [Header("Movement & Reach")]
        [SerializeField] private float approachSpeed = 5.2f;
        [SerializeField] private float engageDistance = 1.82f;
        [SerializeField] private float strikeReach = 2.75f;
        [SerializeField] private float thrustReach = 3.50f;
        [SerializeField] private float arenaHalfWidth = 6.2f;
        [SerializeField] private float minPlayerSeparation = 1.55f;

        [Header("Pattern Timings (Seconds)")]
        [SerializeField] private float regularWindup1 = 0.42f;
        [SerializeField] private float regularWindup2 = 0.34f;
        [SerializeField] private float phase2Windup3 = 0.32f;
        [SerializeField] private float delayedSlamWindup = 0.64f;
        [SerializeField] private float perilousTelegraphDuration = 0.58f;
        [SerializeField] private float perilousGanpaEarlyWindow = 0.40f;
        [SerializeField] private float executableDuration = 3.50f;

        [Header("References")]
        [SerializeField] private CombatantVisual visual;
        [SerializeField] private Material shockwaveParticleMaterial;

        public CombatStats Stats { get; } = new CombatStats
        {
            MaxHp = 120f,
            MaxPosture = 100f,
            MaxLives = 2,
            PostureRecoveryDelay = 2.0f,
            BasePostureRecoveryPerSec = 13f
        };

        public BossState State { get; private set; } = BossState.IdleRecover;
        public int CurrentPhase => Stats.CurrentLives <= 1 ? 2 : 1;
        public bool IsExecutable => State == BossState.Executable;
        public bool IsPerilousCounterable =>
            State == BossState.PerilousThrust_Telegraph && _stateTimer <= perilousGanpaEarlyWindow;
        public float FacingSign { get; private set; } = -1f;

        public event Action<bool> OnDangerSignChanged;
        public event Action<bool> OnExecutableChanged;
        public event Action<string, Color> OnBossBanner;

        private CombatDuelManager _duel;
        private PlayerController _player;
        private CombatJuiceController _juice;

        private float _stateTimer;
        private int _patternIndex;
        private int _lastPatternIndex = -1;
        private int _consecutiveHitsTaken;
        private bool _dangerVisible;
        private bool _execVisible;
        private bool _glintFired;

        // Smooth physical lunge velocity (eliminates 1-frame teleportation!)
        private float _lungeVelocity;
        private float _lungeRemaining;

        // Phase 2 ground shockwave state (zero per-frame GC)
        private bool _shockwaveActive;
        private bool _shockwaveResolved;
        private float _shockwaveX;
        private float _shockwaveDir;
        private float _shockwaveTimer;
        private ParticleSystem _shockwaveParticles;
        private const float ShockwaveSpeed = 7.4f;
        private const float ShockwaveDuration = 1.15f;
        private const float ShockwaveHitHalfWidth = 0.58f;

        private void Awake()
        {
            InitShockwaveParticles();
        }

        public void Bind(CombatDuelManager duel, PlayerController player, CombatJuiceController juice, CombatantVisual vis, Material shockwaveMat = null)
        {
            _duel = duel;
            _player = player;
            _juice = juice;
            if (vis != null) visual = vis;
            if (shockwaveMat != null) shockwaveParticleMaterial = shockwaveMat;
        }

        public void ResetForDuel(Vector3 spawnPos)
        {
            transform.position = spawnPos;
            Stats.ResetAll();
            State = BossState.IdleRecover;
            _stateTimer = 0.25f;
            _patternIndex = 0;
            _lastPatternIndex = -1;
            _consecutiveHitsTaken = 0;
            _glintFired = false;
            SetDangerSign(false);
            SetExecutableSign(false);
            StopShockwave();
            _lungeRemaining = 0f;
            _lungeVelocity = 0f;

            if (visual != null)
            {
                visual.SetDead(false);
                visual.SetMoveSpeed(0f);
                visual.SetVerticalHop(0f);
                visual.SetFacing(-1f);
                visual.SetPose(WeaponPose.Idle);
            }
        }

        private void Update()
        {
            if (_duel == null || !_duel.IsDuelActive || State == BossState.Defeated) return;

            float dt = Time.deltaTime;
            UpdateFacing();
            UpdateSmoothLunge(dt);
            UpdateShockwave(dt);

            if (State == BossState.IdleRecover || State == BossState.Approach)
            {
                Stats.TickPostureRecovery(dt, isGuardingBonus: false);
            }

            switch (State)
            {
                case BossState.IdleRecover:
                    _stateTimer -= dt;
                    if (visual != null)
                    {
                        visual.SetMoveSpeed(0f);
                        visual.SetPose(WeaponPose.Idle);
                    }
                    if (_stateTimer <= 0f)
                    {
                        State = BossState.Approach;
                    }
                    break;

                case BossState.Approach:
                    if (visual != null) visual.SetPose(WeaponPose.Idle);
                    float dist = DistanceToPlayer();
                    if (dist > engageDistance)
                    {
                        if (visual != null) visual.SetMoveSpeed(1f);
                        float spd = CurrentPhase == 2 ? approachSpeed * 1.18f : approachSpeed;
                        MoveX(FacingSign * spd * dt);
                    }
                    else
                    {
                        if (visual != null) visual.SetMoveSpeed(0f);
                        BeginNextPattern();
                    }
                    break;

                case BossState.Combo2Hit_Windup1:
                    _stateTimer -= dt;
                    CheckPreStrikeGlint(0.15f);
                    if (_stateTimer <= 0f)
                    {
                        if (visual != null) visual.SetAttackComboVariant(1);
                        DeliverStrike(new IncomingStrike
                        {
                            Kind = AttackKind.NormalSlash,
                            HpDamage = 16f,
                            GuardPostureDamage = 24f,
                            ParryBossPostureDamage = 24f,
                            ParrySelfPostureDamage = 8f,
                            HitPostureDamage = 18f,
                            IsPerilous = false
                        }, strikeReach, lunge: 0.48f);

                        if (State == BossState.Combo2Hit_Windup1)
                        {
                            // Hold the extended blade contact pose for 0.12s so the sword visibly clashes/bites during hitstop!
                            State = BossState.Combo2Hit_Impact1;
                            _stateTimer = 0.12f;
                        }
                    }
                    break;

                case BossState.Combo2Hit_Impact1:
                    _stateTimer -= dt;
                    if (_stateTimer <= 0f)
                    {
                        State = BossState.Combo2Hit_Windup2;
                        _stateTimer = regularWindup2;
                        _glintFired = false;
                        if (visual != null)
                        {
                            visual.SetAttackComboVariant(2);
                            visual.SetPose(WeaponPose.WindupForward);
                        }
                    }
                    break;

                case BossState.Combo2Hit_Windup2:
                    _stateTimer -= dt;
                    CheckPreStrikeGlint(0.14f);
                    if (_stateTimer <= 0f)
                    {
                        if (visual != null) visual.SetAttackComboVariant(2);
                        DeliverStrike(new IncomingStrike
                        {
                            Kind = AttackKind.NormalSlash,
                            HpDamage = 18f,
                            GuardPostureDamage = 26f,
                            ParryBossPostureDamage = 28f,
                            ParrySelfPostureDamage = 8f,
                            HitPostureDamage = 20f,
                            IsPerilous = false,
                            StaggerBossOnParry = (CurrentPhase == 1)
                        }, strikeReach, lunge: 0.52f);

                        if (State == BossState.Combo2Hit_Windup2)
                        {
                            if (CurrentPhase == 2)
                            {
                                State = BossState.Combo2Hit_Impact2;
                                _stateTimer = 0.11f;
                            }
                            else
                            {
                                EnterRecovery(0.58f);
                            }
                        }
                    }
                    break;

                case BossState.Combo2Hit_Impact2:
                    _stateTimer -= dt;
                    if (_stateTimer <= 0f)
                    {
                        State = BossState.Combo2Hit_Windup3Phase2;
                        _stateTimer = phase2Windup3;
                        _glintFired = false;
                        if (visual != null)
                        {
                            visual.SetAttackComboVariant(3);
                            visual.SetPose(WeaponPose.SpinSlash);
                        }
                    }
                    break;

                case BossState.Combo2Hit_Windup3Phase2:
                    _stateTimer -= dt;
                    CheckPreStrikeGlint(0.14f);
                    if (_stateTimer <= 0f)
                    {
                        DeliverStrike(new IncomingStrike
                        {
                            Kind = AttackKind.NormalSlash,
                            HpDamage = 20f,
                            GuardPostureDamage = 28f,
                            ParryBossPostureDamage = 32f,
                            ParrySelfPostureDamage = 9f,
                            HitPostureDamage = 22f,
                            IsPerilous = false,
                            StaggerBossOnParry = true
                        }, strikeReach + 0.15f, lunge: 0.52f);

                        if (State == BossState.Combo2Hit_Windup3Phase2)
                        {
                            EnterRecovery(0.56f);
                        }
                    }
                    break;

                case BossState.DelayedSlam_Windup:
                    _stateTimer -= dt;
                    CheckPreStrikeGlint(0.16f);
                    if (_stateTimer <= 0f)
                    {
                        DeliverStrike(new IncomingStrike
                        {
                            Kind = AttackKind.DelayedHeavySlam,
                            HpDamage = 30f,
                            GuardPostureDamage = 42f,
                            ParryBossPostureDamage = 36f,
                            ParrySelfPostureDamage = 10f,
                            HitPostureDamage = 30f,
                            IsPerilous = false,
                            StaggerBossOnParry = true
                        }, strikeReach + 0.30f, lunge: 0.58f);

                        if (State == BossState.DelayedSlam_Windup)
                        {
                            Vector3 slamPos = transform.position + Vector3.right * (FacingSign * 1.0f);
                            _juice?.TriggerHeavySlamImpact(slamPos);
                            if (CurrentPhase == 2)
                            {
                                SpawnGroundShockwave(slamPos.x, FacingSign);
                                OnBossBanner?.Invoke("바닥 충격파! 회피/도약", new Color(0.95f, 0.28f, 0.16f, 1f));
                            }
                            EnterRecovery(0.65f);
                        }
                    }
                    break;

                case BossState.PerilousThrust_Telegraph:
                    _stateTimer -= dt;
                    if (_stateTimer <= 0f)
                    {
                        SetDangerSign(false);
                        DeliverStrike(new IncomingStrike
                        {
                            Kind = AttackKind.PerilousThrust,
                            HpDamage = 34f,
                            GuardPostureDamage = 45f,
                            ParryBossPostureDamage = 0f,
                            ParrySelfPostureDamage = 0f,
                            HitPostureDamage = 32f,
                            IsPerilous = true
                        }, thrustReach, lunge: 1.35f);

                        if (State == BossState.PerilousThrust_Telegraph)
                        {
                            EnterRecovery(0.64f);
                        }
                    }
                    break;

                case BossState.AttackRecovery:
                    _stateTimer -= dt;
                    if (_stateTimer <= 0f)
                    {
                        State = BossState.IdleRecover;
                        _stateTimer = CurrentPhase == 2 ? 0.14f : 0.22f;
                    }
                    break;

                case BossState.ParriedStagger:
                    _stateTimer -= dt;
                    if (visual != null) visual.SetPose(WeaponPose.Staggered);
                    if (_stateTimer <= 0f)
                    {
                        State = BossState.IdleRecover;
                        _stateTimer = 0.12f;
                    }
                    break;

                case BossState.Executable:
                    if (visual != null) visual.SetPose(WeaponPose.StanceBrokenKneel);
                    // Rule: If Boss HP is already 0, stay permanently staggered waiting for 인살 (NEVER refill HP!)
                    if (Stats.IsHpDepleted)
                    {
                        break;
                    }
                    _stateTimer -= dt;
                    if (_stateTimer <= 0f)
                    {
                        // Posture-only break expired while HP > 0: recover 35% posture and resume
                        SetExecutableSign(false);
                        Stats.SetPosture(Stats.MaxPosture * 0.65f);
                        State = BossState.IdleRecover;
                        _stateTimer = 0.45f;
                    }
                    break;

                case BossState.PhaseTransition:
                    _stateTimer -= dt;
                    if (visual != null) visual.SetPose(WeaponPose.WindupOverhead);
                    if (_stateTimer <= 0f)
                    {
                        State = BossState.Approach;
                    }
                    break;
            }
        }

        private void CheckPreStrikeGlint(float threshold)
        {
            if (!_glintFired && _stateTimer <= threshold)
            {
                _glintFired = true;
                // Sharp white-gold blade glint star VFX + metallic ping right as the Perfect Parry window opens!
                if (visual != null)
                {
                    visual.TriggerFlash(new Color(1.45f, 1.15f, 0.35f, 1f), 0.14f);
                    _juice?.TriggerBladeGlint(visual.GetBladeMidpoint(), perilous: false);
                }
            }
        }

        private void BeginNextPattern(bool fastCounter = false)
        {
            _consecutiveHitsTaken = 0;
            _glintFired = false;

            // Dynamic pattern selection: favors rapid sword exchanges at close range, mixes in Slam & Perilous Thrust without repeating
            int chosen;
            if (fastCounter)
            {
                // Immediate counter-slash after Boss deflects Player!
                chosen = UnityEngine.Random.value < 0.72f ? 0 : 1;
            }
            else
            {
                int roll = UnityEngine.Random.Range(0, 100);
                if (roll < 50) chosen = 0;
                else if (roll < 76) chosen = 1;
                else chosen = 2;
                if (chosen == _lastPatternIndex && chosen != 0)
                    chosen = 0;
            }
            _lastPatternIndex = chosen;
            _patternIndex = chosen;

            switch (chosen)
            {
                case 0:
                    // Pattern 1: Rapid 2-hit / 3-hit sword exchange (챙! 챙! 캉!)
                    State = BossState.Combo2Hit_Windup1;
                    float w1 = CurrentPhase == 2 ? regularWindup1 * 0.88f : regularWindup1;
                    _stateTimer = fastCounter ? w1 * 0.78f : w1;
                    if (visual != null)
                    {
                        visual.SetAttackComboVariant(1);
                        visual.SetPose(CurrentPhase == 2 ? WeaponPose.SpinSlash : WeaponPose.WindupForward);
                    }
                    break;

                case 1:
                    // Pattern 2: Heavy Overhead Cleave (쾅! + Phase 2 Shockwave)
                    State = BossState.DelayedSlam_Windup;
                    _stateTimer = fastCounter ? delayedSlamWindup * 0.80f : delayedSlamWindup;
                    if (visual != null) visual.SetPose(WeaponPose.WindupOverhead);
                    break;

                case 2:
                    // Pattern 3: Perilous Thrust (殺, Ganpa counter)
                    State = BossState.PerilousThrust_Telegraph;
                    _stateTimer = perilousTelegraphDuration;
                    SetDangerSign(true);
                    _juice?.TriggerDangerTelegraph();
                    if (visual != null) visual.SetPose(WeaponPose.WindupThrustPerilous);
                    break;
            }
        }

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
            MoveX(_lungeVelocity * step);
        }

        private void DeliverStrike(IncomingStrike strike, float reach, float lunge)
        {
            // Cap forward lunge strictly by available gap before minPlayerSeparation (close in right to blade-clash distance!)
            float room = Mathf.Max(0f, DistanceToPlayer() - minPlayerSeparation);
            float safeLunge = Mathf.Min(lunge, room);
            if (safeLunge > 0.02f)
            {
                StartSmoothLunge(FacingSign * safeLunge, strike.Kind == AttackKind.PerilousThrust ? 0.12f : 0.08f);
            }
            _juice?.TriggerSlashSwing(heavy: strike.Kind != AttackKind.NormalSlash);

            if (visual != null)
            {
                if (strike.Kind == AttackKind.PerilousThrust)
                    visual.SetPose(WeaponPose.ThrustExtend);
                else if (strike.Kind == AttackKind.DelayedHeavySlam)
                    visual.SetPose(WeaponPose.ExecutionFinish);
                else if (CurrentPhase == 2 && State == BossState.Combo2Hit_Windup3Phase2)
                    visual.SetPose(WeaponPose.SpinSlash);
                else
                    visual.SetPose(WeaponPose.SlashSwing);
            }

            if (_player == null || DistanceToPlayer() > reach) return;

            Vector3 clashPos = visual != null ? visual.GetBladeMidpoint() : (_player.transform.position + transform.position) * 0.5f + Vector3.up * 1.32f;
            DefenseOutcome outcome = _player.ReceiveBossStrike(strike, clashPos, out bool staggerBoss);

            if (outcome == DefenseOutcome.PerfectParry)
            {
                // Keep blades locked at clash distance during hitstop, with a tight 0.16m knockback skid!
                StartSmoothLunge(-FacingSign * 0.16f, 0.12f);
                bool postureFull = Stats.AddPosture(strike.ParryBossPostureDamage, canBreak: true);
                if (postureFull)
                {
                    EnterExecutableState();
                    return;
                }

                if (staggerBoss)
                {
                    State = BossState.ParriedStagger;
                    _stateTimer = 0.72f;
                }
            }
            else if (outcome == DefenseOutcome.Guard)
            {
                Stats.AddPosture(4f, canBreak: false);
            }
        }

        // --- Receiving Player Offence ---

        public bool ReceivePlayerHit(float hpDamage, float postureDamage, AttackKind kind, Vector3 impactPos, bool isHeavyHit = false)
        {
            if (State == BossState.Defeated || State == BossState.PhaseTransition) return false;

            if (State == BossState.Executable)
            {
                return false;
            }

            // Sekiro ping-pong rhythm:
            // When Boss is neutral/approaching: Hit 1 cuts in, Hit 2 is blocked with his sword ("챙!", 0 HP damage), Hit 3+ (including heavy/mapae spam!) is deflected ("캉!") into an immediate counter-slash!
            bool isNeutral = (State == BossState.IdleRecover || State == BossState.Approach || State == BossState.AttackRecovery);
            bool canDeflectBack = isNeutral && _consecutiveHitsTaken >= 2;

            if (canDeflectBack)
            {
                _consecutiveHitsTaken = 0;
                Stats.AddPosture(postureDamage * 0.55f, canBreak: true);
                Vector3 deflectPos = visual != null ? visual.GetBladeMidpoint() : impactPos;
                _juice?.TriggerPerfectParry(deflectPos, 2);
                if (visual != null)
                {
                    visual.SetPose(WeaponPose.ParryFlash);
                    visual.TriggerFlash(new Color(1.2f, 0.88f, 0.25f, 1f), 0.22f);
                }

                if (Stats.IsPostureFull)
                {
                    EnterExecutableState();
                    return true;
                }

                // Immediately transition into fast counter-slash after deflecting Player!
                BeginNextPattern(fastCounter: true);
                return true;
            }

            // On Hit 2 in neutral, Boss raises his 대도 to block ("챙!") with silver-gold sparks (0 HP damage!) before deflecting the next blow!
            if (isNeutral && kind == AttackKind.PlayerLight && _consecutiveHitsTaken == 1 && !isHeavyHit)
            {
                _consecutiveHitsTaken++;
                bool guardBroke = Stats.AddPosture(postureDamage * 0.85f, canBreak: true);
                Vector3 blockPos = visual != null ? visual.GetBladeMidpoint() : impactPos;
                _juice?.TriggerGuardBlock(blockPos);
                if (visual != null)
                {
                    visual.SetPose(WeaponPose.Guard);
                    visual.TriggerGuardImpact();
                    visual.TriggerFlash(new Color(0.92f, 0.95f, 1.0f, 1f), 0.14f);
                }
                if (guardBroke)
                {
                    EnterExecutableState();
                }
                return false;
            }

            // Bonus Punish Damage when Player counter-attacks while Boss is wide open in ParriedStagger!
            if (State == BossState.ParriedStagger)
            {
                hpDamage *= 1.35f;
                postureDamage *= 1.45f;
                isHeavyHit = true;
            }

            _consecutiveHitsTaken++;
            Stats.TakeHpDamage(hpDamage);
            bool postureFull = Stats.AddPosture(postureDamage, canBreak: true);

            // Visceral hit feedback: heavy hitstop + sparks + clash ring + visible recoil flinch!
            _juice?.TriggerPlayerSwordHit(impactPos, isHeavyHit);
            if (visual != null)
            {
                visual.TriggerFlash(new Color(1.0f, 0.32f, 0.20f, 1f), isHeavyHit ? 0.20f : 0.14f);
            }

            if (postureFull || Stats.IsHpDepleted)
            {
                EnterExecutableState();
                return false;
            }

            // Interrupt ONLY Idle/Approach/Recovery/ParriedStagger so Player cannot infinite-stunlock Boss out of active attack windups without parrying!
            bool interruptible = isNeutral || State == BossState.ParriedStagger;
            if (interruptible)
            {
                SetDangerSign(false);
                State = BossState.ParriedStagger;
                _stateTimer = isHeavyHit ? 0.42f : 0.26f;
                StartSmoothLunge(-FacingSign * (isHeavyHit ? 0.44f : 0.24f), 0.13f);
                if (visual != null) visual.SetPose(WeaponPose.Staggered);
            }
            else if (visual != null)
            {
                // During active Boss attack windups (hyper-armor), show impact jolt without cancelling the Boss's swing!
                visual.TriggerGuardImpact();
            }

            return false;
        }

        public void ReceiveGanpaCounter(Vector3 clashPos)
        {
            SetDangerSign(false);
            Stats.TakeHpDamage(16f);
            bool postureFull = Stats.AddPosture(48f, canBreak: true);

            if (visual != null)
            {
                visual.TriggerFlash(new Color(0.95f, 0.75f, 0.05f, 1f), 0.30f);
            }

            if (postureFull || Stats.IsHpDepleted)
            {
                EnterExecutableState();
                return;
            }

            State = BossState.ParriedStagger;
            _stateTimer = 0.95f;
        }

        public void ReceiveExecution()
        {
            SetDangerSign(false);
            SetExecutableSign(false);
            StopShockwave();
            _lungeRemaining = 0f;
            _lungeVelocity = 0f;

            bool hasMoreLives = Stats.ConsumeLife();
            if (!hasMoreLives)
            {
                State = BossState.Defeated;
                Stats.TakeHpDamage(Stats.MaxHp);
                if (visual != null) visual.SetDead(true);
                _duel?.NotifyBossDefeated();
                return;
            }

            // Transition to Life 2 (Phase 2)
            Stats.ResetForNextLife();
            State = BossState.PhaseTransition;
            _stateTimer = 1.05f;
            _patternIndex = 0;
            StartSmoothLunge(-FacingSign * 1.4f, 0.24f);

            if (visual != null)
            {
                visual.TriggerFlash(new Color(0.92f, 0.16f, 0.16f, 1f), 0.45f);
            }

            OnBossBanner?.Invoke("2페이즈 — 대장군의 분노", new Color(0.92f, 0.22f, 0.18f, 1f));
        }

        private void EnterExecutableState()
        {
            SetDangerSign(false);
            SetExecutableSign(true);
            StopShockwave();
            State = BossState.Executable;
            _stateTimer = executableDuration;
            _juice?.TriggerGuardBreak(transform.position + Vector3.up * 1.3f);
            if (visual != null)
            {
                visual.SetPose(WeaponPose.StanceBrokenKneel);
                visual.TriggerFlash(new Color(0.92f, 0.15f, 0.15f, 1f), 0.35f);
            }
            OnBossBanner?.Invoke("적 체간 붕괴! 인살!", new Color(0.95f, 0.20f, 0.16f, 1f));
        }

        private void EnterRecovery(float duration)
        {
            State = BossState.AttackRecovery;
            _stateTimer = CurrentPhase == 2 ? duration * 0.85f : duration;
        }

        private void SetDangerSign(bool active)
        {
            if (_dangerVisible == active) return;
            _dangerVisible = active;
            OnDangerSignChanged?.Invoke(active);
        }

        private void SetExecutableSign(bool active)
        {
            if (_execVisible == active) return;
            _execVisible = active;
            OnExecutableChanged?.Invoke(active);
        }

        private void UpdateFacing()
        {
            if (_player == null) return;
            if (State == BossState.Combo2Hit_Windup2 || State == BossState.PerilousThrust_Telegraph) return;

            float dx = _player.transform.position.x - transform.position.x;
            if (Mathf.Abs(dx) > 0.05f)
            {
                FacingSign = dx >= 0f ? 1f : -1f;
                if (visual != null) visual.SetFacing(FacingSign);
            }
        }

        private float DistanceToPlayer()
        {
            if (_player == null) return 999f;
            return Mathf.Abs(_player.transform.position.x - transform.position.x);
        }

        private void MoveX(float deltaX)
        {
            Vector3 p = transform.position;
            float nextX = Mathf.Clamp(p.x + deltaX, -arenaHalfWidth, arenaHalfWidth);

            if (_player != null)
            {
                // Strict 1:1 duel spacing: Boss ALWAYS stays right of Player by >= minPlayerSeparation (NEVER passes through!)
                float px = _player.transform.position.x;
                float minAllowedX = px + minPlayerSeparation;
                if (nextX < minAllowedX)
                    nextX = minAllowedX;
                nextX = Mathf.Clamp(nextX, -arenaHalfWidth, arenaHalfWidth);
            }

            p.x = nextX;
            p.y = 0f;
            p.z = 0f;
            transform.position = p;
        }

        // --- Phase 2 Ground Shockwave (jump/dodge with 회피/도약) ---

        private void InitShockwaveParticles()
        {
            var go = new GameObject("VFX_Phase2GroundShockwave");
            go.transform.SetParent(transform.parent, false);
            _shockwaveParticles = go.AddComponent<ParticleSystem>();

            var main = _shockwaveParticles.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.20f, 0.34f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.4f, 3.6f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.18f, 0.36f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.95f, 0.18f, 0.12f, 1f),
                new Color(1.0f, 0.68f, 0.12f, 1f));
            main.gravityModifier = 0.6f;
            main.maxParticles = 128;

            var emission = _shockwaveParticles.emission;
            emission.enabled = false;

            var shape = _shockwaveParticles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(0.35f, 0.12f, 0.75f);

            var velocity = _shockwaveParticles.velocityOverLifetime;
            velocity.enabled = true;
            velocity.y = new ParticleSystem.MinMaxCurve(2.2f);

            var sizeOverLife = _shockwaveParticles.sizeOverLifetime;
            sizeOverLife.enabled = true;
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));

            var renderer = _shockwaveParticles.GetComponent<ParticleSystemRenderer>();
            if (shockwaveParticleMaterial != null)
            {
                renderer.sharedMaterial = shockwaveParticleMaterial;
            }
            else
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    var mat = new Material(shader);
                    Texture2D tex = CreateSoftWaveTexture();
                    if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                    if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
                    if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
                    if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 2f);
                    if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
                    if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
                    mat.renderQueue = 3000;
                    renderer.sharedMaterial = mat;
                }
            }
        }

        private static Texture2D _sharedWaveTex;
        private static Texture2D CreateSoftWaveTexture()
        {
            if (_sharedWaveTex != null) return _sharedWaveTex;
            const int res = 32;
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float half = (res - 1) * 0.5f;
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            }
            tex.Apply(false, true);
            _sharedWaveTex = tex;
            return _sharedWaveTex;
        }

        private void SpawnGroundShockwave(float startX, float dir)
        {
            _shockwaveActive = true;
            _shockwaveResolved = false;
            _shockwaveX = startX;
            _shockwaveDir = dir >= 0f ? 1f : -1f;
            _shockwaveTimer = ShockwaveDuration;

            if (_shockwaveParticles != null)
            {
                _shockwaveParticles.transform.position = new Vector3(_shockwaveX, 0.16f, 0f);
                _shockwaveParticles.Emit(36);
            }
        }

        private void StopShockwave()
        {
            _shockwaveActive = false;
            _shockwaveResolved = true;
            _shockwaveTimer = 0f;
            if (_shockwaveParticles != null) _shockwaveParticles.Clear();
        }

        private void UpdateShockwave(float dt)
        {
            if (!_shockwaveActive) return;

            _shockwaveTimer -= dt;
            _shockwaveX += _shockwaveDir * ShockwaveSpeed * dt;

            if (_shockwaveParticles != null)
            {
                _shockwaveParticles.transform.position = new Vector3(_shockwaveX, 0.14f, 0f);
                _shockwaveParticles.Emit(8);
            }

            if (!_shockwaveResolved && _player != null)
            {
                if (Mathf.Abs(_shockwaveX - _player.transform.position.x) <= ShockwaveHitHalfWidth)
                {
                    _shockwaveResolved = true;
                    Vector3 hitPos = new Vector3(_player.transform.position.x, 0.35f, 0f);
                    _player.ReceiveBossStrike(new IncomingStrike
                    {
                        Kind = AttackKind.GroundShockwave,
                        HpDamage = 24f,
                        GuardPostureDamage = 35f,
                        ParryBossPostureDamage = 0f,
                        ParrySelfPostureDamage = 0f,
                        HitPostureDamage = 26f,
                        IsPerilous = true
                    }, hitPos, out _);
                }
            }

            if (_shockwaveTimer <= 0f || Mathf.Abs(_shockwaveX) > arenaHalfWidth + 1.5f)
            {
                _shockwaveActive = false;
            }
        }
    }
}
