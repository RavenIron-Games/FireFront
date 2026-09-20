using System.Collections.Generic;
using FireFront.Config;
using FireFront.Utils;
using HarmonyLib;
using UnityEngine;

namespace FireFront.Fire
{
    /// <summary>
    /// Lives on every peer's instance of a charred tree or log (attached from the TreeBase /
    /// TreeLog Awake postfix below whenever the ZDO carries the charred flag). Draws the char,
    /// fades the embers, and — on the peer that owns the object — executes the fate the server
    /// baked into the ZDO when its time comes.
    /// </summary>
    public class CharredTreeController : MonoBehaviour
    {
        private ZNetView _nview;
        private TreeBase _tree;
        private TreeLog _log;
        private List<Renderer> _renderers;
        private CharredSmoke _smoke;
        private int _emberVariant;
        private float _noiseSeed;
        private float _nextCheck;
        private bool _done;
        private bool _skinned;
        private bool _smokeTried;

        private const float CheckInterval = 0.25f;

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            _tree = GetComponent<TreeBase>();
            _log = GetComponent<TreeLog>();
            _noiseSeed = Random.Range(0f, 100f);
        }

        private void Start()
        {
            TrySkin();
        }

        private void TrySkin()
        {
            if (_skinned) return;
            if (_nview == null || !_nview.IsValid()) return;
            _skinned = true;
            if (!FireVFXController.GraphicsAvailable) return; // headless: the fate logic below still runs, the look does not
            try
            {
                _emberVariant = CharredTextures.VariantFor(_nview.GetZDO().m_uid);
                _renderers = CharredTreeSkin.Apply(gameObject, CurrentEmber(), _emberVariant);
                // m_text is a localisation token ("$prop_beech"); Localize replaces tokens inside
                // a longer string, so this reads "Charred Beech" in whatever language is set.
                HoverText hover = GetComponent<HoverText>();
                if (hover != null && !string.IsNullOrEmpty(hover.m_text) && !hover.m_text.StartsWith("Charred"))
                {
                    hover.m_text = "Charred " + hover.m_text;
                }
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"[CHARRED] skinning {name} threw: {ex.Message}");
            }
            TrySmoke();
        }

        /// <summary>Post-fire smoke, once, if the object is still young enough to be smoking.</summary>
        private void TrySmoke()
        {
            if (_smokeTried || _nview == null || !_nview.IsValid()) return;
            _smokeTried = true;
            try
            {
                _smoke = CharredSmoke.TryAttach(gameObject, _log != null, CharredAge());
            }
            catch (System.Exception ex)
            {
                FireLogger.Debug($"[CHARRED] smoke on {name} threw: {ex.Message}");
            }
        }

        private float CharredAge()
        {
            return CharredTreeLifecycle.SecondsSince(_nview.GetZDO().GetLong(CharredTreeLifecycle.CharredAtHash, CharredTreeLifecycle.NowTicks));
        }

        private Color CurrentEmber()
        {
            if (_nview == null || !_nview.IsValid()) return Color.black;
            long at = _nview.GetZDO().GetLong(CharredTreeLifecycle.CharredAtHash, CharredTreeLifecycle.NowTicks);
            Color c = CharredTreeSkin.EmberAt(CharredTreeLifecycle.SecondsSince(at), FireConfig.CharredEmberGlowSeconds.Value, _noiseSeed);
            // Distance: the mask averages toward its mean past the last mip, and a mean glow on a
            // whole trunk is the neon rod the first in-game run saw. Full to 30 m, a third at 90 m.
            Camera cam = global::Utils.GetMainCamera();
            if (cam != null)
            {
                float d = Vector3.Distance(cam.transform.position, transform.position);
                c *= Mathf.Lerp(1f, 0.3f, Mathf.Clamp01((d - 30f) / 60f));
            }
            return c;
        }

        private void Update()
        {
            if (_done) return;
            if (Time.time < _nextCheck) return;
            _nextCheck = Time.time + CheckInterval;

            if (_nview == null || !_nview.IsValid()) return;
            if (!_skinned) TrySkin();

            // Cosmetic first, and independent of ownership: every peer fades its own view.
            if (_renderers != null && FireConfig.CharredEmberGlowSeconds.Value > 0f)
            {
                float age = CharredAge();
                if (age <= FireConfig.CharredEmberGlowSeconds.Value + 1f) CharredTreeSkin.SetEmber(_renderers, CurrentEmber(), _emberVariant);
            }
            if (_smoke != null) _smoke.Tick(CharredAge());

            if (!_nview.IsOwner()) return;
            ZDO zdo = _nview.GetZDO();
            long now = CharredTreeLifecycle.NowTicks;

            if (_tree != null)
            {
                int fate = zdo.GetInt(CharredTreeLifecycle.FateHash, CharredTreeLifecycle.FateUndecided);
                if (fate == CharredTreeLifecycle.FateCollapse && now >= zdo.GetLong(CharredTreeLifecycle.CollapseAtHash, 0L))
                {
                    _done = true;
                    Vector3 dir = zdo.GetVec3(CharredTreeLifecycle.FallDirHash, Vector3.zero);
                    CharredTreeLifecycle.CollapseStandingTree(_tree, _nview, dir);
                }
            }
            else if (_log != null)
            {
                long crumbleAt = zdo.GetLong(CharredTreeLifecycle.CrumbleAtHash, 0L);
                int fate = zdo.GetInt(CharredTreeLifecycle.FateHash, CharredTreeLifecycle.FateUndecided);
                // A log that burned down in place honours the stand/collapse roll like a tree
                // would; a log that came from a collapsing tree carries FateStand and only the
                // crumble timer (its fall was the fate).
                bool due = crumbleAt != 0L && now >= crumbleAt;
                bool allowed = fate != CharredTreeLifecycle.FateStand || zdo.GetBool(CharredTreeLifecycle.CoalDroppedHash, false);
                if (due && allowed)
                {
                    _done = true;
                    CharredTreeLifecycle.CrumbleLog(_log, _nview);
                }
            }
        }

        /// <summary>
        /// A player chop killed this charred object (the RPC_Damage prefix calls this on the
        /// owner instead of vanilla's death branch): same exit as the fate collapse.
        /// </summary>
        public void OnChoppedDown(Vector3 hitDir)
        {
            if (_done) return;
            _done = true;
            if (_tree != null) CharredTreeLifecycle.CollapseStandingTree(_tree, _nview, hitDir);
            else if (_log != null) CharredTreeLifecycle.CrumbleLog(_log, _nview);
        }

        public static CharredTreeController Ensure(GameObject go)
        {
            if (go == null) return null;
            CharredTreeController c = go.GetComponent<CharredTreeController>();
            if (c == null) c = go.AddComponent<CharredTreeController>();
            return c;
        }
    }

    /// <summary>
    /// Attach the controller wherever a charred ZDO comes up as an instance. ZNetView has
    /// script execution order -20, so the ZDO is already on the view when TreeBase/TreeLog
    /// Awake — and so when this postfix — runs, on every peer, including for objects created
    /// from a pre-baked ZDO (ZNetView.m_initZDO). A flag written AFTER Instantiate would be
    /// missed here; the lifecycle never does that.
    /// </summary>
    [HarmonyPatch(typeof(TreeBase), "Awake")]
    public static class TreeBase_Awake_CharredPatch
    {
        public static void Postfix(TreeBase __instance)
        {
            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (CharredTreeLifecycle.IsCharred(nv)) CharredTreeController.Ensure(__instance.gameObject);
            else FireManager.Instance?.OnBurnableInstantiated(__instance);
        }
    }

    [HarmonyPatch(typeof(WearNTear), "Awake")]
    public static class WearNTear_Awake_FirePatch
    {
        public static void Postfix(WearNTear __instance)
        {
            FireManager.Instance?.OnBurnableInstantiated(__instance);
        }
    }

    [HarmonyPatch(typeof(TreeLog), "Awake")]
    public static class TreeLog_Awake_CharredPatch
    {
        public static void Postfix(TreeLog __instance)
        {
            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (CharredTreeLifecycle.IsCharred(nv)) CharredTreeController.Ensure(__instance.gameObject);
            else FireManager.Instance?.OnBurnableInstantiated(__instance);
        }
    }
}
