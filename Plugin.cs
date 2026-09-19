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
        public const string VERSION = "0.21.14";

        public static Plugin Instance { get; private set; }

        private Harmony _harmony;

        // TEMPORARY DIAGNOSTIC — remove alongside the matching one in
        // FireManager.TryIgnite once the server-authority question is settled.
        // ZNet.instance doesn't exist yet at Awake() (world not loaded), so this
        // polls in Update() and logs once as soon as it appears — giving a clear
        // "this peer is a server / this peer is a client" line near the top of
        // each connected peer's log, to correlate against TryIgnite call counts.
        private bool _authorityLogged;

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

        private void Update()
        {
            if (_authorityLogged || ZNet.instance == null) return;
            _authorityLogged = true;
            FireLogger.Info($"[AUTHORITY-CHECK] ZNet.instance ready — IsServer={ZNet.instance.IsServer()}, " +
                             $"peer={SystemInfo.deviceUniqueIdentifier}");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}