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
        public const string VERSION = "0.21.16";

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

            _harmony = new Harmony(GUID);
            _harmony.PatchAll();

            FireLogger.Info($"{NAME} {VERSION} loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}