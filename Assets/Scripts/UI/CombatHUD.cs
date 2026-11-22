using UnityEngine;
using UnityEngine.UI;

namespace EosaBlade
{
    /// <summary>
    /// Builds and drives the clean, scroll-free Korean ink-wash mobile combat HUD:
    /// Top-Center Boss HP + Center-Outward Posture + 2 Life Orbs, Top-Left Player HP/Posture,
    /// Center 殺 Danger Sign & 인살 Execution Seal, Bottom-Left ◀ ▶, Bottom-Right 3+1 Buttons (Parry largest).
    /// Zero mystery shapes, zero ScrollRect, minimal per-frame GC.
    /// </summary>
    public class CombatHUD : MonoBehaviour
    {
        [SerializeField] private Font uiFont;

        [Header("Art Sprites (Step 2)")]
        [SerializeField] private Sprite parryBtnSprite;
        [SerializeField] private Sprite attackBtnSprite;
        [SerializeField] private Sprite ganpaBtnSprite;
        [SerializeField] private Sprite dodgeBtnSprite;
        [SerializeField] private Sprite dangerSalSprite;

        // Palette from 2_5d.md (4-color principle)
        private static readonly Color InkDark = new Color(0.08f, 0.08f, 0.11f, 0.86f);
        private static readonly Color InkNavy = new Color(0.12f, 0.15f, 0.21f, 0.90f);
        private static readonly Color GoldAccent = new Color(0.95f, 0.72f, 0.02f, 1f);
        private static readonly Color CrimsonAccent = new Color(0.85f, 0.15f, 0.15f, 1f);
        private static readonly Color PaleWhite = new Color(0.91f, 0.93f, 0.95f, 1f);
        private static readonly Color MutedSlate = new Color(0.55f, 0.60f, 0.68f, 0.75f);

        private CombatDuelManager _duel;
        private PlayerController _player;
        private GeneralBossController _boss;

        // Dynamic UI references
        private Image _playerHpFill;
        private Image _playerPostureFill;
        private Text _playerStatusText;

        private Image _bossHpFill;
        private RectTransform _bossPostureFillRect;
        private Image _bossPostureFillImage;
        private float _bossPostureMaxWidth;
        private Image[] _bossLifeOrbs;

        private RectTransform _dangerSignRoot;
        private Text _executionSignText;
        private Text _feedbackText;
        private float _feedbackTimer;

        private Text _bannerText;
        private float _bannerTimer;

        private Text _attackBtnLabel;
        private Image _attackBtnRing;
        private Image _specialBtnRing;
        private Image _specialCooldownOverlay;
        private Image _dodgeCooldownOverlay;

        private GameObject _endOverlayRoot;
        private Text _endTitleText;

        private Text _fpsText;
        private float _fpsAccum;
        private int _fpsFrames;
        private float _fpsNextUpdate;

        private bool _dangerActive;
        private bool _execActive;
        private bool _hudBuilt;

        // Zero-GC dirty-check caches (avoid per-frame Canvas rebuilds & string allocations)
        private float _lastPlayerHp = -1f;
        private float _lastPlayerPosture = -1f;
        private float _lastBossHp = -1f;
        private float _lastBossPosture = -1f;
        private int _lastBossLives = -1;
        private int _lastStatusKey = -999;
        private bool _lastMapaeEmpowered;
        private float _lastSpecialCd = -1f;
        private float _lastDodgeCd = -1f;

        private static readonly string[] ParryStreakLabels =
        {
            "어사 · 암행어사",
            "연속 튕겨내기 x1",
            "연속 튕겨내기 x2",
            "연속 튕겨내기 x3",
            "연속 튕겨내기 x4",
            "연속 튕겨내기 x5",
            "연속 튕겨내기 x6",
            "연속 튕겨내기 x7",
            "연속 튕겨내기 x8",
            "연속 튕겨내기 x9",
            "연속 튕겨내기 x10+"
        };

        private static readonly string[] FpsLabels = BuildFpsLabels();
        private static string[] BuildFpsLabels()
        {
            var arr = new string[121];
            for (int i = 0; i < arr.Length; i++) arr[i] = i + " FPS";
            return arr;
        }

        // Cached anti-aliased sprites (created once, zero runtime allocation)
        private static Sprite _circleFillSprite;
        private static Sprite _circleRingSprite;
        private static Sprite _pillSprite;

        public void SetFont(Font font)
        {
            uiFont = font;
        }

        public void SetArtSprites(Sprite parry, Sprite attack, Sprite ganpa, Sprite dodge, Sprite dangerSal)
        {
            parryBtnSprite = parry;
            attackBtnSprite = attack;
            ganpaBtnSprite = ganpa;
            dodgeBtnSprite = dodge;
            dangerSalSprite = dangerSal;
        }

        public void Bind(CombatDuelManager duel, PlayerController player, GeneralBossController boss)
        {
            _duel = duel;
            _player = player;
            _boss = boss;

            if (!_hudBuilt)
            {
                BuildHudHierarchy();
                _hudBuilt = true;
            }

            if (_player != null)
            {
                _player.OnCombatFeedback -= HandleCombatFeedback;
                _player.OnCombatFeedback += HandleCombatFeedback;
            }

            if (_boss != null)
            {
                _boss.OnDangerSignChanged -= HandleDangerChanged;
                _boss.OnDangerSignChanged += HandleDangerChanged;
                _boss.OnExecutableChanged -= HandleExecutableChanged;
                _boss.OnExecutableChanged += HandleExecutableChanged;
                _boss.OnBossBanner -= HandleBossBanner;
                _boss.OnBossBanner += HandleBossBanner;
            }
        }

        public void ResetHudState()
        {
            _dangerActive = false;
            _execActive = false;
            if (_dangerSignRoot != null) _dangerSignRoot.gameObject.SetActive(false);
            if (_executionSignText != null) _executionSignText.gameObject.SetActive(false);
            if (_feedbackText != null) _feedbackText.gameObject.SetActive(false);
            if (_endOverlayRoot != null) _endOverlayRoot.SetActive(false);
        }

        public void ShowBanner(string message, Color color, float duration = 1.6f)
        {
            if (_bannerText == null) return;
            _bannerText.text = message;
            _bannerText.color = color;
            _bannerText.gameObject.SetActive(true);
            _bannerTimer = duration;
        }

        public void ShowEndOverlay(bool victory)
        {
            if (_endOverlayRoot == null) return;
            _endOverlayRoot.SetActive(true);
            _endTitleText.text = victory ? "승리 — 대장군 토벌" : "패배 — 다시 일어서라";
            _endTitleText.color = victory ? GoldAccent : CrimsonAccent;
        }

        private void HandleCombatFeedback(string text, Color color)
        {
            if (_feedbackText == null) return;
            _feedbackText.text = text;
            _feedbackText.color = color;
            _feedbackText.gameObject.SetActive(true);
            _feedbackTimer = 0.72f;
        }

        private void HandleDangerChanged(bool active)
        {
            _dangerActive = active;
            if (_dangerSignRoot != null)
            {
                _dangerSignRoot.gameObject.SetActive(active);
            }
        }

        private void HandleExecutableChanged(bool active)
        {
            _execActive = active;
            if (_executionSignText != null)
            {
                _executionSignText.gameObject.SetActive(active);
            }
            if (_attackBtnLabel != null)
            {
                _attackBtnLabel.text = active ? "인살!" : "공격";
                _attackBtnLabel.color = active ? CrimsonAccent : PaleWhite;
            }
            if (_attackBtnRing != null)
            {
                _attackBtnRing.color = active ? CrimsonAccent : PaleWhite;
            }
        }

        private void HandleBossBanner(string message, Color color)
        {
            ShowBanner(message, color, 1.8f);
        }

        private void Update()
        {
            if (!_hudBuilt) return;
            float dt = Time.unscaledDeltaTime;

            UpdateGauges();
            UpdateCombatPrompts(dt);
            UpdateFpsReadout(dt);
        }

        private void UpdateGauges()
        {
            if (_player != null)
            {
                float pHp = _player.Stats.HpNormalized;
                if (Mathf.Abs(pHp - _lastPlayerHp) > 0.001f)
                {
                    _lastPlayerHp = pHp;
                    _playerHpFill.fillAmount = pHp;
                }

                float pPos = _player.Stats.PostureNormalized;
                if (Mathf.Abs(pPos - _lastPlayerPosture) > 0.001f)
                {
                    _lastPlayerPosture = pPos;
                    _playerPostureFill.fillAmount = pPos;
                    _playerPostureFill.color = pPos > 0.78f ? CrimsonAccent : GoldAccent;
                }

                int statusKey;
                if (_player.State == PlayerState.GuardBroken) statusKey = -1;
                else if (_player.IsMapaeEmpowered) statusKey = -2;
                else statusKey = Mathf.Clamp(_player.ConsecutiveParries, 0, ParryStreakLabels.Length - 1);

                if (statusKey != _lastStatusKey)
                {
                    _lastStatusKey = statusKey;
                    if (statusKey == -1) _playerStatusText.text = "가드 붕괴!";
                    else if (statusKey == -2) _playerStatusText.text = "삼마패 강화 준비";
                    else _playerStatusText.text = ParryStreakLabels[statusKey];
                }

                bool mapae = _player.IsMapaeEmpowered;
                if (mapae != _lastMapaeEmpowered && _specialBtnRing != null)
                {
                    _lastMapaeEmpowered = mapae;
                    _specialBtnRing.color = mapae ? GoldAccent : PaleWhite;
                }

                float spCd = _player.SpecialCooldownNormalized;
                if (_specialCooldownOverlay != null && Mathf.Abs(spCd - _lastSpecialCd) > 0.005f)
                {
                    _lastSpecialCd = spCd;
                    _specialCooldownOverlay.fillAmount = spCd;
                }

                float dgCd = _player.DodgeCooldownNormalized;
                if (_dodgeCooldownOverlay != null && Mathf.Abs(dgCd - _lastDodgeCd) > 0.005f)
                {
                    _lastDodgeCd = dgCd;
                    _dodgeCooldownOverlay.fillAmount = dgCd;
                }
            }

            if (_boss != null)
            {
                float bHp = _boss.Stats.HpNormalized;
                if (Mathf.Abs(bHp - _lastBossHp) > 0.001f)
                {
                    _lastBossHp = bHp;
                    _bossHpFill.fillAmount = bHp;
                }

                // Symmetrical center-outward posture bar (dirty-checked)
                float bPos = _boss.Stats.PostureNormalized;
                if (Mathf.Abs(bPos - _lastBossPosture) > 0.001f)
                {
                    _lastBossPosture = bPos;
                    Vector2 size = _bossPostureFillRect.sizeDelta;
                    size.x = _bossPostureMaxWidth * bPos;
                    _bossPostureFillRect.sizeDelta = size;
                    _bossPostureFillImage.color = bPos > 0.80f ? CrimsonAccent : GoldAccent;
                }

                int lives = _boss.Stats.CurrentLives;
                if (lives != _lastBossLives)
                {
                    _lastBossLives = lives;
                    for (int i = 0; i < _bossLifeOrbs.Length; i++)
                    {
                        _bossLifeOrbs[i].color = i < lives ? CrimsonAccent : new Color(0.25f, 0.25f, 0.30f, 0.65f);
                    }
                }
            }
        }

        private void UpdateCombatPrompts(float dt)
        {
            if (_dangerActive && _dangerSignRoot != null)
            {
                float pulse = 1.0f + 0.14f * Mathf.Sin(Time.unscaledTime * 28f);
                _dangerSignRoot.localScale = Vector3.one * pulse;
            }

            if (_execActive && _executionSignText != null)
            {
                float pulse = 1.0f + 0.12f * Mathf.Sin(Time.unscaledTime * 14f);
                _executionSignText.rectTransform.localScale = Vector3.one * pulse;
            }

            if (_feedbackTimer > 0f && _feedbackText != null)
            {
                _feedbackTimer -= dt;
                float t = Mathf.Clamp01(_feedbackTimer / 0.72f);
                Color c = _feedbackText.color;
                c.a = Mathf.Clamp01(t * 1.6f);
                _feedbackText.color = c;
                if (_feedbackTimer <= 0f) _feedbackText.gameObject.SetActive(false);
            }

            if (_bannerTimer > 0f && _bannerText != null)
            {
                _bannerTimer -= dt;
                if (_bannerTimer <= 0f) _bannerText.gameObject.SetActive(false);
            }
        }

        private void UpdateFpsReadout(float dt)
        {
            if (_fpsText == null || dt <= 0.0001f) return;
            _fpsAccum += 1f / dt;
            _fpsFrames++;
            if (Time.unscaledTime >= _fpsNextUpdate)
            {
                int avg = Mathf.Clamp(Mathf.RoundToInt(_fpsAccum / Mathf.Max(1, _fpsFrames)), 0, FpsLabels.Length - 1);
                _fpsText.text = FpsLabels[avg];
                _fpsAccum = 0f;
                _fpsFrames = 0;
                _fpsNextUpdate = Time.unscaledTime + 0.5f;
            }
        }

        // --- Clean Programmatic UI Construction (Safe Area, Crisp Anti-Aliased Circles/Pills, Zero ScrollRect) ---

        private void BuildHudHierarchy()
        {
            EnsureSprites();
            if (uiFont == null)
            {
                uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }

            RectTransform safeRoot = CreatePanel("SafeAreaRoot", transform as RectTransform, Color.clear);
            Stretch(safeRoot);
            safeRoot.gameObject.AddComponent<SafeAreaFitter>();

            BuildTopLeftPlayerHud(safeRoot);
            BuildTopCenterBossHud(safeRoot);
            BuildTopRightUtility(safeRoot);
            BuildCenterCombatCues(safeRoot);
            BuildBottomLeftMovement(safeRoot);
            BuildBottomRightActionButtons(safeRoot);
            // Parent EndOverlay directly to Canvas root so the dark backdrop covers camera cutout/notch edges
            BuildEndOverlay(transform as RectTransform);
        }

        private void BuildTopLeftPlayerHud(RectTransform parent)
        {
            RectTransform box = CreatePanel("PlayerHud", parent, Color.clear);
            box.anchorMin = new Vector2(0f, 1f);
            box.anchorMax = new Vector2(0f, 1f);
            box.pivot = new Vector2(0f, 1f);
            box.anchoredPosition = new Vector2(36f, -26f);
            box.sizeDelta = new Vector2(380f, 108f);

            _playerStatusText = CreateText("PlayerLabel", box, "어사 · 암행어사", 22, PaleWhite, TextAnchor.MiddleLeft);
            _playerStatusText.rectTransform.anchorMin = new Vector2(0f, 1f);
            _playerStatusText.rectTransform.anchorMax = new Vector2(1f, 1f);
            _playerStatusText.rectTransform.pivot = new Vector2(0f, 1f);
            _playerStatusText.rectTransform.anchoredPosition = Vector2.zero;
            _playerStatusText.rectTransform.sizeDelta = new Vector2(0f, 30f);

            // HP Bar
            _playerHpFill = CreateFilledBar("PlayerHP", box, new Vector2(0f, -38f), new Vector2(360f, 22f), CrimsonAccent);
            // Posture Bar
            _playerPostureFill = CreateFilledBar("PlayerPosture", box, new Vector2(0f, -68f), new Vector2(280f, 14f), GoldAccent);
        }

        private void BuildTopCenterBossHud(RectTransform parent)
        {
            RectTransform box = CreatePanel("BossHud", parent, Color.clear);
            box.anchorMin = new Vector2(0.5f, 1f);
            box.anchorMax = new Vector2(0.5f, 1f);
            box.pivot = new Vector2(0.5f, 1f);
            box.anchoredPosition = new Vector2(0f, -24f);
            box.sizeDelta = new Vector2(720f, 126f);

            Text bossName = CreateText("BossName", box, "타락한 조선 대장군", 26, PaleWhite, TextAnchor.MiddleCenter);
            bossName.rectTransform.anchorMin = new Vector2(0.5f, 1f);
            bossName.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            bossName.rectTransform.pivot = new Vector2(0.5f, 1f);
            bossName.rectTransform.anchoredPosition = Vector2.zero;
            bossName.rectTransform.sizeDelta = new Vector2(520f, 34f);

            // Center-outward Boss Posture Gauge (GDD)
            _bossPostureMaxWidth = 540f;
            RectTransform postureBg = CreateRoundedBox("BossPostureBg", box, InkDark, new Vector2(_bossPostureMaxWidth, 16f));
            postureBg.anchorMin = new Vector2(0.5f, 1f);
            postureBg.anchorMax = new Vector2(0.5f, 1f);
            postureBg.pivot = new Vector2(0.5f, 1f);
            postureBg.anchoredPosition = new Vector2(0f, -40f);

            GameObject postureFillGo = new GameObject("BossPostureFill", typeof(RectTransform), typeof(Image));
            postureFillGo.transform.SetParent(postureBg, false);
            _bossPostureFillRect = postureFillGo.GetComponent<RectTransform>();
            _bossPostureFillRect.anchorMin = new Vector2(0.5f, 0.5f);
            _bossPostureFillRect.anchorMax = new Vector2(0.5f, 0.5f);
            _bossPostureFillRect.pivot = new Vector2(0.5f, 0.5f);
            _bossPostureFillRect.sizeDelta = new Vector2(0f, 12f);
            _bossPostureFillImage = postureFillGo.GetComponent<Image>();
            _bossPostureFillImage.sprite = _pillSprite;
            _bossPostureFillImage.type = Image.Type.Sliced;
            _bossPostureFillImage.color = GoldAccent;

            // Boss HP Gauge
            RectTransform hpHolder = CreatePanel("BossHpHolder", box, Color.clear);
            hpHolder.anchorMin = new Vector2(0.5f, 1f);
            hpHolder.anchorMax = new Vector2(0.5f, 1f);
            hpHolder.pivot = new Vector2(0.5f, 1f);
            hpHolder.anchoredPosition = new Vector2(0f, -66f);
            hpHolder.sizeDelta = new Vector2(640f, 22f);
            _bossHpFill = CreateFilledBar("BossHP", hpHolder, Vector2.zero, new Vector2(640f, 22f), CrimsonAccent);

            // Boss Lives (목숨 2줄: ● ●)
            _bossLifeOrbs = new Image[2];
            for (int i = 0; i < 2; i++)
            {
                GameObject orbGo = new GameObject($"BossLife_{i + 1}", typeof(RectTransform), typeof(Image));
                orbGo.transform.SetParent(box, false);
                RectTransform rt = orbGo.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(54f + i * 32f, -104f);
                rt.sizeDelta = new Vector2(20f, 20f);
                Image img = orbGo.GetComponent<Image>();
                img.sprite = _circleFillSprite;
                img.color = CrimsonAccent;
                _bossLifeOrbs[i] = img;
            }
        }

        private void BuildTopRightUtility(RectTransform parent)
        {
            // FPS indicator
            _fpsText = CreateText("FpsText", parent, "60 FPS", 18, MutedSlate, TextAnchor.MiddleRight);
            _fpsText.rectTransform.anchorMin = new Vector2(1f, 1f);
            _fpsText.rectTransform.anchorMax = new Vector2(1f, 1f);
            _fpsText.rectTransform.pivot = new Vector2(1f, 1f);
            _fpsText.rectTransform.anchoredPosition = new Vector2(-174f, -30f);
            _fpsText.rectTransform.sizeDelta = new Vector2(110f, 36f);

            // Restart Button
            RectTransform btnRect = CreateRoundedBox("RestartBtn", parent, InkNavy, new Vector2(126f, 48f), raycastTarget: true);
            btnRect.anchorMin = new Vector2(1f, 1f);
            btnRect.anchorMax = new Vector2(1f, 1f);
            btnRect.pivot = new Vector2(1f, 1f);
            btnRect.anchoredPosition = new Vector2(-34f, -24f);

            Text label = CreateText("RestartLabel", btnRect, "재시작", 22, PaleWhite, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);

            Button btn = btnRect.gameObject.AddComponent<Button>();
            btn.onClick.AddListener(() => _duel?.ResetDuel());
        }

        private void BuildCenterCombatCues(RectTransform parent)
        {
            // 殺 Danger Sign (uses crimson calligraphy artwork sprite if assigned, text fallback otherwise)
            if (dangerSalSprite != null)
            {
                GameObject salGo = new GameObject("DangerSign", typeof(RectTransform), typeof(Image));
                salGo.transform.SetParent(parent, false);
                _dangerSignRoot = salGo.GetComponent<RectTransform>();
                _dangerSignRoot.anchorMin = new Vector2(0.5f, 0.67f);
                _dangerSignRoot.anchorMax = new Vector2(0.5f, 0.67f);
                _dangerSignRoot.sizeDelta = new Vector2(250f, 250f);
                Image salImg = salGo.GetComponent<Image>();
                salImg.sprite = dangerSalSprite;
                salImg.preserveAspect = true;
                salImg.raycastTarget = false;
                _dangerSignRoot.gameObject.SetActive(false);
            }
            else
            {
                Text salTxt = CreateText("DangerSign", parent, "殺", 128, CrimsonAccent, TextAnchor.MiddleCenter);
                _dangerSignRoot = salTxt.rectTransform;
                _dangerSignRoot.anchorMin = new Vector2(0.5f, 0.68f);
                _dangerSignRoot.anchorMax = new Vector2(0.5f, 0.68f);
                _dangerSignRoot.sizeDelta = new Vector2(240f, 160f);
                _dangerSignRoot.gameObject.SetActive(false);
            }

            // 인살 Execution Seal Prompt
            _executionSignText = CreateText("ExecutionSign", parent, "인살", 72, CrimsonAccent, TextAnchor.MiddleCenter);
            _executionSignText.rectTransform.anchorMin = new Vector2(0.5f, 0.56f);
            _executionSignText.rectTransform.anchorMax = new Vector2(0.5f, 0.56f);
            _executionSignText.rectTransform.sizeDelta = new Vector2(320f, 110f);
            _executionSignText.gameObject.SetActive(false);

            // Combat Feedback (튕겨내기! / 간파! / 막기 / 가드 붕괴!)
            _feedbackText = CreateText("CombatFeedback", parent, "", 38, GoldAccent, TextAnchor.MiddleCenter);
            _feedbackText.rectTransform.anchorMin = new Vector2(0.5f, 0.44f);
            _feedbackText.rectTransform.anchorMax = new Vector2(0.5f, 0.44f);
            _feedbackText.rectTransform.sizeDelta = new Vector2(560f, 64f);
            _feedbackText.gameObject.SetActive(false);

            // Phase / Duel Intro Banner
            _bannerText = CreateText("DuelBanner", parent, "", 34, GoldAccent, TextAnchor.MiddleCenter);
            _bannerText.rectTransform.anchorMin = new Vector2(0.5f, 0.78f);
            _bannerText.rectTransform.anchorMax = new Vector2(0.5f, 0.78f);
            _bannerText.rectTransform.sizeDelta = new Vector2(760f, 54f);
            _bannerText.gameObject.SetActive(false);
        }

        private void BuildBottomLeftMovement(RectTransform parent)
        {
            HoldButton leftBtn = CreateCircleActionButton("BtnMoveLeft", parent,
                anchor: new Vector2(0f, 0f),
                pos: new Vector2(126f, 130f),
                diameter: 142f,
                label: "◀",
                fontSize: 42,
                ringColor: MutedSlate,
                out _, out _, out _);
            leftBtn.Pressed += () => _player?.SetMoveLeft(true);
            leftBtn.Released += () => _player?.SetMoveLeft(false);

            HoldButton rightBtn = CreateCircleActionButton("BtnMoveRight", parent,
                anchor: new Vector2(0f, 0f),
                pos: new Vector2(296f, 130f),
                diameter: 142f,
                label: "▶",
                fontSize: 42,
                ringColor: MutedSlate,
                out _, out _, out _);
            rightBtn.Pressed += () => _player?.SetMoveRight(true);
            rightBtn.Released += () => _player?.SetMoveRight(false);
        }

        private void BuildBottomRightActionButtons(RectTransform parent)
        {
            // Decision: Parry/Guard is the LARGEST primary button at ergonomic right-thumb spot
            HoldButton parryBtn = CreateCircleActionButton("BtnParryGuard", parent,
                anchor: new Vector2(1f, 0f),
                pos: new Vector2(-156f, 152f),
                diameter: 214f,
                label: "패링",
                fontSize: 28,
                ringColor: GoldAccent,
                out _, out _, out _,
                artSprite: parryBtnSprite);
            parryBtn.Pressed += () => _player?.OnParryDown();
            parryBtn.Released += () => _player?.OnParryUp();

            // Attack / Execution button adjacent to Parry
            HoldButton attackBtn = CreateCircleActionButton("BtnAttack", parent,
                anchor: new Vector2(1f, 0f),
                pos: new Vector2(-366f, 128f),
                diameter: 158f,
                label: "공격",
                fontSize: 26,
                ringColor: PaleWhite,
                out _attackBtnLabel, out _attackBtnRing, out _,
                artSprite: attackBtnSprite);
            attackBtn.Pressed += () => _player?.OnAttackDown();
            attackBtn.Released += () => _player?.OnAttackUp();

            // Special / Ganpa button above Parry
            HoldButton specialBtn = CreateCircleActionButton("BtnSpecialGanpa", parent,
                anchor: new Vector2(1f, 0f),
                pos: new Vector2(-148f, 356f),
                diameter: 140f,
                label: "간파",
                fontSize: 22,
                ringColor: PaleWhite,
                out _, out _specialBtnRing, out _specialCooldownOverlay,
                artSprite: ganpaBtnSprite);
            specialBtn.Pressed += () => _player?.OnSpecialDown();

            // Dodge / Leap button upper-left of Parry
            HoldButton dodgeBtn = CreateCircleActionButton("BtnDodgeLeap", parent,
                anchor: new Vector2(1f, 0f),
                pos: new Vector2(-334f, 314f),
                diameter: 136f,
                label: "회피",
                fontSize: 22,
                ringColor: MutedSlate,
                out _, out _, out _dodgeCooldownOverlay,
                artSprite: dodgeBtnSprite);
            dodgeBtn.Pressed += () => _player?.OnDodgeDown();
        }

        private void BuildEndOverlay(RectTransform parent)
        {
            RectTransform overlay = CreatePanel("EndOverlay", parent, new Color(0.04f, 0.04f, 0.06f, 0.80f));
            Stretch(overlay);
            // Generous bleed beyond Canvas edges so notched/curved ultrawide screens never leak a bright edge strip
            overlay.offsetMin = new Vector2(-260f, -260f);
            overlay.offsetMax = new Vector2(260f, 260f);
            _endOverlayRoot = overlay.gameObject;

            _endTitleText = CreateText("EndTitle", overlay, "", 60, GoldAccent, TextAnchor.MiddleCenter);
            _endTitleText.rectTransform.anchorMin = new Vector2(0.5f, 0.58f);
            _endTitleText.rectTransform.anchorMax = new Vector2(0.5f, 0.58f);
            _endTitleText.rectTransform.sizeDelta = new Vector2(820f, 96f);

            RectTransform retryBtnRect = CreateRoundedBox("RetryBtn", overlay, InkNavy, new Vector2(280f, 76f), raycastTarget: true);
            retryBtnRect.anchorMin = new Vector2(0.5f, 0.38f);
            retryBtnRect.anchorMax = new Vector2(0.5f, 0.38f);
            retryBtnRect.anchoredPosition = Vector2.zero;

            Text retryText = CreateText("RetryLabel", retryBtnRect, "다시 도전", 32, GoldAccent, TextAnchor.MiddleCenter);
            Stretch(retryText.rectTransform);

            Button btn = retryBtnRect.gameObject.AddComponent<Button>();
            btn.onClick.AddListener(() => _duel?.ResetDuel());

            _endOverlayRoot.SetActive(false);
        }

        // --- UI Factory Helpers ---

        private HoldButton CreateCircleActionButton(
            string name,
            RectTransform parent,
            Vector2 anchor,
            Vector2 pos,
            float diameter,
            string label,
            int fontSize,
            Color ringColor,
            out Text labelText,
            out Image ringImage,
            out Image cooldownOverlay,
            Sprite artSprite = null)
        {
            GameObject root = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(HoldButton));
            root.transform.SetParent(parent, false);
            RectTransform rt = root.GetComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(diameter, diameter);

            Image bg = root.GetComponent<Image>();
            bool hasArt = artSprite != null;
            bg.sprite = hasArt ? artSprite : _circleFillSprite;
            bg.color = hasArt ? Color.white : InkDark;

            // Crisp status highlight ring (subtle when art already has gold rim, brightens on 인살/마패강화)
            GameObject ringGo = new GameObject("Ring", typeof(RectTransform), typeof(Image));
            ringGo.transform.SetParent(rt, false);
            RectTransform ringRt = ringGo.GetComponent<RectTransform>();
            Stretch(ringRt);
            ringImage = ringGo.GetComponent<Image>();
            ringImage.sprite = _circleRingSprite;
            ringImage.color = hasArt ? new Color(ringColor.r, ringColor.g, ringColor.b, 0.35f) : ringColor;
            ringImage.raycastTarget = false;

            // Radial cooldown sweep
            GameObject cdGo = new GameObject("Cooldown", typeof(RectTransform), typeof(Image));
            cdGo.transform.SetParent(rt, false);
            RectTransform cdRt = cdGo.GetComponent<RectTransform>();
            Stretch(cdRt);
            cooldownOverlay = cdGo.GetComponent<Image>();
            cooldownOverlay.sprite = _circleFillSprite;
            cooldownOverlay.color = new Color(0f, 0f, 0f, 0.55f);
            cooldownOverlay.type = Image.Type.Filled;
            cooldownOverlay.fillMethod = Image.FillMethod.Radial360;
            cooldownOverlay.fillAmount = 0f;
            cooldownOverlay.raycastTarget = false;

            labelText = CreateText("Label", rt, label, fontSize, PaleWhite, hasArt ? TextAnchor.LowerCenter : TextAnchor.MiddleCenter);
            Stretch(labelText.rectTransform);
            if (hasArt)
            {
                labelText.rectTransform.offsetMin = new Vector2(0f, -26f);
                labelText.rectTransform.offsetMax = Vector2.zero;
            }
            labelText.raycastTarget = false;

            return root.GetComponent<HoldButton>();
        }

        private Image CreateFilledBar(string name, RectTransform parent, Vector2 anchoredPos, Vector2 size, Color fillColor)
        {
            RectTransform bg = CreateRoundedBox($"{name}_Bg", parent, InkDark, size);
            bg.anchorMin = new Vector2(0f, 1f);
            bg.anchorMax = new Vector2(0f, 1f);
            bg.pivot = new Vector2(0f, 1f);
            bg.anchoredPosition = anchoredPos;

            GameObject fillGo = new GameObject($"{name}_Fill", typeof(RectTransform), typeof(Image));
            fillGo.transform.SetParent(bg, false);
            RectTransform fillRt = fillGo.GetComponent<RectTransform>();
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = new Vector2(2f, 2f);
            fillRt.offsetMax = new Vector2(-2f, -2f);

            Image fill = fillGo.GetComponent<Image>();
            fill.sprite = _pillSprite;
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = 0;
            fill.fillAmount = 1f;
            fill.color = fillColor;
            fill.raycastTarget = false;
            return fill;
        }

        private RectTransform CreateRoundedBox(string name, RectTransform parent, Color color, Vector2 size, bool raycastTarget = false)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = size;
            Image img = go.GetComponent<Image>();
            img.sprite = _pillSprite;
            img.type = Image.Type.Sliced;
            img.color = color;
            img.raycastTarget = raycastTarget;
            return rt;
        }

        private RectTransform CreatePanel(string name, RectTransform parent, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            if (color.a > 0f)
            {
                Image img = go.AddComponent<Image>();
                img.color = color;
            }
            return go.GetComponent<RectTransform>();
        }

        private Text CreateText(string name, RectTransform parent, string content, int fontSize, Color color, TextAnchor align)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(Shadow));
            go.transform.SetParent(parent, false);
            Text txt = go.GetComponent<Text>();
            txt.font = uiFont;
            txt.text = content;
            txt.fontSize = fontSize;
            txt.color = color;
            txt.alignment = align;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;

            Shadow shadow = go.GetComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
            shadow.effectDistance = new Vector2(2f, -2f);
            return txt;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void EnsureSprites()
        {
            if (_circleFillSprite == null) _circleFillSprite = BuildCircleSprite(128, ringOnly: false);
            if (_circleRingSprite == null) _circleRingSprite = BuildCircleSprite(128, ringOnly: true);
            if (_pillSprite == null) _pillSprite = BuildPillSprite(32);
        }

        private static Sprite BuildCircleSprite(int size, bool ringOnly)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float r = (size - 2) * 0.5f;
            float center = (size - 1) * 0.5f;
            float innerR = r - 4.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float outerAlpha = Mathf.Clamp01(r - dist + 0.75f);
                    float alpha = outerAlpha;
                    if (ringOnly)
                    {
                        float innerAlpha = Mathf.Clamp01(dist - innerR + 0.75f);
                        alpha = outerAlpha * innerAlpha;
                    }
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite BuildPillSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float radius = 6f;
            float min = radius;
            float max = size - 1 - radius;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float cx = Mathf.Clamp(x, min, max);
                    float cy = Mathf.Clamp(y, min, max);
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    float a = Mathf.Clamp01(radius - d + 0.75f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(8, 8, 8, 8));
        }
    }
}
