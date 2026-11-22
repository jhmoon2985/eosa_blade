using System;
using UnityEngine;

namespace EosaBlade
{
    public enum AttackKind
    {
        NormalSlash,
        DelayedHeavySlam,
        PerilousThrust,
        GroundShockwave,
        PlayerLight,
        PlayerHeavy,
        PlayerMapae
    }

    public enum DefenseOutcome
    {
        PerfectParry,
        Guard,
        GuardBreak,
        GanpaCounter,
        Dodged,
        Hit
    }

    [Serializable]
    public struct IncomingStrike
    {
        public AttackKind Kind;
        public float HpDamage;
        public float GuardPostureDamage;
        public float ParryBossPostureDamage;
        public float ParrySelfPostureDamage;
        public float HitPostureDamage;
        public bool IsPerilous;
    }

    /// <summary>
    /// Sekiro-style Health + Posture (기세/체간) resource model.
    /// Lower HP slows down posture recovery. Perfect parry never triggers guard break.
    /// </summary>
    [Serializable]
    public class CombatStats
    {
        public float MaxHp = 100f;
        public float MaxPosture = 100f;
        public int MaxLives = 1;
        public float PostureRecoveryDelay = 1.6f;
        public float BasePostureRecoveryPerSec = 15f;

        public float CurrentHp { get; private set; }
        public float CurrentPosture { get; private set; }
        public int CurrentLives { get; private set; }

        private float _lastCombatActivityTime = -10f;

        public float HpNormalized => MaxHp > 0f ? Mathf.Clamp01(CurrentHp / MaxHp) : 0f;
        public float PostureNormalized => MaxPosture > 0f ? Mathf.Clamp01(CurrentPosture / MaxPosture) : 0f;
        public bool IsHpDepleted => CurrentHp <= 0.001f;
        public bool IsPostureFull => CurrentPosture >= MaxPosture - 0.001f;

        public void ResetAll()
        {
            CurrentHp = MaxHp;
            CurrentPosture = 0f;
            CurrentLives = MaxLives;
            _lastCombatActivityTime = -10f;
        }

        public void ResetForNextLife()
        {
            CurrentHp = MaxHp;
            CurrentPosture = 0f;
            _lastCombatActivityTime = Time.time;
        }

        public void TakeHpDamage(float amount)
        {
            if (amount <= 0f) return;
            CurrentHp = Mathf.Max(0f, CurrentHp - amount);
            _lastCombatActivityTime = Time.time;
        }

        /// <summary>
        /// Adds posture damage. Returns true if posture reached 100% and canBreak is true.
        /// When canBreak is false (Perfect Parry), posture is clamped at MaxPosture - 1.
        /// </summary>
        public bool AddPosture(float amount, bool canBreak)
        {
            if (amount <= 0f) return false;
            _lastCombatActivityTime = Time.time;

            if (!canBreak)
            {
                float cap = Mathf.Max(0f, MaxPosture - 1f);
                CurrentPosture = Mathf.Min(cap, CurrentPosture + amount);
                return false;
            }

            CurrentPosture = Mathf.Min(MaxPosture, CurrentPosture + amount);
            return CurrentPosture >= MaxPosture - 0.001f;
        }

        public void SetHp(float value)
        {
            CurrentHp = Mathf.Clamp(value, 0f, MaxHp);
        }

        public void SetPosture(float value)
        {
            CurrentPosture = Mathf.Clamp(value, 0f, MaxPosture);
        }

        public bool ConsumeLife()
        {
            CurrentLives = Mathf.Max(0, CurrentLives - 1);
            return CurrentLives > 0;
        }

        public void TouchCombatActivity()
        {
            _lastCombatActivityTime = Time.time;
        }

        /// <summary>
        /// Updates posture recovery using Sekiro HP-ratio scaling.
        /// Guarding slightly boosts player posture recovery when idle.
        /// </summary>
        public void TickPostureRecovery(float dt, bool isGuardingBonus = false)
        {
            if (CurrentPosture <= 0f) return;
            if (Time.time - _lastCombatActivityTime < PostureRecoveryDelay) return;

            float hpRatio = HpNormalized;
            // Lower HP dramatically slows posture recovery (100% HP -> 1.0x, 50% HP -> 0.45x, <=25% HP -> 0.15x)
            float hpMultiplier;
            if (hpRatio > 0.75f) hpMultiplier = 1.0f;
            else if (hpRatio > 0.50f) hpMultiplier = 0.70f;
            else if (hpRatio > 0.25f) hpMultiplier = 0.40f;
            else hpMultiplier = 0.15f;

            float guardBonus = isGuardingBonus ? 1.45f : 1.0f;
            float rate = BasePostureRecoveryPerSec * hpMultiplier * guardBonus;
            CurrentPosture = Mathf.Max(0f, CurrentPosture - rate * dt);
        }
    }
}
