using UnityEngine;

namespace EosaBlade
{
    /// <summary>
    /// Handles hitstop (normal 0.03s / parry 0.06s / execution 0.12s slow-mo),
    /// 2.5D camera framing + shake + zoom punch, pooled CC0 SFX playback,
    /// pooled spark VFX, and short Android haptic feedback. Zero per-frame GC.
    /// </summary>
    public class CombatJuiceController : MonoBehaviour
    {
        [Header("Camera Framing")]
        [SerializeField] private Camera targetCamera;
        [SerializeField] private Transform playerTransform;
        [SerializeField] private Transform bossTransform;
        [SerializeField] private Transform backdropTransform;
        [SerializeField] private Vector3 baseCameraOffset = new Vector3(0f, 1.95f, -7.8f);
        [SerializeField] private Vector3 lookOffset = new Vector3(0f, 1.15f, 0f);
        [SerializeField] private float baseFov = 36f;
        [SerializeField] private float cameraFollowSmooth = 7f;

        [Header("VFX Materials (Pre-baked URP Particle Assets)")]
        [SerializeField] private Material sparkParticleMaterial;
        [SerializeField] private Material clashRingMaterial;

        [Header("CC0 Sound Effects")]
        [SerializeField] private AudioClip[] parryClips;
        [SerializeField] private AudioClip guardClip;
        [SerializeField] private AudioClip[] slashClips;
        [SerializeField] private AudioClip hitClip;
        [SerializeField] private AudioClip dangerGongClip;
        [SerializeField] private AudioClip ganpaClip;
        [SerializeField] private AudioClip guardBreakClip;
        [SerializeField] private AudioClip executionClip;
        [SerializeField] private AudioClip slamClip;

        // Time-scale / Hitstop state (driven by unscaledDeltaTime)
        private float _hitstopRemaining;
        private float _activeTimeScale = 1f;

        // Camera juice
        private float _shakeTrauma;
        private float _shakeSeed;
        private float _zoomPunch;      // positive zooms FOV in
        private float _targetZoomHold; // sustained zoom during execution
        private Vector3 _smoothedFocus;
        private bool _cameraInitialized;

        // Pooled AudioSources (no PlayClipAtPoint allocations)
        private AudioSource[] _sfxPool;
        private int _sfxCursor;
        private const int SfxPoolSize = 8;

        // Pooled ParticleSystems
        private ParticleSystem _goldParryParticles;
        private ParticleSystem _silverGuardParticles;
        private ParticleSystem _crimsonImpactParticles;
        private ParticleSystem _clashRingParticles;

        private readonly ParticleSystem.EmitParams _emitParams = new ParticleSystem.EmitParams();

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject _vibrator;
        private AndroidJavaClass _vibrationEffectClass;
        private int _androidApiLevel;
#endif

        public void BindReferences(Camera cam, Transform player, Transform boss, Material sparkMat = null, Transform backdrop = null, Material ringMat = null)
        {
            targetCamera = cam;
            playerTransform = player;
            bossTransform = boss;
            if (sparkMat != null) sparkParticleMaterial = sparkMat;
            if (backdrop != null) backdropTransform = backdrop;
            if (ringMat != null) clashRingMaterial = ringMat;
        }

        public void BindClips(
            AudioClip[] parries,
            AudioClip guard,
            AudioClip[] slashes,
            AudioClip hit,
            AudioClip dangerGong,
            AudioClip ganpa,
            AudioClip guardBreak,
            AudioClip execution,
            AudioClip slam)
        {
            parryClips = parries;
            guardClip = guard;
            slashClips = slashes;
            hitClip = hit;
            dangerGongClip = dangerGong;
            ganpaClip = ganpa;
            guardBreakClip = guardBreak;
            executionClip = execution;
            slamClip = slam;
        }

        private void Awake()
        {
            _shakeSeed = Random.Range(1f, 99f);
            InitAudioPool();
            InitParticlePools();
            InitAndroidHaptics();
        }

        private void OnDisable()
        {
            Time.timeScale = 1f;
            _hitstopRemaining = 0f;
            _activeTimeScale = 1f;
        }

        public void ResetJuice()
        {
            Time.timeScale = 1f;
            _hitstopRemaining = 0f;
            _activeTimeScale = 1f;
            _shakeTrauma = 0f;
            _zoomPunch = 0f;
            _targetZoomHold = 0f;
            _cameraInitialized = false;
        }

        private void Update()
        {
            float unscaledDt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);

            // Hitstop / slow-motion countdown in real time
            if (_hitstopRemaining > 0f)
            {
                _hitstopRemaining -= unscaledDt;
                if (_hitstopRemaining <= 0f)
                {
                    _hitstopRemaining = 0f;
                    _activeTimeScale = 1f;
                    Time.timeScale = 1f;
                    _targetZoomHold = 0f;
                }
                else
                {
                    Time.timeScale = _activeTimeScale;
                }
            }

            UpdateCamera(unscaledDt);
        }

        private void UpdateCamera(float dt)
        {
            if (targetCamera == null || playerTransform == null || bossTransform == null) return;

            Vector3 mid = (playerTransform.position + bossTransform.position) * 0.5f;
            float dist = Mathf.Abs(playerTransform.position.x - bossTransform.position.x);
            float dynamicZ = baseCameraOffset.z - Mathf.Clamp((dist - 3.6f) * 0.45f, 0f, 2.2f);
            Vector3 desiredFocus = new Vector3(Mathf.Clamp(mid.x, -4.2f, 4.2f), 0f, 0f);

            if (!_cameraInitialized)
            {
                _smoothedFocus = desiredFocus;
                _cameraInitialized = true;
            }
            else
            {
                _smoothedFocus = Vector3.Lerp(_smoothedFocus, desiredFocus, 1f - Mathf.Exp(-cameraFollowSmooth * dt));
            }

            // Decay shake & zoom punch
            _shakeTrauma = Mathf.Max(0f, _shakeTrauma - dt * 2.8f);
            _zoomPunch = Mathf.Lerp(_zoomPunch, _targetZoomHold, 1f - Mathf.Exp(-12f * dt));

            float shakeMag = _shakeTrauma * _shakeTrauma;
            float t = Time.unscaledTime * 38f;
            float sx = (Mathf.PerlinNoise(_shakeSeed, t) - 0.5f) * 0.55f * shakeMag;
            float sy = (Mathf.PerlinNoise(_shakeSeed + 17.3f, t) - 0.5f) * 0.42f * shakeMag;

            Vector3 camPos = _smoothedFocus + new Vector3(baseCameraOffset.x + sx, baseCameraOffset.y + sy, dynamicZ);
            targetCamera.transform.position = camPos;
            targetCamera.transform.LookAt(_smoothedFocus + lookOffset);

            targetCamera.fieldOfView = Mathf.Clamp(baseFov - _zoomPunch, 24f, 44f);

            // Subtle parallax tracking on backdrop so 20:9 / 21:9 ultrawide phones NEVER show black side gaps
            if (backdropTransform != null)
            {
                Vector3 bp = backdropTransform.position;
                bp.x = _smoothedFocus.x * 0.72f;
                backdropTransform.position = bp;
            }
        }

        // --- Public Combat Feedback Triggers ---

        public void TriggerNormalHit(Vector3 worldPos)
        {
            StartHitstop(0.03f, 0.04f);
            AddShake(0.28f);
            PlayOneShot(hitClip, 0.85f, Random.Range(0.95f, 1.05f));
            EmitParticles(_crimsonImpactParticles, worldPos, 14);
            EmitClashRing(worldPos, new Color(0.95f, 0.25f, 0.18f, 0.80f), 0.90f);
            TriggerHaptic(14, 110);
        }

        public void TriggerPlayerSwordHit(Vector3 worldPos, bool heavy)
        {
            // Meaty blade-bite hitstop while the Hero's sword is physically buried inside the Boss's torso!
            StartHitstop(heavy ? 0.088f : 0.062f, 0.02f);
            AddShake(heavy ? 0.52f : 0.36f);
            _zoomPunch = Mathf.Max(_zoomPunch, baseFov * (heavy ? 0.045f : 0.024f));
            // Layered crisp blade cut + body/armor impact for unmistakable hitting satisfaction
            PlayOneShot(hitClip, heavy ? 1.0f : 0.92f, heavy ? 0.86f : Random.Range(0.96f, 1.06f));
            PlayRandomClip(slashClips, heavy ? 0.90f : 0.72f, heavy ? 0.88f : 1.12f);
            EmitParticles(_crimsonImpactParticles, worldPos, heavy ? 30 : 20);
            EmitParticles(_goldParryParticles, worldPos, heavy ? 12 : 6);
            EmitClashRing(worldPos, heavy ? new Color(1.0f, 0.58f, 0.14f, 0.95f) : new Color(0.98f, 0.82f, 0.68f, 0.85f), heavy ? 1.35f : 0.95f);
            TriggerHaptic(heavy ? 32 : 20, heavy ? 210 : 145);
        }

        public void TriggerPerfectParry(Vector3 worldPos, int consecutiveCount)
        {
            // Crisp 0.075s blade-lock hitstop right at the exact frame the two swords cross!
            StartHitstop(0.075f, 0.015f);
            AddShake(0.56f);
            _zoomPunch = Mathf.Max(_zoomPunch, baseFov * 0.045f);

            float pitch = Mathf.Min(1.22f, 0.98f + (consecutiveCount - 1) * 0.045f);
            PlayRandomClip(parryClips, 1.0f, pitch);
            EmitParticles(_goldParryParticles, worldPos, 28);
            EmitClashRing(worldPos, new Color(1.0f, 0.84f, 0.22f, 1f), 1.65f);
            TriggerHaptic(24, 180);
        }

        public void TriggerGuardBlock(Vector3 worldPos)
        {
            StartHitstop(0.02f, 0.15f);
            AddShake(0.22f);
            PlayOneShot(guardClip, 0.82f, Random.Range(0.84f, 0.92f));
            EmitParticles(_silverGuardParticles, worldPos, 12);
            EmitClashRing(worldPos, new Color(0.82f, 0.88f, 0.95f, 0.70f), 0.95f);
            TriggerHaptic(14, 95);
        }

        public void TriggerGuardBreak(Vector3 worldPos)
        {
            StartHitstop(0.07f, 0.05f);
            AddShake(0.70f);
            PlayOneShot(guardBreakClip, 0.95f, 0.92f);
            EmitParticles(_crimsonImpactParticles, worldPos, 22);
            EmitClashRing(worldPos, new Color(0.95f, 0.22f, 0.14f, 0.95f), 2.10f);
            TriggerHaptic(45, 220);
        }

        public void TriggerGanpaSuccess(Vector3 worldPos)
        {
            StartHitstop(0.08f, 0.03f);
            AddShake(0.68f);
            _zoomPunch = Mathf.Max(_zoomPunch, baseFov * 0.065f);
            PlayOneShot(ganpaClip, 1.0f, 1.05f);
            PlayRandomClip(parryClips, 0.8f, 1.12f);
            EmitParticles(_goldParryParticles, worldPos, 36);
            EmitClashRing(worldPos, new Color(1.0f, 0.78f, 0.10f, 1f), 2.35f);
            TriggerHaptic(36, 220);
        }

        public void TriggerDangerTelegraph()
        {
            PlayOneShot(dangerGongClip, 0.95f, 1.0f);
            TriggerHaptic(22, 160);
        }

        public void TriggerHeavySlamImpact(Vector3 worldPos)
        {
            AddShake(0.55f);
            PlayOneShot(slamClip, 0.9f, 0.95f);
            EmitParticles(_silverGuardParticles, worldPos, 18);
            EmitClashRing(worldPos + Vector3.up * 0.2f, new Color(0.95f, 0.55f, 0.15f, 0.90f), 1.80f);
        }

        public void TriggerExecution(Vector3 worldPos)
        {
            // GDD: 0.12s slow-motion + dramatic zoom + crimson burst + gong/slash
            StartHitstop(0.12f, 0.22f);
            _targetZoomHold = baseFov * 0.14f;
            _zoomPunch = baseFov * 0.16f;
            AddShake(0.88f);
            PlayOneShot(executionClip, 1.0f, 0.95f);
            PlayRandomClip(slashClips, 0.95f, 0.82f);
            EmitParticles(_crimsonImpactParticles, worldPos, 48);
            EmitParticles(_goldParryParticles, worldPos, 20);
            EmitClashRing(worldPos, new Color(0.95f, 0.12f, 0.12f, 1f), 2.85f);
            TriggerHaptic(60, 255);
        }

        public void TriggerSlashSwing(bool heavy = false)
        {
            PlayRandomClip(slashClips, heavy ? 0.85f : 0.65f, heavy ? 0.82f : Random.Range(0.96f, 1.08f));
        }

        private void StartHitstop(float realSeconds, float slowScale)
        {
            if (realSeconds <= 0f) return;
            if (realSeconds >= _hitstopRemaining)
            {
                _hitstopRemaining = realSeconds;
                _activeTimeScale = slowScale;
                Time.timeScale = slowScale;
            }
        }

        private void AddShake(float amount)
        {
            _shakeTrauma = Mathf.Clamp01(_shakeTrauma + amount);
        }

        private void InitAudioPool()
        {
            _sfxPool = new AudioSource[SfxPoolSize];
            for (int i = 0; i < SfxPoolSize; i++)
            {
                var go = new GameObject($"SFX_Voice_{i}");
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 0f; // Crisp 2D feedback for mobile parry rhythm
                _sfxPool[i] = src;
            }
        }

        private void PlayRandomClip(AudioClip[] clips, float volume, float pitch)
        {
            if (clips == null || clips.Length == 0) return;
            AudioClip clip = clips[Random.Range(0, clips.Length)];
            PlayOneShot(clip, volume, pitch);
        }

        private void PlayOneShot(AudioClip clip, float volume, float pitch)
        {
            if (clip == null || _sfxPool == null) return;
            AudioSource src = _sfxPool[_sfxCursor];
            _sfxCursor = (_sfxCursor + 1) % _sfxPool.Length;
            src.pitch = pitch;
            src.volume = volume;
            src.PlayOneShot(clip);
        }

        private void InitParticlePools()
        {
            _goldParryParticles = CreateSparkSystem("VFX_GoldParry", new Color(1.0f, 0.88f, 0.28f, 1f), new Color(1f, 0.98f, 0.85f, 1f), 5.5f, 11.0f, 0.22f, 0.14f);
            _silverGuardParticles = CreateSparkSystem("VFX_SilverGuard", new Color(0.88f, 0.93f, 0.98f, 1f), new Color(0.68f, 0.76f, 0.86f, 1f), 3.8f, 7.0f, 0.18f, 0.11f);
            _crimsonImpactParticles = CreateSparkSystem("VFX_CrimsonImpact", new Color(0.98f, 0.22f, 0.16f, 1f), new Color(0.75f, 0.08f, 0.08f, 1f), 4.5f, 9.0f, 0.22f, 0.15f);
            _clashRingParticles = CreateClashRingSystem("VFX_ClashRing");
        }

        private ParticleSystem CreateClashRingSystem(string objName)
        {
            var go = new GameObject(objName);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = 0.19f;
            main.startSpeed = 0f;
            main.startSize = 1.6f;
            main.maxParticles = 16;

            var emission = ps.emission;
            emission.enabled = false;

            var shape = ps.shape;
            shape.enabled = false;

            // Rapid punchy expansion from tight flash -> wide Sekiro parry ring
            var sizeOverLife = ps.sizeOverLifetime;
            sizeOverLife.enabled = true;
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.18f),
                new Keyframe(0.35f, 0.82f),
                new Keyframe(1f, 1.15f)));

            var colOverLife = ps.colorOverLifetime;
            colOverLife.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.85f, 0.35f), new GradientAlphaKey(0f, 1f) });
            colOverLife.color = new ParticleSystem.MinMaxGradient(g);

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            if (clashRingMaterial != null) renderer.sharedMaterial = clashRingMaterial;
            else if (sparkParticleMaterial != null) renderer.sharedMaterial = sparkParticleMaterial;
            return ps;
        }

        private void EmitClashRing(Vector3 worldPos, Color color, float size)
        {
            if (_clashRingParticles == null) return;
            // Keep clash ring elevated at blade height and slightly toward camera so it NEVER clips into the floor or torso
            worldPos.y = Mathf.Max(worldPos.y, 1.35f);
            worldPos.z = -0.35f;
            var ep = new ParticleSystem.EmitParams
            {
                position = worldPos,
                startColor = color,
                startSize = size * 0.62f,
                rotation = Random.Range(0f, 360f)
            };
            _clashRingParticles.Emit(ep, 1);
        }

        private ParticleSystem CreateSparkSystem(string objName, Color c1, Color c2, float minSpd, float maxSpd, float life, float size)
        {
            var go = new GameObject(objName);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.65f, life);
            main.startSpeed = new ParticleSystem.MinMaxCurve(minSpd, maxSpd);
            main.startSize = new ParticleSystem.MinMaxCurve(size * 0.55f, size);
            main.startColor = new ParticleSystem.MinMaxGradient(c1, c2);
            main.gravityModifier = 0.85f;
            main.maxParticles = 96;

            var emission = ps.emission;
            emission.enabled = false;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.15f;

            var sizeOverLife = ps.sizeOverLifetime;
            sizeOverLife.enabled = true;
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));

            var colOverLife = ps.colorOverLifetime;
            colOverLife.enabled = true;
            Gradient fadeGrad = new Gradient();
            fadeGrad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.85f, 0.45f), new GradientAlphaKey(0f, 1f) });
            colOverLife.color = new ParticleSystem.MinMaxGradient(fadeGrad);

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.09f;
            renderer.lengthScale = 2.4f;

            if (sparkParticleMaterial != null)
            {
                renderer.sharedMaterial = sparkParticleMaterial;
            }
            else
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    var mat = new Material(shader);
                    Texture2D tex = CreateSoftRadialTexture();
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

            return ps;
        }

        private static Texture2D _sharedSparkTex;
        private static Texture2D CreateSoftRadialTexture()
        {
            if (_sharedSparkTex != null) return _sharedSparkTex;
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
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a;
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply(false, true);
            _sharedSparkTex = tex;
            return _sharedSparkTex;
        }

        private void EmitParticles(ParticleSystem ps, Vector3 worldPos, int count)
        {
            if (ps == null) return;
            worldPos.y = Mathf.Max(worldPos.y, 1.25f);
            worldPos.z = -0.30f;
            ps.transform.position = worldPos;
            ps.Emit(count);
        }

        private void InitAndroidHaptics()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Reference Handheld.Vibrate so Unity auto-injects android.permission.VIBRATE into AndroidManifest
            if (Time.frameCount < 0) Handheld.Vibrate();
            try
            {
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    _androidApiLevel = version.GetStatic<int>("SDK_INT");
                }
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    _vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                }
                if (_androidApiLevel >= 26)
                {
                    _vibrationEffectClass = new AndroidJavaClass("android.os.VibrationEffect");
                }
            }
            catch
            {
                _vibrator = null;
            }
#endif
        }

        private void TriggerHaptic(long milliseconds, int amplitude)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_vibrator == null) return;
            try
            {
                if (_androidApiLevel >= 26 && _vibrationEffectClass != null)
                {
                    using (var effect = _vibrationEffectClass.CallStatic<AndroidJavaObject>(
                        "createOneShot", milliseconds, Mathf.Clamp(amplitude, 1, 255)))
                    {
                        _vibrator.Call("vibrate", effect);
                    }
                }
                else
                {
                    _vibrator.Call("vibrate", milliseconds);
                }
            }
            catch
            {
                // Ignore if device lacks vibrator permissions
            }
#endif
        }
    }
}
