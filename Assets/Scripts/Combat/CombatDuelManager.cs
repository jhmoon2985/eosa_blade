using UnityEngine;

namespace EosaBlade
{
    /// <summary>
    /// Coordinates the 1:1 duel lifecycle (start, win, defeat, instant zero-reload restart).
    /// </summary>
    public class CombatDuelManager : MonoBehaviour
    {
        [SerializeField] private PlayerController player;
        [SerializeField] private GeneralBossController boss;
        [SerializeField] private CombatJuiceController juice;
        [SerializeField] private CombatHUD hud;

        [SerializeField] private Vector3 playerSpawn = new Vector3(-2.1f, 0f, 0f);
        [SerializeField] private Vector3 bossSpawn = new Vector3(2.1f, 0f, 0f);
        [SerializeField] private float hardMinSeparation = 1.52f;
        [SerializeField] private float arenaHalfWidth = 6.2f;

        public bool IsDuelActive { get; private set; }

        public void Bind(PlayerController p, GeneralBossController b, CombatJuiceController j, CombatHUD h)
        {
            player = p;
            boss = b;
            juice = j;
            hud = h;
        }

        private void Start()
        {
            if (player != null && boss != null && juice != null)
            {
                player.Bind(this, boss, juice, player.GetComponent<CombatantVisual>());
                boss.Bind(this, player, juice, boss.GetComponent<CombatantVisual>());
            }

            if (hud != null)
            {
                hud.Bind(this, player, boss);
            }

            ResetDuel();
        }

        private void LateUpdate()
        {
            // Hard invariant solver: guarantees Boss & Player NEVER overlap or pass through each other even at arena walls
            if (player == null || boss == null) return;
            Vector3 pp = player.transform.position;
            Vector3 bp = boss.transform.position;
            float gap = bp.x - pp.x;
            if (gap < hardMinSeparation)
            {
                float deficit = hardMinSeparation - gap;
                pp.x -= deficit * 0.5f;
                bp.x += deficit * 0.5f;

                if (pp.x < -arenaHalfWidth)
                {
                    pp.x = -arenaHalfWidth;
                    bp.x = pp.x + hardMinSeparation;
                }
                else if (bp.x > arenaHalfWidth)
                {
                    bp.x = arenaHalfWidth;
                    pp.x = bp.x - hardMinSeparation;
                }

                pp.y = 0f; pp.z = 0f;
                bp.y = 0f; bp.z = 0f;
                player.transform.position = pp;
                boss.transform.position = bp;
            }
        }

        public void ResetDuel()
        {
            IsDuelActive = true;
            juice?.ResetJuice();
            player?.ResetForDuel(playerSpawn);
            boss?.ResetForDuel(bossSpawn);
            hud?.ResetHudState();
            hud?.ShowBanner("진검승부 — 타락한 조선 대장군", new Color(0.95f, 0.78f, 0.25f, 1f), 1.6f);
        }

        public void NotifyBossDefeated()
        {
            IsDuelActive = false;
            hud?.ShowEndOverlay(victory: true);
        }

        public void NotifyPlayerDefeated()
        {
            IsDuelActive = false;
            hud?.ShowEndOverlay(victory: false);
        }
    }
}
