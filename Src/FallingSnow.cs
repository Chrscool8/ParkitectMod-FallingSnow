using HarmonyLib;

using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

using Parkitect.UI;

using TMPro;

using UnityEngine;
using UnityEngine.UI;

namespace FallingSnow
{
    /// <summary>
    /// When it rains (or storms) below freezing, the park gets snow instead: falling flakes over the whole park, with
    /// the cloudy-day ambience and without the rain's lens drops, wet ground, and rain sounds. Everything is made in code;
    /// the mod ships no assets. Lightning still happens, so storms become thundersnow.
    /// </summary>
    public class FS : AbstractMod, IModSettings
    {
        public const string VERSION_NUMBER = "261025";
        // Kept from the original BananaLib version, so players who had it on keep it on.
        public const string IDENTIFIER = "ChrisBradel::FallingSnow";
        private const string LOG_PREFIX = "FallingSnow: ";

        private Harmony harmony;

        public override string getIdentifier() => IDENTIFIER;

        public override string getName() => "Falling Snow";

        public override string getDescription() => @"Rain turns to snow when it's below freezing.";

        public override string getVersionNumber() => VERSION_NUMBER;

        public override bool isMultiplayerModeCompatible() => true;

        public override bool isRequiredByAllPlayersInMultiplayerMode() => false;

        public override void onEnabled()
        {
            harmony = new Harmony(IDENTIFIER);
            harmony.PatchAll(typeof(FS).Assembly);
            Snow.Enable();
            Log("Enabled.");
        }

        public override void onDisabled()
        {
            Snow.Disable();
            harmony?.UnpatchAll(IDENTIFIER);
            harmony = null;
        }

        public void onDrawSettingsUI()
        {
            GUILayout.Label("Snow amount: " + Mathf.RoundToInt(FSSettings.Intensity.Value * 100f) + "%");
            FSSettings.Intensity.Value = Mathf.Round(GUILayout.HorizontalSlider(FSSettings.Intensity.Value, 0.25f, 3f) * 20f) / 20f;

            GUILayout.Space(8f);
            GUILayout.Label("Snow below: " + TextUtility.formatTemperature(FSSettings.FreezingPoint.Value));
            FSSettings.FreezingPoint.Value = Mathf.Round(GUILayout.HorizontalSlider(FSSettings.FreezingPoint.Value, -10f, 10f));
        }

        public void onSettingsOpened() { }

        public void onSettingsClosed()
        {
            PlayerPrefs.Save();
            Snow.Refresh(rebuild: true);
        }

        public static void Log(string message)
        {
            Debug.LogWarning(LOG_PREFIX + message);
        }
    }

    /// <summary>Per-player settings, kept in PlayerPrefs.</summary>
    public static class FSSettings
    {
        public sealed class Slider
        {
            private readonly string key;
            private readonly float defaultValue;
            private float? value;

            public Slider(string key, float defaultValue)
            {
                this.key = key;
                this.defaultValue = defaultValue;
            }

            public float Value
            {
                get
                {
                    if (value == null)
                        value = PlayerPrefs.GetFloat(key, defaultValue);
                    return value.Value;
                }
                set
                {
                    if (this.value == value)
                        return;

                    this.value = value;
                    PlayerPrefs.SetFloat(key, value);
                }
            }
        }

        /// <summary>Multiplier on how many flakes fall.</summary>
        public static readonly Slider Intensity = new Slider("FS_Intensity", 1f);

        /// <summary>Rain below this temperature (°C) falls as snow.</summary>
        public static readonly Slider FreezingPoint = new Slider("FS_FreezingPoint", 0f);
    }

    /// <summary>
    /// Decides between rain and snow, and keeps the park's weather visuals matching. The game sets a new weather's
    /// temperature only after its rain has started, so the choice is checked again whenever the temperature changes.
    /// </summary>
    public static class Snow
    {
        private static readonly AccessTools.FieldRef<RainController, bool> rainAndLightningEnabled = AccessTools.FieldRefAccess<RainController, bool>("rainAndLightningEnabled");
        private static readonly MethodInfo animateStart = AccessTools.Method(typeof(RainController), "animateStart");
        private static readonly MethodInfo animateEnd = AccessTools.Method(typeof(RainController), "animateEnd");
        private static readonly MethodInfo changeAmbient = AccessTools.Method(typeof(WeatherController), "changeAmbient");

        private const float FADE_SECONDS = 3f;

        private static readonly List<ParticleSystem> flakes = new List<ParticleSystem>();
        // Each snowfall has its own material, so one can fade out while the next fades in.
        private static Material material;
        private static EventManager subscribedTo;

        /// <summary>Whether the rain's lens drops and wet ground are currently shown.</summary>
        public static bool RainVisualsShown { get; set; }

        /// <summary>Whether the current rain is falling as snow.</summary>
        public static bool IsSnowing { get; private set; }

        private static WeatherController Weather => GameController.Instance != null && GameController.Instance.park != null ? GameController.Instance.park.weatherController : null;

        public static bool ShouldSnow
        {
            get
            {
                WeatherController weather = Weather;
                return weather != null && weather.Temperature < FSSettings.FreezingPoint.Value;
            }
        }

        public static void Enable()
        {
            Subscribe();
            Refresh(rebuild: false);
        }

        public static void Disable()
        {
            if (subscribedTo != null)
                subscribedTo.OnTemperatureChanged -= OnTemperatureChanged;
            subscribedTo = null;

            Kill(null);
            IsSnowing = false;
        }

        // The event manager can be replaced between parks, so this is called again whenever weather starts.
        public static void Subscribe()
        {
            EventManager events = EventManager.Instance;
            if (events == null || events == subscribedTo)
                return;

            if (subscribedTo != null)
                subscribedTo.OnTemperatureChanged -= OnTemperatureChanged;
            events.OnTemperatureChanged += OnTemperatureChanged;
            subscribedTo = events;
        }

        private static void OnTemperatureChanged()
        {
            Refresh(rebuild: false);
        }

        /// <summary>Switches between rain and snow if the temperature (or the settings) call for it.</summary>
        public static void Refresh(bool rebuild)
        {
            WeatherController weather = Weather;
            RainController rain = weather != null ? weather.rainController : null;
            if (rain == null || !rainAndLightningEnabled(rain))
                return;

            bool snow = ShouldSnow;
            if (snow == IsSnowing && !rebuild)
                return;

            // Snow is re-made too when its settings change.
            Decide();
            rain.KillRainParticles();
            if (Settings.Instance.graphicsRainEffect)
                rain.InstantiateRainParticles();

            if (snow == RainVisualsShown)
                rain.StartCoroutine((IEnumerator)(snow ? animateEnd : animateStart).Invoke(rain, null));

            changeAmbient.Invoke(weather, new object[] { ScriptableSingleton<SoundAssetManager>.Instance.ambientRain });
        }

        public static void Decide()
        {
            IsSnowing = ShouldSnow;
        }

        /// <summary>
        /// Covers the park in the same 10×10 grid of emitters the game uses for rain. Storms (the rain's storm speed) get
        /// a blizzard, whose wind blows in a random direction. Its grid shifts upwind and reaches further past the park,
        /// so the snow still covers the whole park on its way down.
        /// </summary>
        public static void Spawn(RainController rain)
        {
            if (flakes.Count > 0)
                return;

            Park park = GameController.Instance.park;
            material = SnowEmitter.NewMaterial();
            rain.StartCoroutine(Fade(material, 0f, 1f, FADE_SECONDS));
            bool blizzard = park.weatherController.rainController.rainFallSpeed == RainController.RAIN_SPEED.STORM;

            Vector2 wind = Vector2.zero;
            int margin = 1;
            if (blizzard)
            {
                wind = Random.insideUnitCircle.normalized * SnowEmitter.BLIZZARD_WIND;
                float fallTime = SnowEmitter.HEIGHT / (SnowEmitter.AVERAGE_FALL_SPEED * SnowEmitter.BLIZZARD_SPEED);
                margin += Mathf.CeilToInt(wind.magnitude * fallTime / 2f / 10f);
            }

            // Start upwind by half the distance the wind carries the snow while it falls.
            Vector2 upwind = -wind * (SnowEmitter.HEIGHT / (SnowEmitter.AVERAGE_FALL_SPEED * (blizzard ? SnowEmitter.BLIZZARD_SPEED : 1f))) / 2f;
            for (int x = -margin; x < Mathf.CeilToInt(park.xSize / 10f) + margin; x++)
            {
                for (int z = -margin; z < Mathf.CeilToInt(park.zSize / 10f) + margin; z++)
                    flakes.Add(SnowEmitter.Create(new Vector3(x * 10 + upwind.x, SnowEmitter.HEIGHT, z * 10 + upwind.y), blizzard, wind, material));
            }
        }

        /// <summary>
        /// Fades the snow out and then removes it, using <paramref name="host"/> to run the fade. Without a host (the mod
        /// being switched off), it's removed right away.
        /// </summary>
        public static void Kill(MonoBehaviour host)
        {
            List<ParticleSystem> emitters = new List<ParticleSystem>(flakes);
            flakes.Clear();
            foreach (ParticleSystem emitter in emitters)
            {
                if (emitter != null)
                    emitter.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmitting);
            }

            if (host != null && host.isActiveAndEnabled)
                host.StartCoroutine(FadeAndDestroy(emitters, material));
            else
                Destroy(emitters, material);
            material = null;
        }

        private static IEnumerator FadeAndDestroy(List<ParticleSystem> emitters, Material fading)
        {
            yield return Fade(fading, fading != null ? fading.color.a : 0f, 0f, FADE_SECONDS);
            Destroy(emitters, fading);
        }

        private static void Destroy(List<ParticleSystem> emitters, Material fading)
        {
            foreach (ParticleSystem emitter in emitters)
            {
                if (emitter != null)
                    Object.Destroy(emitter.gameObject);
            }

            if (fading != null)
                Object.Destroy(fading);
        }

        private static IEnumerator Fade(Material fading, float from, float to, float seconds)
        {
            for (float t = 0f; t < 1f && fading != null; t += Time.deltaTime / seconds)
            {
                fading.color = new Color(1f, 1f, 1f, Mathf.Lerp(from, to, t));
                yield return null;
            }

            if (fading != null)
                fading.color = new Color(1f, 1f, 1f, to);
        }
    }

    /// <summary>
    /// Builds one snow emitter. Like the game's rain, each particle is a big sheet covered in many flakes rather than a
    /// single flake, so the sky looks full for very few particles. Sheets come in four patterns and random turns so
    /// they don't repeat, drift a little sideways, and the emitter starts out already full.
    /// </summary>
    public static class SnowEmitter
    {
        public const float HEIGHT = 60f;
        // Sheets disappear just under the lowest the terrain can go (0), so none keep falling below the park.
        private const float FLOOR = -1f;
        private const float SHEETS_PER_SECOND = 0.4f;
        private const int TILE_SIZE = 256;
        private const int FLAKES_PER_TILE = 14;

        // Storms become blizzards: more snow, falling faster, blown sideways by a steady wind.
        public const float BLIZZARD_AMOUNT = 2f;
        public const float BLIZZARD_SPEED = 1.5f;
        public const float BLIZZARD_WIND = 1.5f;
        public const float AVERAGE_FALL_SPEED = 1.5f;

        private static Material baseMaterial;

        public static ParticleSystem Create(Vector3 position, bool blizzard, Vector2 wind, Material material)
        {
            // One speed per emitter, so each sheet's lifetime ends exactly at FLOOR. Neighboring emitters still differ.
            float speed = Random.Range(1.2f, 1.8f) * (blizzard ? BLIZZARD_SPEED : 1f);
            float lifetime = (HEIGHT - FLOOR) / speed;

            GameObject go = new GameObject("FallingSnow");
            go.transform.position = position;
            // Emit straight down.
            go.transform.eulerAngles = new Vector3(90f, 0f, 0f);

            ParticleSystem system = go.AddComponent<ParticleSystem>();
            system.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = system.main;
            main.loop = true;
            main.prewarm = true;
            main.duration = lifetime;
            main.startLifetime = lifetime;
            main.startSpeed = speed;
            main.startSize = new ParticleSystem.MinMaxCurve(4f, 7f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 2f * Mathf.PI);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 1f, 1f, 0.8f), Color.white);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 500;
            // Like the rain: off-screen emitters skip ahead instead of pausing, so turning the camera finds them full.
            main.cullingMode = ParticleSystemCullingMode.Automatic;

            ParticleSystem.EmissionModule emission = system.emission;
            emission.rateOverTime = SHEETS_PER_SECOND * FSSettings.Intensity.Value * (blizzard ? BLIZZARD_AMOUNT : 1f);

            ParticleSystem.ShapeModule shape = system.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(10f, 10f, 0f);

            ParticleSystem.VelocityOverLifetimeModule drift = system.velocityOverLifetime;
            drift.enabled = true;
            drift.space = ParticleSystemSimulationSpace.World;
            drift.x = new ParticleSystem.MinMaxCurve(wind.x - 0.3f, wind.x + 0.3f);
            drift.y = new ParticleSystem.MinMaxCurve(0f, 0f);
            drift.z = new ParticleSystem.MinMaxCurve(wind.y - 0.3f, wind.y + 0.3f);

            // Each sheet keeps one random pattern from the 2×2 atlas.
            ParticleSystem.TextureSheetAnimationModule patterns = system.textureSheetAnimation;
            patterns.enabled = true;
            patterns.numTilesX = 2;
            patterns.numTilesY = 2;
            patterns.frameOverTime = new ParticleSystem.MinMaxCurve(0f, 0.999f);

            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            system.Play();
            return system;
        }

        /// <summary>A copy of the snow material for one snowfall, starting out invisible so it can fade in.</summary>
        public static Material NewMaterial()
        {
            // Sprites/Default is always included in Unity builds, and it takes particle colors and alpha as they are.
            if (baseMaterial == null)
            {
                baseMaterial = new Material(Shader.Find("Sprites/Default"))
                {
                    name = "FallingSnow",
                    mainTexture = MakeSheetAtlas(),
                };
            }

            return new Material(baseMaterial) { color = new Color(1f, 1f, 1f, 0f) };
        }

        /// <summary>Four tiles of scattered soft white flakes in assorted sizes, the same every time.</summary>
        private static Texture2D MakeSheetAtlas()
        {
            int size = TILE_SIZE * 2;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true)
            {
                name = "FallingSnowSheets",
                wrapMode = TextureWrapMode.Clamp,
            };

            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color(1f, 1f, 1f, 0f);

            System.Random random = new System.Random(2866424079.GetHashCode());
            for (int tile = 0; tile < 4; tile++)
            {
                int left = (tile % 2) * TILE_SIZE;
                int bottom = (tile / 2) * TILE_SIZE;

                for (int flake = 0; flake < FLAKES_PER_TILE; flake++)
                {
                    float radius = 1f + (float)random.NextDouble() * 1.8f;
                    // Kept away from the tile's edges so neighboring tiles don't bleed in.
                    float cx = left + 6 + (float)random.NextDouble() * (TILE_SIZE - 12);
                    float cy = bottom + 6 + (float)random.NextDouble() * (TILE_SIZE - 12);
                    DrawFlake(pixels, size, cx, cy, radius);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return texture;
        }

        private static void DrawFlake(Color[] pixels, int size, float cx, float cy, float radius)
        {
            int reach = Mathf.CeilToInt(radius) + 1;
            for (int y = (int)cy - reach; y <= (int)cy + reach; y++)
            {
                for (int x = (int)cx - reach; x <= (int)cx + reach; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy)) / radius;
                    float alpha = Mathf.SmoothStep(1f, 0f, distance);
                    int index = y * size + x;
                    if (alpha > pixels[index].a)
                        pixels[index].a = alpha;
                }
            }
        }
    }

    [HarmonyPatch(typeof(RainController), nameof(RainController.enableRainAndLightning))]
    public static class EnablePatch
    {
        private static void Prefix()
        {
            Snow.Subscribe();
            Snow.Decide();
        }
    }

    [HarmonyPatch(typeof(RainController), nameof(RainController.InstantiateRainParticles))]
    public static class InstantiatePatch
    {
        private static bool Prefix(RainController __instance)
        {
            Snow.Decide();
            if (!Snow.IsSnowing)
                return true;

            Snow.Spawn(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(RainController), nameof(RainController.KillRainParticles))]
    public static class KillPatch
    {
        private static void Postfix(RainController __instance)
        {
            Snow.Kill(__instance);
        }
    }

    /// <summary>The lens drops and wet ground fade in only for rain, and fade out only if they were shown.</summary>
    [HarmonyPatch(typeof(RainController), "animateStart")]
    public static class AnimateStartPatch
    {
        private static bool Prefix(ref IEnumerator __result)
        {
            if (Snow.IsSnowing)
            {
                __result = Nothing();
                return false;
            }

            Snow.RainVisualsShown = true;
            return true;
        }

        public static IEnumerator Nothing()
        {
            yield break;
        }
    }

    [HarmonyPatch(typeof(RainController), "animateEnd")]
    public static class AnimateEndPatch
    {
        private static bool Prefix(ref IEnumerator __result)
        {
            if (!Snow.RainVisualsShown)
            {
                __result = AnimateStartPatch.Nothing();
                return false;
            }

            Snow.RainVisualsShown = false;
            return true;
        }
    }

    /// <summary>
    /// Adds a temperature box and Set button under the weather buttons in the F12 cheats menu, made from the game's own
    /// controls. Entered in the player's units. Off in multiplayer, since temperature isn't synced between players.
    /// </summary>
    [HarmonyPatch(typeof(ParkCheatsTab), "Awake")]
    public static class CheatsTemperaturePatch
    {
        private static void Postfix(ParkCheatsTab __instance)
        {
            Transform weatherPanel = FindChild(__instance.transform, "WeatherPanel");
            TMP_InputField template = ScriptableSingleton<UIAssetManager>.Instance.renameWindowGO.GetComponentInChildren<TMP_InputField>(true);
            if (weatherPanel == null || weatherPanel.childCount == 0 || template == null)
            {
                FS.Log("Couldn't find the cheats menu's weather buttons; the temperature cheat is skipped.");
                return;
            }

            // The new row copies the weather row's layout and keeps one of its buttons as the Set button.
            GameObject row = Object.Instantiate(weatherPanel.gameObject, weatherPanel.parent, false);
            row.name = "FallingSnowTemperaturePanel";
            row.transform.SetSiblingIndex(weatherPanel.GetSiblingIndex() + 1);
            for (int i = row.transform.childCount - 1; i > 0; i--)
                Object.DestroyImmediate(row.transform.GetChild(i).gameObject);

            Button setButton = row.transform.GetChild(0).GetComponent<Button>();
            setButton.name = "SetTemperatureButton";
            setButton.onClick = new Button.ButtonClickedEvent();
            setButton.GetComponentInChildren<TextMeshProUGUI>().text = "Set " + Unit;

            TMP_InputField input = Object.Instantiate(template.gameObject, row.transform, false).GetComponent<TMP_InputField>();
            input.name = "TemperatureInput";
            input.transform.SetSiblingIndex(0);
            input.contentType = TMP_InputField.ContentType.DecimalNumber;
            input.characterLimit = 6;
            input.text = ToDisplay(GameController.Instance.park.weatherController.Temperature).ToString("F0", CultureInfo.CurrentCulture);

            bool multiplayer = CommandController.Instance.isInMultiplayerMode();
            input.interactable = !multiplayer;
            setButton.interactable = !multiplayer;
            setButton.onClick.AddListener(() =>
            {
                if (float.TryParse(input.text, NumberStyles.Float, CultureInfo.CurrentCulture, out float value))
                    GameController.Instance.park.weatherController.Temperature = FromDisplay(value);
            });
        }

        private static bool Imperial => Settings.Instance.unitSystem == Settings.UnitSystem.IMPERIAL;

        private static string Unit => Imperial ? "°F" : "°C";

        private static float ToDisplay(float celsius) => Imperial ? celsius * 1.8f + 32f : celsius;

        private static float FromDisplay(float value) => Imperial ? (value - 32f) / 1.8f : value;

        private static Transform FindChild(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name)
                    return child;

                Transform found = FindChild(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }
    }

    /// <summary>Snow is quiet: it keeps the cloudy-day ambience instead of rain sounds.</summary>
    [HarmonyPatch(typeof(WeatherController), "changeAmbient")]
    public static class AmbiencePatch
    {
        private static void Prefix(ref MoonaEvent newAmbient)
        {
            SoundAssetManager sounds = ScriptableSingleton<SoundAssetManager>.Instance;
            if (Snow.IsSnowing && newAmbient == sounds.ambientRain)
                newAmbient = sounds.ambientSun;
        }
    }
}
