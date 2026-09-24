using BepInEx;
using FireFront.Config;
using FireFront.Fire;
using FireFront.Utils;
using HarmonyLib;
using UnityEngine;

namespace FireFront
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.raveniron.firefront";
        public const string NAME = "FireFront";
        public const string VERSION = "1.0.2";

        /// <summary>
        /// FireFront's wire compatibility number, exchanged at connect with <see cref="VERSION"/>
        /// (Fire/VersionCheck.cs). Two builds with the same number work together; two with
        /// different numbers do not, and both sides say so. Change it when, and only when, any
        /// FireFront RPC's name, argument list or payload layout changes. 1 = 0.24.0, the first
        /// build that sends it; anything older sends nothing.
        /// Still 1 at 1.0.2: the cell size it appends to the ground-fire sync comes after
        /// everything an older build reads, so each side still reads the other's packages whole.
        /// </summary>
        public const int WireProtocol = 1;

        public static Plugin Instance { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;

            FireLogger.Init(Logger);
            FireConfig.Bind(base.Config);

            // After Bind, so the config migration's own writes are not mistaken for an admin
            // moving a slider, and so Settable() resolves against entries that already exist.
            FireFront.Commands.FireDevCommands.HookLiveConfigSync(base.Config);

            // FireManager lives on the plugin GameObject and persists across scenes.
            gameObject.AddComponent<FireManager>();

            // A tree that streams in already charred skins on its spawn frame and would join the
            // char field build synchronously (review of the async fix, 2026-09-20). On a client
            // the build starts here, on a worker thread, and is long done before a world loads.
            // Headless there is no graphics device and nothing to skin, so nothing starts.
            if (FireVFXController.GraphicsAvailable) CharredTextures.Prewarm();

            _harmony = new Harmony(GUID);
            _harmony.PatchAll();

            FireLogger.Info($"{NAME} {VERSION} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            CharredTreeSkin.ReleaseAll();
        }
    }
}