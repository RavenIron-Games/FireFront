using System.Collections.Generic;
using FireFront.Config;
using HarmonyLib;
using UnityEngine;

namespace FireFront.Fire
{
    public class BurntTreeManager : MonoBehaviour
    {
        private TreeBase m_treeBase;
        private TreeLog m_treeLog;

        public void Awake()
        {
            m_treeBase = GetComponent<TreeBase>();
            m_treeLog = GetComponent<TreeLog>();

            ApplyBurntShader();
            OverrideDropTable();
        }

        private void ApplyBurntShader()
        {
            var renderers = GetComponentsInChildren<MeshRenderer>();
            foreach (var r in renderers)
            {
                foreach (var mat in r.materials)
                {
                    if (mat.HasProperty("_Color"))
                        mat.SetColor("_Color", new Color(0.1f, 0.1f, 0.1f, 1f));
                    if (mat.HasProperty("_EmissionColor"))
                        mat.SetColor("_EmissionColor", Color.black);
                }
            }
        }

        private void OverrideDropTable()
        {
            GameObject coalPrefab = ZNetScene.instance.GetPrefab("Coal");
            if (coalPrefab == null) return;

            DropTable dt = m_treeBase != null ? m_treeBase.m_dropWhenDestroyed : m_treeLog.m_dropWhenDestroyed;
            if (dt == null) return;
            
            // Only drop a small amount of coal
            dt.m_drops.Clear();
            dt.m_drops.Add(new DropTable.DropData { m_item = coalPrefab, m_weight = 1f, m_stackMin = 1, m_stackMax = 2 });
            dt.m_dropMin = 1;
            dt.m_dropMax = 2;
        }

        public static void CloneToBurntTree(Component target, string prefabName, Vector3 position, Quaternion rotation)
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null) return;

            GameObject burntClone = Instantiate(prefab, position, rotation);
            ZNetView nv = burntClone.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid())
            {
                nv.GetZDO().Set("FireFront_IsBurnt", true);
            }
        }
    }

    [HarmonyPatch(typeof(TreeBase), "Awake")]
    public static class TreeBase_Awake_Patch
    {
        public static void Postfix(TreeBase __instance)
        {
            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid() && nv.GetZDO().GetBool("FireFront_IsBurnt", false))
            {
                if (__instance.gameObject.GetComponent<BurntTreeManager>() == null)
                {
                    __instance.gameObject.AddComponent<BurntTreeManager>();
                }
            }
        }
    }

    [HarmonyPatch(typeof(TreeLog), "Awake")]
    public static class TreeLog_Awake_Patch
    {
        public static void Postfix(TreeLog __instance)
        {
            ZNetView nv = __instance.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid() && nv.GetZDO().GetBool("FireFront_IsBurnt", false))
            {
                if (__instance.gameObject.GetComponent<BurntTreeManager>() == null)
                {
                    __instance.gameObject.AddComponent<BurntTreeManager>();
                }
            }
        }
    }
}
