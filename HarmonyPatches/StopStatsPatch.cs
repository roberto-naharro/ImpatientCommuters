using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.UI;
using HarmonyLib;
using ImpatientCommuters.Util;
using UnityEngine;

namespace ImpatientCommuters
{
    // Appends impatient-departure stats to each stop button in the
    // PublicTransportWorldInfoPanel (the line info panel showing all stops).
    [HarmonyPatch(typeof(PublicTransportWorldInfoPanel), "UpdateStopButtons")]
    static class StopStatsPatch
    {
        private const string ImpatientLabelName = "ImpatientCommuters_Count";
        private static readonly Color32 OrangeColor = new Color32(255, 140, 0, 255);

        static void Postfix(UITemplateList<UIButton> ___m_stopButtons, ushort lineID)
        {

            Log.DebugLog("StopStatsPatch.Postfix: lineID=" + lineID
                + " stopButtons=" + (___m_stopButtons == null ? "NULL" : ___m_stopButtons.items.Count.ToString()));

            if (___m_stopButtons == null)
                return;

            ushort stop = Singleton<TransportManager>.instance
                .m_lines.m_buffer[lineID].m_stops;

            foreach (UIComponent btn in ___m_stopButtons.items)
            {
                int   lastMin;
                float avg;
                GetStopStats(stop, out lastMin, out avg);

                Log.DebugLog("StopStatsPatch: stop=" + stop
                    + " lastMin=" + lastMin + " avg=" + avg.ToString("F1"));

                UILabel impatientLabel = btn.Find<UILabel>(ImpatientLabelName);

                bool hasData = lastMin > 0 || avg >= 0.5f;
                if (hasData)
                {
                    if (impatientLabel == null)
                        impatientLabel = CreateCountLabel(btn);

                    impatientLabel.text    = "(-" + lastMin + ")";
                    impatientLabel.tooltip = "Impatient departures last min: " + lastMin
                        + "\nRolling avg: " + avg.ToString("F1") + "/min";
                    impatientLabel.Show();
                }
                else if (impatientLabel != null)
                {
                    impatientLabel.Hide();
                }

                stop = TransportLine.GetNextStop(stop);
            }
        }

        private static UILabel CreateCountLabel(UIComponent btn)
        {
            UILabel label = btn.AddUIComponent<UILabel>();
            label.name       = ImpatientLabelName;
            label.textColor  = OrangeColor;
            label.textScale  = 0.75f;
            label.autoSize   = true;
            label.wordWrap   = false;

            // Stack below PassengerCount so the label stays inside the button bounds.
            UILabel countLabel = btn.Find<UILabel>("PassengerCount");
            if (countLabel != null)
                label.relativePosition = new Vector3(
                    countLabel.relativePosition.x,
                    countLabel.relativePosition.y + countLabel.height + 1f);
            else
                label.relativePosition = new Vector3(4f, btn.height - 14f);

            return label;
        }

        internal static void GetStopStats(ushort nodeId, out int lastMinCount, out float avgPerMin)
        {
            lastMinCount = 0;
            avgPerMin    = 0f;

            lock (CommuterImpatienceLimiter.StopLock)
            {
                List<int> times;
                if (!CommuterImpatienceLimiter.StopDepartures.TryGetValue(nodeId, out times)
                    || times.Count == 0)
                    return;

                int now    = System.Environment.TickCount;
                int count1 = 0;
                int count2 = 0;

                foreach (int t in times)
                {
                    int age = now - t;
                    if (age < 60000)  count1++;
                    if (age < 120000) count2++;
                }

                lastMinCount = count1;
                avgPerMin    = count2 / 2f;
            }
        }
    }
}
