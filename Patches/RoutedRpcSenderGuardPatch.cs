using FireFront.Utils;
using HarmonyLib;

namespace FireFront.Patches
{
    /// <summary>
    /// Server side: a routed package for one of FireFront's methods, or for vanilla's
    /// RPC_Damage, is dropped when the sender it claims is not the peer whose connection it
    /// arrived on. Why that matters and what is in scope is with
    /// <see cref="ValheimBridge.ValidateRoutedSender"/>. This runs for every routed package the
    /// server receives, reads five header fields and rewinds; a package that passes is untouched.
    ///
    /// Anything unexpected passes the package through: the guard is a filter in front of vanilla,
    /// never a reason for vanilla traffic to stop. A throw is logged once.
    ///
    /// Signature verified: ZRoutedRpc.RPC_RoutedRPC(ZRpc rpc, ZPackage pkg) (private; 1.0.15)
    /// </summary>
    [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
    public static class RoutedRpcSenderGuardPatch
    {
        private static bool _threwLogged;

        [HarmonyPrefix]
        public static bool Prefix(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                return ValheimBridge.ValidateRoutedSender(rpc, pkg);
            }
            catch (System.Exception ex)
            {
                if (!_threwLogged)
                {
                    _threwLogged = true;
                    FireLogger.Warn($"{AuthLog.Prefix} sender guard threw and passed the package through ({ex.GetType().Name}: {ex.Message}); not logged again.");
                }
                return true;
            }
        }
    }
}
