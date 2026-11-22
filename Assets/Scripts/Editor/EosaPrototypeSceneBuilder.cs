#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace EosaBlade.Editor
{
    /// <summary>
    /// Configures Unity 6 URP project settings (Android target, Landscape Left/Right, 60fps mobile),
    /// configures Mixamo Humanoid rigs & animations, imports 2D UI/Background art,
    /// and builds the full Step 2 Art CombatPrototype.unity scene.
    /// </summary>
    public static class EosaPrototypeSceneBuilder
    {
        private const string ScenePath = "Assets/Scenes/CombatPrototype.unity";
        private const string MaterialsDir = "Assets/Materials";

        private const string HeroFbxPath = "Assets/Art/Characters/Hero/hero_eosa.fbx";
        private const string HeroTexPath = "Assets/Art/Characters/Hero/hero_eosa_albedo.png";
        private const string BossFbxPath = "Assets/Art/Characters/Boss_General/boss_general.fbx";
        private const string BossTexPath = "Assets/Art/Characters/Boss_General/boss_general_albedo.png";

        private const string HwandoObjPath = "Assets/Art/Weapons/weapon_hwando.obj";
        private const string HwandoTexPath = "Assets/Art/Weapons/weapon_hwando_albedo.png";
        private const string MapaeObjPath = "Assets/Art/Weapons/weapon_mapae.obj";
        private const string MapaeTexPath = "Assets/Art/Weapons/weapon_mapae_albedo.png";

        private const string BgTemplePath = "Assets/Art/Backgrounds/bg_mountain_temple.png";
        private const string FloorTexPath = "Assets/Art/Backgrounds/tex_courtyard_floor.png";
        private const string BtnParryPath = "Assets/Art/UI/btn_parry.png";
        private const string BtnAttackPath = "Assets/Art/UI/btn_attack.png";
        private const string BtnGanpaPath = "Assets/Art/UI/btn_ganpa.png";
        private const string BtnDodgePath = "Assets/Art/UI/btn_dodge.png";
        private const string DangerSalPath = "Assets/Art/UI/ui_danger_sal.png";
        private const string IconFirePath = "Assets/Art/UI/icon_fire_talisman.png";
        private const string SparkTexPath = "Assets/Art/VFX/tex_spark_streak.png";
        private const string ClashRingTexPath = "Assets/Art/VFX/tex_clash_ring.png";
        private const string SlashTrailTexPath = "Assets/Art/VFX/tex_slash_trail.png";

        private const string AnimDir = "Assets/Art/Animations";
        private const string AnimControllerPath = "Assets/Art/Animations/CombatantAnimator.controller";

        [MenuItem("EosaBlade/1. Configure Project & Build Combat Prototype Scene")]
        public static void BuildAndConfigureProject()
        {
            ConfigureProjectSettings();
            ConfigureArtImporters();
            RuntimeAnimatorController animCtrl = BuildCombatantAnimatorController();
            BuildCombatPrototypeScene(animCtrl);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            CaptureDiagnosticScreenshots();
            Debug.Log("[EosaBlade] Step 2 Full Art CombatPrototype.unity built and visually verified.");
        }

        [MenuItem("EosaBlade/3. Rebuild Scene & Build Android Lite APK")]
        public static void RebuildSceneAndBuildAndroidLite()
        {
            BuildAndConfigureProject();
            BuildScript.AndroidLite();
        }

        private static void ConfigureProjectSettings()
        {
            PlayerSettings.companyName = "KoonKim";
            PlayerSettings.productName = "어사검";
            PlayerSettings.bundleVersion = "0.2.5";

            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.koonkim.eosablade");
            PlayerSettings.Android.bundleVersionCode = 7;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);

            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;

            QualitySettings.vSyncCount = 0;

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            }
        }

        private static void ConfigureArtImporters()
        {
            // 1. Textures & UI Sprites (mobile-optimized sizes)
            ConfigureTexture(HeroTexPath, asSprite: false, maxSize: 1024);
            ConfigureTexture(BossTexPath, asSprite: false, maxSize: 1024);
            ConfigureTexture(HwandoTexPath, asSprite: false, maxSize: 512);
            ConfigureTexture(MapaeTexPath, asSprite: false, maxSize: 512);
            ConfigureTexture(BgTemplePath, asSprite: false, maxSize: 2048);
            ConfigureTexture(FloorTexPath, asSprite: false, maxSize: 512);

            ConfigureTexture(BtnParryPath, asSprite: true, maxSize: 256);
            ConfigureTexture(BtnAttackPath, asSprite: true, maxSize: 256);
            ConfigureTexture(BtnGanpaPath, asSprite: true, maxSize: 256);
            ConfigureTexture(BtnDodgePath, asSprite: true, maxSize: 256);
            ConfigureTexture(DangerSalPath, asSprite: true, maxSize: 512);
            ConfigureTexture(IconFirePath, asSprite: true, maxSize: 256);
            ConfigureTexture(SparkTexPath, asSprite: false, maxSize: 128);
            ConfigureTexture(ClashRingTexPath, asSprite: false, maxSize: 256);
            ConfigureTexture(SlashTrailTexPath, asSprite: false, maxSize: 256);

            // 1b. Optimize all short combat SFX for zero parry-frame decode hitch (DecompressOnLoad + Mono + ADPCM)
            ConfigureCombatAudioClips();

            // 2. Character FBXs -> Humanoid with own Avatar
            ConfigureCharacterFbx(HeroFbxPath);
            ConfigureCharacterFbx(BossFbxPath);

            // 3. Weapon OBJs -> static mesh (no rig/animation)
            ConfigureStaticWeaponMesh(HwandoObjPath);
            ConfigureStaticWeaponMesh(MapaeObjPath);

            // 4. Shared Mixamo Animation Clips -> Humanoid retargeted from Hero Avatar, root locked on XZ/Y
            Avatar heroAvatar = AssetDatabase.LoadAssetAtPath<Avatar>(HeroFbxPath);
            if (heroAvatar == null)
            {
                Debug.LogWarning("[EosaBlade] Hero Avatar not found at " + HeroFbxPath);
                return;
            }

            (string file, bool loop)[] anims =
            {
                ("anim_idle.fbx", true),
                ("anim_run.fbx", true),
                ("anim_block_idle.fbx", true),
                ("anim_parry.fbx", false),
                ("anim_slash.fbx", false),
                ("anim_heavy_slash.fbx", false),
                ("anim_spin_slash.fbx", false),
                ("anim_thrust.fbx", false),
                ("anim_impact.fbx", false),
                ("anim_jump.fbx", false),
                ("anim_powerup.fbx", false),
                ("anim_death.fbx", false),
            };

            foreach (var (file, loop) in anims)
            {
                string path = $"{AnimDir}/{file}";
                ConfigureAnimationFbx(path, heroAvatar, loop);
            }
        }

        private static void ConfigureTexture(string path, bool asSprite, int maxSize)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return;
            ti.textureType = asSprite ? TextureImporterType.Sprite : TextureImporterType.Default;
            if (asSprite) ti.spriteImportMode = SpriteImportMode.Single;
            ti.alphaIsTransparency = asSprite;
            ti.mipmapEnabled = !asSprite;
            ti.isReadable = false;
            ti.maxTextureSize = maxSize;
            ti.textureCompression = TextureImporterCompression.Compressed;

            // Explicit Android ASTC 6x6 hardware compression (6x less VRAM & GPU bandwidth -> lower heat)
            var androidSettings = new TextureImporterPlatformSettings
            {
                name = "Android",
                overridden = true,
                maxTextureSize = maxSize,
                format = TextureImporterFormat.ASTC_6x6,
                compressionQuality = 50
            };
            ti.SetPlatformTextureSettings(androidSettings);
            ti.SaveAndReimport();
        }

        private static void ConfigureCombatAudioClips()
        {
            string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/Audio/SFX" });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var ai = AssetImporter.GetAtPath(path) as AudioImporter;
                if (ai == null) continue;
                ai.forceToMono = true;
                ai.loadInBackground = false;
                var settings = ai.defaultSampleSettings;
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
                settings.compressionFormat = AudioCompressionFormat.ADPCM;
                settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
                ai.defaultSampleSettings = settings;
                ai.SaveAndReimport();
            }
        }

        private static void ConfigureCharacterFbx(string path)
        {
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null) return;
            mi.animationType = ModelImporterAnimationType.Human;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.importAnimation = false;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            // Mixamo exported 1.78m/2.18m coords with UnitScaleFactor=1.0 (cm).
            // Using useFileScale=true + globalScale=100 scales Mesh, Skeleton, Bindposes AND Humanoid humanScale cleanly to 1.0x meters!
            mi.useFileScale = true;
            mi.globalScale = 100f;
            mi.isReadable = false;
            mi.meshCompression = ModelImporterMeshCompression.Medium;
            mi.optimizeMeshPolygons = true;
            mi.optimizeMeshVertices = true;
            mi.SaveAndReimport();
        }

        private static void ConfigureStaticWeaponMesh(string path)
        {
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null) return;
            mi.animationType = ModelImporterAnimationType.None;
            mi.importAnimation = false;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.globalScale = 1f;
            mi.isReadable = false;
            mi.meshCompression = ModelImporterMeshCompression.Medium;
            mi.optimizeMeshPolygons = true;
            mi.optimizeMeshVertices = true;
            mi.SaveAndReimport();
        }

        private static void ConfigureAnimationFbx(string path, Avatar sourceAvatar, bool loop)
        {
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null) return;

            mi.animationType = ModelImporterAnimationType.Human;
            mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            mi.sourceAvatar = sourceAvatar;
            mi.importAnimation = true;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.useFileScale = true;
            mi.globalScale = 100f;

            ModelImporterClipAnimation[] clips = mi.defaultClipAnimations;
            if (clips != null && clips.Length > 0)
            {
                for (int i = 0; i < clips.Length; i++)
                {
                    clips[i].loopTime = loop;
                    clips[i].lockRootRotation = true;
                    clips[i].keepOriginalOrientation = true; // Keep true 3/4 camera-facing chest orientation (never turn back to camera!)
                    clips[i].lockRootHeightY = true;
                    clips[i].heightFromFeet = true;          // Lock feet soles squarely onto y=0 (NEVER sink hips to y=0!)
                    clips[i].keepOriginalPositionY = false;
                    clips[i].lockRootPositionXZ = true;      // Bake XZ into pose
                    clips[i].keepOriginalPositionXZ = false; // Base Upon: Center of Mass -> 100% IN-PLACE (zero forward run-through or teleport!)
                }
                mi.clipAnimations = clips;
            }
            mi.SaveAndReimport();
        }

        private static RuntimeAnimatorController BuildCombatantAnimatorController()
        {
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(AnimControllerPath) != null)
            {
                AssetDatabase.DeleteAsset(AnimControllerPath);
            }

            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(AnimControllerPath);
            AnimatorStateMachine sm = controller.layers[0].stateMachine;

            (string stateName, string fbxFile)[] states =
            {
                ("Idle", "anim_idle.fbx"),
                ("Run", "anim_run.fbx"),
                ("BlockIdle", "anim_block_idle.fbx"),
                ("Parry", "anim_parry.fbx"),
                ("Slash", "anim_slash.fbx"),
                ("HeavySlash", "anim_heavy_slash.fbx"),
                ("SpinSlash", "anim_spin_slash.fbx"),
                ("Thrust", "anim_thrust.fbx"),
                ("Impact", "anim_impact.fbx"),
                ("Jump", "anim_jump.fbx"),
                ("PowerUp", "anim_powerup.fbx"),
                ("Death", "anim_death.fbx"),
            };

            AnimatorState idleState = null;
            for (int i = 0; i < states.Length; i++)
            {
                string clipPath = $"{AnimDir}/{states[i].fbxFile}";
                AnimationClip clip = LoadFirstClip(clipPath);
                AnimatorState st = sm.AddState(states[i].stateName, new Vector3(260f, i * 56f, 0f));
                st.motion = clip;
                st.writeDefaultValues = true;
                if (i == 0) idleState = st;
            }

            if (idleState != null) sm.defaultState = idleState;
            EditorUtility.SetDirty(controller);
            return controller;
        }

        private static AnimationClip LoadFirstClip(string fbxPath)
        {
            Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
            if (subAssets == null) return null;
            foreach (Object obj in subAssets)
            {
                if (obj is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                {
                    return clip;
                }
            }
            return null;
        }

        private static void BuildCombatPrototypeScene(RuntimeAnimatorController animCtrl)
        {
            Directory.CreateDirectory(MaterialsDir);
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 1. Single-light Moonlit Joseon Trilight Ambient (0 additional lights -> ~30% cheaper mobile fragment shaders!)
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.36f, 0.46f, 0.62f, 1f);
            RenderSettings.ambientEquatorColor = new Color(0.24f, 0.30f, 0.40f, 1f);
            RenderSettings.ambientGroundColor = new Color(0.11f, 0.13f, 0.17f, 1f);
            RenderSettings.fog = false;

            // Single directional moonlight key light (zero additional-light pass overhead)
            GameObject keyLightGo = new GameObject("Moonlight_Key");
            Light keyLight = keyLightGo.AddComponent<Light>();
            keyLight.type = LightType.Directional;
            keyLight.color = new Color(0.86f, 0.92f, 1.0f, 1f);
            keyLight.intensity = 1.55f;
            keyLight.shadows = LightShadows.Hard;
            keyLightGo.transform.rotation = Quaternion.Euler(34f, -26f, 0f);

            // 2. Side-View 2.5D Main Camera
            GameObject camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            Camera cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.055f, 0.068f, 0.095f, 1f);
            cam.fieldOfView = 36f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 80f;
            camGo.transform.position = new Vector3(0f, 1.95f, -7.8f);
            camGo.transform.LookAt(new Vector3(0f, 1.15f, 0f));
            camGo.AddComponent<AudioListener>();

            // 3. Materials (matte silk for Hero, weathered armor for Boss, ink-wash cobblestone courtyard floor)
            Material floorMat = CreateUrpLitMaterial($"{MaterialsDir}/Mat_StoneFloor.mat", LoadTex(FloorTexPath), new Color(0.72f, 0.78f, 0.85f, 1f), 0.0f, 0.08f);
            floorMat.SetTextureScale("_BaseMap", new Vector2(6f, 2.5f));
            floorMat.SetTextureScale("_MainTex", new Vector2(6f, 2.5f));
            EditorUtility.SetDirty(floorMat);
            Material heroMat = CreateUrpLitMaterial($"{MaterialsDir}/Mat_HeroEosa.mat", LoadTex(HeroTexPath), Color.white, 0.04f, 0.12f);
            Material bossMat = CreateUrpLitMaterial($"{MaterialsDir}/Mat_BossGeneral.mat", LoadTex(BossTexPath), Color.white, 0.24f, 0.28f);
            Material hwandoMat = CreateUrpLitMaterial($"{MaterialsDir}/Mat_WeaponHwando.mat", LoadTex(HwandoTexPath), Color.white, 0.68f, 0.65f);
            Material bossBladeMat = CreateUrpLitMaterial($"{MaterialsDir}/Mat_BossBlade.mat", LoadTex(HwandoTexPath), new Color(0.72f, 0.64f, 0.58f, 1f), 0.72f, 0.58f);
            Material mapaeMat = CreateUrpLitMaterial($"{MaterialsDir}/Mat_WeaponMapae.mat", LoadTex(MapaeTexPath), Color.white, 0.72f, 0.62f);
            Material bgMat = CreateUrpUnlitMaterial($"{MaterialsDir}/Mat_BgMountainTemple.mat", LoadTex(BgTemplePath));
            Material trailMat = CreateAdditiveVfxMaterial($"{MaterialsDir}/Mat_SwordTrail.mat", LoadTex(SlashTrailTexPath));
            Material sparkMat = CreateAdditiveVfxMaterial($"{MaterialsDir}/Mat_SparkParticle.mat", LoadTex(SparkTexPath));
            Material clashRingMat = CreateAdditiveVfxMaterial($"{MaterialsDir}/Mat_ClashRing.mat", LoadTex(ClashRingTexPath));

            // 4. Seamless Ink-Wash Cobblestone Courtyard Floor (38m wide) + Full Moon Temple Backdrop (26.4m wide + camera parallax -> zero black bars on 21:9 phones!)
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "ArenaFloor";
            Object.DestroyImmediate(floor.GetComponent<Collider>());
            floor.transform.position = new Vector3(0f, -0.04f, -0.2f);
            floor.transform.localScale = new Vector3(38f, 0.08f, 12.0f);
            Renderer floorRend = floor.GetComponent<Renderer>();
            floorRend.sharedMaterial = floorMat;
            floorRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            GameObject bgQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bgQuad.name = "BG_MountainTemple";
            Object.DestroyImmediate(bgQuad.GetComponent<Collider>());
            bgQuad.transform.position = new Vector3(0f, 2.35f, 5.2f);
            bgQuad.transform.localScale = new Vector3(24.0f, 13.5f, 1f);
            Renderer bgRend = bgQuad.GetComponent<Renderer>();
            bgRend.sharedMaterial = bgMat;
            bgRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            bgRend.receiveShadows = false;

            // 5. Player 3D Humanoid (암행어사 + 어사 환도 + 삼마패)
            GameObject playerGo = CreateHumanoidCombatant(
                "Player_AmhaengEosa",
                new Vector3(-2.6f, 0f, 0f),
                facingSign: 1f,
                targetHeightMeters: 1.80f,
                HeroFbxPath,
                heroMat,
                animCtrl,
                HwandoObjPath,
                hwandoMat,
                weaponScale: 1.0f,
                attachMapae: true,
                mapaeMat: mapaeMat,
                trailMat: trailMat,
                weaponTint: Color.white,
                out CombatantVisual playerVis);
            PlayerController playerCtrl = playerGo.AddComponent<PlayerController>();

            // 6. Boss 3D Humanoid (타락한 조선 대장군 + 대도)
            GameObject bossGo = CreateHumanoidCombatant(
                "Boss_FallenGeneral",
                new Vector3(2.6f, 0f, 0f),
                facingSign: -1f,
                targetHeightMeters: 2.20f,
                BossFbxPath,
                bossMat,
                animCtrl,
                HwandoObjPath,
                bossBladeMat,
                weaponScale: 1.48f,
                attachMapae: false,
                mapaeMat: null,
                trailMat: trailMat,
                weaponTint: new Color(0.85f, 0.72f, 0.65f, 1f),
                out CombatantVisual bossVis);
            GeneralBossController bossCtrl = bossGo.AddComponent<GeneralBossController>();

            // 7. Combat Juice Controller (Hitstop, Camera Shake, CC0 Audio, Spark VFX, Backdrop Parallax)
            GameObject systemsGo = new GameObject("CombatSystems");
            CombatJuiceController juice = systemsGo.AddComponent<CombatJuiceController>();
            juice.BindReferences(cam, playerGo.transform, bossGo.transform, sparkMat, bgQuad.transform, clashRingMat);

            AudioClip[] parryClips =
            {
                AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/sfx_parry_1.ogg"),
                AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/sfx_parry_2.ogg"),
                AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/sfx_parry_3.ogg"),
            };
            AudioClip[] slashClips =
            {
                AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/sfx_slash_1.ogg"),
                AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/sfx_slash_2.ogg"),
            };

            juice.BindClips(
                parries: parryClips,
                guard: AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/sfx_guard.ogg"),
                slashes: slashClips,
                hit: AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/sfx_hit.ogg"),
                dangerGong: AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/sfx_danger_gong.ogg"),
                ganpa: AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/sfx_ganpa.ogg"),
                guardBreak: AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/sfx_guard_break.ogg"),
                execution: AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/sfx_execution.ogg"),
                slam: AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/sfx_slam.ogg"));

            // 8. Canvas + Mobile Art HUD
            GameObject canvasGo = new GameObject("CombatHUDCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            CombatHUD hud = canvasGo.AddComponent<CombatHUD>();
            Font krFont = AssetDatabase.LoadAssetAtPath<Font>("Assets/Fonts/NotoSerifKR-Bold.ttf");
            hud.SetFont(krFont);
            hud.SetArtSprites(
                parry: AssetDatabase.LoadAssetAtPath<Sprite>(BtnParryPath),
                attack: AssetDatabase.LoadAssetAtPath<Sprite>(BtnAttackPath),
                ganpa: AssetDatabase.LoadAssetAtPath<Sprite>(BtnGanpaPath),
                dodge: AssetDatabase.LoadAssetAtPath<Sprite>(BtnDodgePath),
                dangerSal: AssetDatabase.LoadAssetAtPath<Sprite>(DangerSalPath));

            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

            // 9. Duel Manager
            CombatDuelManager duelManager = systemsGo.AddComponent<CombatDuelManager>();
            duelManager.Bind(playerCtrl, bossCtrl, juice, hud);
            playerCtrl.Bind(duelManager, bossCtrl, juice, playerVis);
            bossCtrl.Bind(duelManager, playerCtrl, juice, bossVis, sparkMat);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(ScenePath, true)
            };
        }

        private static GameObject CreateHumanoidCombatant(
            string rootName,
            Vector3 spawnPos,
            float facingSign,
            float targetHeightMeters,
            string fbxPath,
            Material bodyMat,
            RuntimeAnimatorController animCtrl,
            string weaponObjPath,
            Material weaponMat,
            float weaponScale,
            bool attachMapae,
            Material mapaeMat,
            Material trailMat,
            Color weaponTint,
            out CombatantVisual visual)
        {
            GameObject root = new GameObject(rootName);
            root.transform.position = spawnPos;

            GameObject fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(fbxAsset);
            model.name = "CharacterModel";
            model.transform.SetParent(root.transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.Euler(0f, facingSign > 0f ? 90f : -90f, 0f);

            // Apply URP material and disable offscreen culling on SkinnedMeshRenderers
            Renderer primaryBodyRend = null;
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                r.sharedMaterial = bodyMat;
                if (r is SkinnedMeshRenderer smr)
                {
                    smr.updateWhenOffscreen = true;
                }
                if (primaryBodyRend == null) primaryBodyRend = r;
            }

            // Measure actual world bounds and normalize height cleanly if FBX imported in cm or different scale
            if (primaryBodyRend != null)
            {
                float curHeight = primaryBodyRend.bounds.size.y;
                if (curHeight > 0.0001f && Mathf.Abs(curHeight - targetHeightMeters) > 0.15f)
                {
                    float factor = targetHeightMeters / curHeight;
                    model.transform.localScale = Vector3.one * factor;
                }
            }

            Animator anim = model.GetComponent<Animator>();
            if (anim == null) anim = model.AddComponent<Animator>();
            anim.runtimeAnimatorController = animCtrl;
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            // Attach 3D Hwando sword to RightHand bone (compensating for any bone scale so sword is exact real-world size)
            Transform rightHand = anim.GetBoneTransform(HumanBodyBones.RightHand);
            if (rightHand == null) rightHand = model.transform;
            float rhScale = Mathf.Max(0.001f, rightHand.lossyScale.x);

            GameObject weaponPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(weaponObjPath);
            Renderer weaponRend = null;
            Transform clashAnchor = null;
            TrailRenderer swordTrail = null;

            if (weaponPrefab != null)
            {
                GameObject weaponGo = (GameObject)PrefabUtility.InstantiatePrefab(weaponPrefab);
                weaponGo.name = "EquippedBlade";
                weaponGo.transform.SetParent(rightHand, false);
                // Center wrapped grip directly inside palm; +85 X aligns +Y sword blade cleanly forward-up
                weaponGo.transform.localPosition = new Vector3(0.0f, 0.05f, 0.02f) / rhScale;
                weaponGo.transform.localRotation = Quaternion.Euler(85f, 0f, 0f);
                weaponGo.transform.localScale = Vector3.one * (weaponScale / rhScale);

                foreach (Renderer r in weaponGo.GetComponentsInChildren<Renderer>(true))
                {
                    r.sharedMaterial = weaponMat;
                    if (weaponRend == null) weaponRend = r;
                }

                GameObject tipGo = new GameObject("BladeTip");
                tipGo.transform.SetParent(weaponGo.transform, false);
                tipGo.transform.localPosition = new Vector3(0f, 0.82f, 0f);
                clashAnchor = tipGo.transform;

                swordTrail = tipGo.AddComponent<TrailRenderer>();
                swordTrail.time = 0.14f;
                swordTrail.minVertexDistance = 0.04f;
                swordTrail.widthCurve = new AnimationCurve(
                    new Keyframe(0f, 0.34f * weaponScale),
                    new Keyframe(0.35f, 0.26f * weaponScale),
                    new Keyframe(1f, 0.01f));
                swordTrail.textureMode = LineTextureMode.Stretch;
                swordTrail.numCornerVertices = 4;
                swordTrail.numCapVertices = 2;
                swordTrail.sharedMaterial = trailMat;
                swordTrail.emitting = false;
                swordTrail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                swordTrail.receiveShadows = false;
            }

            // Attach 3D Mapae medallion to Hero's LeftHand
            if (attachMapae && mapaeMat != null)
            {
                Transform leftHand = anim.GetBoneTransform(HumanBodyBones.LeftHand);
                GameObject mapaePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(MapaeObjPath);
                if (leftHand != null && mapaePrefab != null)
                {
                    float lhScale = Mathf.Max(0.001f, leftHand.lossyScale.x);
                    GameObject mapaeGo = (GameObject)PrefabUtility.InstantiatePrefab(mapaePrefab);
                    mapaeGo.name = "EquippedMapae";
                    mapaeGo.transform.SetParent(leftHand, false);
                    mapaeGo.transform.localPosition = new Vector3(0f, 0.07f, 0.03f) / lhScale;
                    mapaeGo.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                    mapaeGo.transform.localScale = Vector3.one * (0.72f / lhScale);
                    foreach (Renderer r in mapaeGo.GetComponentsInChildren<Renderer>(true))
                    {
                        r.sharedMaterial = mapaeMat;
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        r.receiveShadows = false;
                    }
                }
            }

            if (primaryBodyRend != null)
            {
                Bounds b = primaryBodyRend.bounds;
                Debug.Log($"[EosaBlade] VERIFY {rootName}: boundsSize={b.size} boundsCenter={b.center} modelScale={model.transform.localScale} rightHandPos={rightHand.position} rightHandScale={rightHand.lossyScale}");
            }

            visual = root.AddComponent<CombatantVisual>();
            visual.BindHumanoid(anim, model.transform, primaryBodyRend, weaponRend, clashAnchor, swordTrail, weaponTint);
            return root;
        }

        [MenuItem("EosaBlade/2. Capture Camera Diagnostic Screenshots")]
        public static void CaptureDiagnosticScreenshots()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            string outDir = @"C:\Users\user\.gemini\antigravity-cli\brain\dbac198a-fa9e-4371-a3f1-fe0063838f52\scratch";
            Directory.CreateDirectory(outDir);

            // Force Edit-mode SkinnedMeshRenderers to recompute bone matrices on cam.Render()
            foreach (var smr in Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
            {
                smr.forceMatrixRecalculationPerRender = true;
            }

            LogCharacterRigState("TPOSE_BEFORE_ANIM");
            RenderMainCameraToPng(Path.Combine(outDir, "cam_tpose.png"), 1280, 720);

            // Sample Idle animation at t=0.5s directly on both Humanoid Animators (exact same evaluation as Play Mode!)
            foreach (var vis in Object.FindObjectsByType<CombatantVisual>(FindObjectsSortMode.None))
            {
                var anim = vis.GetComponentInChildren<Animator>();
                if (anim != null && anim.runtimeAnimatorController != null)
                {
                    anim.Rebind();
                    anim.Play("Idle", 0, 0.25f);
                    anim.Update(0.05f);
                    anim.Update(0.05f);
                }
            }

            LogCharacterRigState("AFTER_IDLE_ANIM");
            RenderMainCameraToPng(Path.Combine(outDir, "cam_idle.png"), 1280, 720);
            // Also verify 21:9 ultrawide aspect (Galaxy S22/S24) has zero left/right black bars!
            RenderMainCameraToPng(Path.Combine(outDir, "cam_21_9.png"), 1680, 720);

            // Sample Boss 殺 Thrust mid-strike to prove Boss stays 100% grounded (zero sky-launch!)
            GameObject bossObj = GameObject.Find("Boss_FallenGeneral");
            if (bossObj != null)
            {
                var banim = bossObj.GetComponentInChildren<Animator>();
                if (banim != null)
                {
                    banim.Play("Thrust", 0, 0.50f);
                    banim.Update(0.05f);
                    banim.Update(0.05f);
                }
            }
            LogCharacterRigState("AFTER_BOSS_THRUST");
            RenderMainCameraToPng(Path.Combine(outDir, "cam_thrust.png"), 1280, 720);

            // Sample Player Sword Guard & Sword Parry poses (verifies blade raised in front, zero invisible shield!)
            GameObject playerObj = GameObject.Find("Player_AmhaengEosa");
            if (playerObj != null)
            {
                var pvis = playerObj.GetComponent<CombatantVisual>();
                var panim = playerObj.GetComponentInChildren<Animator>();
                Camera cam = Camera.main;
                if (pvis != null && panim != null && cam != null)
                {
                    Vector3 origPos = cam.transform.position;
                    Quaternion origRot = cam.transform.rotation;
                    float origFov = cam.fieldOfView;

                    panim.Rebind();
                    panim.Play("Idle", 0, 0.25f);
                    panim.Update(0.05f);
                    panim.Update(0.05f);
                    pvis.ApplyPoseNowForPreview(WeaponPose.Guard);
                    RenderMainCameraToPng(Path.Combine(outDir, "cam_guard.png"), 1280, 720);

                    // Closeup right on Hero in Guard pose to verify exact hand, elbow, left arm, and blade angle
                    cam.transform.position = playerObj.transform.position + new Vector3(0.4f, 1.35f, -3.1f);
                    cam.transform.LookAt(playerObj.transform.position + new Vector3(0.25f, 1.15f, 0f));
                    cam.fieldOfView = 34f;
                    RenderMainCameraToPng(Path.Combine(outDir, "cam_guard_closeup.png"), 1024, 768);

                    panim.Rebind();
                    panim.Play("Idle", 0, 0.25f);
                    panim.Update(0.05f);
                    panim.Update(0.05f);
                    pvis.ApplyPoseNowForPreview(WeaponPose.ParryFlash, 0.28f);
                    RenderMainCameraToPng(Path.Combine(outDir, "cam_parry_closeup.png"), 1024, 768);

                    cam.transform.position = origPos;
                    cam.transform.rotation = origRot;
                    cam.fieldOfView = origFov;
                    RenderMainCameraToPng(Path.Combine(outDir, "cam_parry.png"), 1280, 720);
                }
            }
            Debug.Log("[EosaBlade] Diagnostic screenshots captured.");
        }

        private static void LogCharacterRigState(string stage)
        {
            foreach (var name in new[] { "Player_AmhaengEosa", "Boss_FallenGeneral" })
            {
                GameObject go = GameObject.Find(name);
                if (go == null) continue;
                var anim = go.GetComponentInChildren<Animator>();
                var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
                string boneInfo = "";
                if (anim != null)
                {
                    Transform hips = anim.GetBoneTransform(HumanBodyBones.Hips);
                    Transform head = anim.GetBoneTransform(HumanBodyBones.Head);
                    Transform lf = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
                    Transform rf = anim.GetBoneTransform(HumanBodyBones.RightFoot);
                    Transform rh = anim.GetBoneTransform(HumanBodyBones.RightHand);
                    boneInfo = $"humanScale={anim.humanScale:F4} isHuman={anim.isHuman} " +
                               $"Hips={(hips ? hips.position.ToString("F3") : "null")} " +
                               $"Head={(head ? head.position.ToString("F3") : "null")} " +
                               $"LFoot={(lf ? lf.position.ToString("F3") : "null")} " +
                               $"RFoot={(rf ? rf.position.ToString("F3") : "null")} " +
                               $"RHand={(rh ? rh.position.ToString("F3") : "null")}";
                }
                string smrInfo = smr != null ? $"boundsSize={smr.bounds.size:F3} boundsCenter={smr.bounds.center:F3} rootBone={(smr.rootBone ? smr.rootBone.localScale.ToString("F3") : "null")}" : "noSMR";
                Debug.Log($"[EosaBlade] [{stage}] {name}: {boneInfo} | {smrInfo}");
            }
        }

        private static void RenderMainCameraToPng(string filePath, int width, int height)
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            RenderTexture rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            cam.targetTexture = prev;
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            File.WriteAllBytes(filePath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        private static Texture2D LoadTex(string path) => AssetDatabase.LoadAssetAtPath<Texture2D>(path);

        private static Material CreateUrpLitMaterial(string assetPath, Texture2D mainTex, Color baseColor, float metallic, float smoothness)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");

            Material mat = existing != null ? existing : new Material(shader);
            mat.shader = shader;
            mat.SetColor("_BaseColor", baseColor);
            mat.SetColor("_Color", baseColor);
            if (mainTex != null)
            {
                mat.SetTexture("_BaseMap", mainTex);
                mat.SetTexture("_MainTex", mainTex);
            }
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smoothness);
            // Keep _EMISSION keyword alive in URP builds (non-zero tiny color + RealtimeEmissive prevents stripping)
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            mat.SetColor("_EmissionColor", new Color(0.004f, 0.004f, 0.004f, 1f));

            if (existing == null) AssetDatabase.CreateAsset(mat, assetPath);
            else EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material CreateAdditiveVfxMaterial(string assetPath, Texture2D vfxTex)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            Material mat = existing != null ? existing : new Material(shader);
            mat.shader = shader;
            if (vfxTex != null)
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", vfxTex);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", vfxTex);
            }
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f); // Transparent
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 2f);     // Additive
            if (mat.HasProperty("_ColorMode")) mat.SetFloat("_ColorMode", 0f); // Multiply with vertex color
            if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
            if (mat.HasProperty("_Cull")) mat.SetInt("_Cull", 0); // Double-sided so sword trails are visible from any angle
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.EnableKeyword("_BLENDMODE_ADD");
            mat.renderQueue = 3000;

            if (existing == null) AssetDatabase.CreateAsset(mat, assetPath);
            else EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material CreateUrpUnlitMaterial(string assetPath, Texture2D mainTex)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Texture");

            Material mat = existing != null ? existing : new Material(shader);
            mat.shader = shader;
            mat.SetColor("_BaseColor", Color.white);
            if (mainTex != null)
            {
                mat.SetTexture("_BaseMap", mainTex);
                mat.SetTexture("_MainTex", mainTex);
            }

            if (existing == null) AssetDatabase.CreateAsset(mat, assetPath);
            else EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material CreateSwordTrailMaterial(string assetPath)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");

            Material mat = existing != null ? existing : new Material(shader);
            mat.shader = shader;
            mat.SetColor("_Color", Color.white);

            if (existing == null) AssetDatabase.CreateAsset(mat, assetPath);
            else EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
#endif
