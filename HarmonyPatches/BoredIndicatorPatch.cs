using ColossalFramework;
using ColossalFramework.UI;
using HarmonyLib;
using ImpatientCommuters.Util;
using UnityEngine;

namespace ImpatientCommuters
{
    // Postfixes WorldInfoPanel.UpdateBindings so it fires for every WorldInfoPanel subclass.
    // We filter to CitizenWorldInfoPanel at runtime.
    [HarmonyPatch(typeof(WorldInfoPanel), "UpdateBindings")]
    static class BoredIndicatorPatch
    {
        private const string LabelName = "ImpatientCommuters_BoredLabel";

        private static UILabel _label;

        static void Postfix(WorldInfoPanel __instance)
        {
            var panel = __instance as CitizenWorldInfoPanel;
            if (panel == null)
                return;

            EnsureLabel(panel);
            if (_label == null)
                return;

            InstanceID id        = WorldInfoPanel.GetCurrentInstanceID();
            uint       citizenId = id.Citizen;
            if (citizenId == 0) { _label.Hide(); return; }

            ushort instanceIdx = Singleton<CitizenManager>.instance
                .m_citizens.m_buffer[citizenId].m_instance;
            if (instanceIdx == 0) { _label.Hide(); return; }

            Log.DebugLog("BoredIndicator: panel size=" + panel.component.width + "×" + panel.component.height
                + " labelPos=" + _label.relativePosition
                + " instanceIdx=" + instanceIdx);

            lock (CommuterImpatienceLimiter.BoredLock)
            {
                int ts;
                if (CommuterImpatienceLimiter.BoredAt.TryGetValue((uint)instanceIdx, out ts))
                {
                    int elapsed = System.Environment.TickCount - ts;
                    if (elapsed >= 0 && elapsed < 60000)
                    {
                        _label.Show();
                        return;
                    }
                }
            }
            _label.Hide();
        }

        private static void EnsureLabel(CitizenWorldInfoPanel panel)
        {
            if (_label != null)
                return; // also catches Unity-destroyed via operator overload

            // Reuse if it somehow already exists (e.g. level reload with same panel).
            _label = panel.component.Find<UILabel>(LabelName);
            if (_label == null)
                _label = panel.component.AddUIComponent<UILabel>();

            _label.name      = LabelName;
            _label.text      = "Tired of waiting\nwill find another route";
            _label.textColor = new Color32(255, 165, 0, 255);
            _label.textScale = 0.85f;
            _label.autoSize  = true;
            _label.wordWrap  = false;
            _label.Hide();

            // Log the panel dimensions so we know where to place the label.
            float panelW = panel.component.width;
            float panelH = panel.component.height;
            Log.Info("BoredIndicator: CitizenWorldInfoPanel size=" + panelW + "×" + panelH);

            // Place label near the bottom of the panel (20px inside), then grow the panel to fit.
            float labelY = panelH - 16f;
            _label.relativePosition = new Vector3(14f, labelY);
            panel.component.height  = panelH + 45f;

            Log.Info("BoredIndicator: label placed at y=" + labelY
                + ", panel resized to " + panel.component.width + "×" + panel.component.height);
        }
    }
}
