// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// A small marker icon that tracks the same quest resource Seance's own ghost is seeking, drawn via
// OnGUI() so it appears "through" walls and geometry regardless of whether the ghost's own pathing
// can actually get there (see SeanceGhostGuide's own file header for the real NavMesh coverage gaps
// that motivated adding this as a backup). OnGUI() runs in its own screen-space pass after the 3D
// scene renders, so drawing a 2D icon at the target's WorldToScreenPoint needs no special shader,
// camera, or render layer to show "through" walls - it never touches the 3D scene at all. Confirmed
// this exact technique is what Hazelnut's own Archaeologists mod (nexusmods.com/daggerfallunity/mods/
// 14) uses for its locator device; the retro-rendering-mode coordinate conversion below is adapted
// from that same source and cross-checked against DFU's own HUDPlaceMarker.cs (a debug-only quest-site
// marker built into the base engine, not usable directly here since it's exterior/building-only and
// gated behind the quest debugger's own visibility toggle, but its math confirms the same approach).
//
// Marker texture is VoxsNecromancy.GetSeanceMarkerTexture() (a real image asset shipped with the mod,
// see that method's own comment) - a procedurally-generated plain white dot (CreateDotTexture below)
// is kept only as a fallback for the should-never-happen case that asset fails to load.
//
// Shares the exact same lifetime as the ghost itself (RoundsRemaining ticked on the same
// EntityEffectBroker.OnNewMagicRound event SeanceGhostLifetime.cs uses) and is dismissed alongside it
// from the same SeanceEffect.DoEffect() call site that already dismisses the previous ghost.

using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace VoxsNecromancyMod.UI
{
    public class SeanceQuestMarker : MonoBehaviour
    {
        static SeanceQuestMarker activeInstance;

        public static void DespawnActive()
        {
            if (activeInstance != null)
                GameObject.Destroy(activeInstance.gameObject);
        }

        // Called by SeanceEffect right alongside spawning the ghost - target may be null (no quest
        // resource currently placed in this dungeon), in which case no marker is created at all.
        public static void Create(Transform target, int roundsRemaining)
        {
            DespawnActive();
            if (target == null)
                return;

            GameObject go = new GameObject("SeanceQuestMarker");
            SeanceQuestMarker marker = go.AddComponent<SeanceQuestMarker>();
            marker.target = target;
            marker.roundsRemaining = roundsRemaining;
        }

        Transform target;
        int roundsRemaining;

        static Texture2D markerTexture;

        // Offset above the target's own transform - most quest resources are positioned at their own
        // natural (often ground-level) origin, and a marker right at that height can end up visually
        // buried in the target's own model or the floor.
        const float heightOffset = 1.5f;

        // Divisor floor for the size falloff below - prevents the dot from growing absurdly large at
        // very close range, and doubles as the "how close before it stops shrinking further" distance.
        const float minSizeDistance = 3f;
        const float baseSizePixels = 96f;
        const float minSizePixels = 6f;
        const float maxSizePixels = 28f;

        void Awake()
        {
            activeInstance = this;

            if (markerTexture == null)
                markerTexture = VoxsNecromancy.GetSeanceMarkerTexture();
            if (markerTexture == null)
                markerTexture = CreateDotTexture(32);
        }

        void OnEnable()
        {
            EntityEffectBroker.OnNewMagicRound += Tick;
        }

        void OnDisable()
        {
            EntityEffectBroker.OnNewMagicRound -= Tick;

            if (activeInstance == this)
                activeInstance = null;
        }

        void Tick()
        {
            roundsRemaining--;
            if (roundsRemaining <= 0)
                GameObject.Destroy(gameObject);
        }

        void OnGUI()
        {
            if (target == null || Event.current.type != EventType.Repaint || GameManager.IsGamePaused)
                return;

            Camera mainCamera = GameManager.Instance.MainCamera;
            if (mainCamera == null)
                return;

            Vector3 screenPos = mainCamera.WorldToScreenPoint(target.position + Vector3.up * heightOffset);
            if (screenPos.z <= 0)
                return;

            // Convert to real screen pixel coordinates (Unity's WorldToScreenPoint is bottom-left
            // origin, GUI is top-left) - retro rendering mode renders to a small offscreen texture
            // first, so screenPos itself is in THAT texture's own pixel space and needs normalizing
            // against it before rescaling to the real Screen.width/height. Adapted from the
            // Archaeologists mod's own LocatorDevice.cs (see file header) and cross-checked against
            // DFU's own HUDPlaceMarker.cs, which handles the same conversion for its own (debug-only,
            // exterior-only) quest site markers.
            Vector2 screenPos2d;
            if (DaggerfallUnity.Settings.RetroRenderingMode > 0)
            {
                float screenHeight = Screen.height;
                if (DaggerfallUI.Instance.DaggerfallHUD != null && DaggerfallUI.Instance.DaggerfallHUD.LargeHUD.Enabled
                    && DaggerfallUnity.Settings.LargeHUDDocked)
                    screenHeight = Screen.height - DaggerfallUI.Instance.DaggerfallHUD.LargeHUD.ScreenHeight;

                float xm = screenPos.x / mainCamera.targetTexture.width;
                float ym = screenPos.y / mainCamera.targetTexture.height;
                screenPos2d = new Vector2(Screen.width * xm, screenHeight - screenHeight * ym);
            }
            else
            {
                screenPos2d = new Vector2(screenPos.x, Screen.height - screenPos.y);
            }

            // Height drives the falloff/clamp range, width follows the texture's own real aspect ratio
            // (skull.png is 244x290, not square) so it's never stretched.
            float height = Mathf.Clamp(baseSizePixels / Mathf.Max(screenPos.z, minSizeDistance), minSizePixels, maxSizePixels);
            float width = height * markerTexture.width / markerTexture.height;

            GUI.depth = 2;
            GUI.DrawTexture(new Rect(screenPos2d.x - width / 2f, screenPos2d.y - height / 2f, width, height), markerTexture);
        }

        static Texture2D CreateDotTexture(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Vector2 center = new Vector2(size / 2f, size / 2f);
            float radius = size / 2f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    float alpha = Mathf.Clamp01(radius - distance);
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            return texture;
        }
    }
}
