using System.Reflection;
using ColossalFramework.UI;
using HarmonyLib;
using ImpatientCommuters.Util;
using UnityEngine;

namespace ImpatientCommuters
{
    // Adds impatient-departure stats next to "Waiting passengers" in the stop info panel.
    // Patched dynamically at runtime because IPTE's PublicTransportStopWorldInfoPanel
    // extends UIPanel (not WorldInfoPanel) and lives in a different assembly.
    static class StopPanelStatsPatch
    {
        private const string LabelName = "ImpatientCommuters_StopStats";
        private static readonly Color32 OrangeColor = new Color32(255, 140, 0, 255);
        private static FieldInfo _instanceIdField;

        internal static void ApplyDynamic(Harmony harmony)
        {
            System.Type panelType = System.Type.GetType(
                "ImprovedPublicTransport2.UI.PublicTransportStopWorldInfoPanel, ImprovedPublicTransport2");

            if (panelType == null)
            {
                Log.Info("IPTE stop panel not found — stop panel stats skipped");
                return;
            }

            MethodInfo lateUpdate = panelType.GetMethod("LateUpdate",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (lateUpdate == null)
            {
                Log.Warning("IPTE stop panel LateUpdate not found");
                return;
            }

            _instanceIdField = panelType.GetField("m_InstanceID",
                BindingFlags.NonPublic | BindingFlags.Instance);

            harmony.Patch(lateUpdate, postfix: new HarmonyMethod(
                typeof(StopPanelStatsPatch),
                nameof(LateUpdatePostfix)));

            Log.Info("Patched IPTE stop panel LateUpdate");
        }

        // Postfix injected onto IPTE's PublicTransportStopWorldInfoPanel.LateUpdate.
        public static void LateUpdatePostfix(UIPanel __instance)
        {
            if (!__instance.isVisible)
                return;

            ushort nodeId = GetNodeId(__instance);
            if (nodeId == 0)
                return;

            UIPanel countPanel = __instance.Find<UIPanel>("PassengerCountPanel");
            if (countPanel == null)
                return;

            UILabel impatientLabel = countPanel.Find<UILabel>(LabelName);

            int   lastMin;
            float avg;
            StopStatsPatch.GetStopStats(nodeId, out lastMin, out avg);

            bool hasData = lastMin > 0 || avg >= 0.5f;
            if (hasData)
            {
                if (impatientLabel == null)
                    impatientLabel = CreateLabel(countPanel);

                impatientLabel.text    = " (-" + lastMin + ")";
                impatientLabel.tooltip = "Impatient departures last min: " + lastMin
                    + "\nRolling avg: " + avg.ToString("F1") + "/min";
                impatientLabel.Show();
            }
            else if (impatientLabel != null)
            {
                impatientLabel.Hide();
            }
        }

        private static ushort GetNodeId(UIPanel panel)
        {
            if (_instanceIdField == null)
                return 0;
            try
            {
                InstanceID id = (InstanceID)_instanceIdField.GetValue(panel);
                return id.NetNode;
            }
            catch
            {
                return 0;
            }
        }

        private static UILabel CreateLabel(UIPanel parent)
        {
            UILabel label = parent.AddUIComponent<UILabel>();
            label.name      = LabelName;
            label.textColor = OrangeColor;
            label.textScale = 13f / 16f;
            label.autoSize  = true;
            label.wordWrap  = false;
            label.height    = 15f;
            return label;
        }
    }
}
