using UnityEngine;

namespace PilotHeim
{
    /// <summary>
    /// Networked objects must leave through ZNetScene: ZNetView.OnDestroy does not unregister itself,
    /// so a plain Object.Destroy leaves a dead entry that makes ZNetScene.RemoveObjects throw every
    /// frame once the player moves away from that zone.
    /// </summary>
    internal static class Net
    {
        public static void Destroy(GameObject go)
        {
            if (go == null) return;
            var nv = go.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid() && ZNetScene.instance != null) ZNetScene.instance.Destroy(go);
            else Object.Destroy(go);
        }
    }
}
