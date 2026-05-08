using System.Reflection;
using CitiesHarmony.API;
using ColossalFramework.UI;
using HarmonyLib;
using ICities;
using ImpatientCommuters.Util;

namespace ImpatientCommuters
{
    public class ImpatientCommutersMod : IUserMod
    {
        private const string HarmonyId = "com.roberto.impatientcommuters";

        public string Name        => "Impatient Commuters";
        public string Description => "Overcrowded stops make waiting passengers more likely to leave and find another route. Probability peaks on arrival, fades as sunk wait time builds, then rises again with frustration — shaped by age, trip purpose, and line frequency.";

        public void OnEnabled()
        {
            HarmonyHelper.EnsureHarmonyInstalled();
            HarmonyHelper.DoOnHarmonyReady(() =>
            {
                try
                {
                    var harmony = new Harmony(HarmonyId);
                    harmony.PatchAll(Assembly.GetExecutingAssembly());
                    Log.Info("Harmony patches applied");
                    StopPanelStatsPatch.ApplyDynamic(harmony);
                }
                catch (System.Exception ex)
                {
                    Log.Error("PatchAll failed: " + ex);
                }
            });
        }

        public void OnDisabled()
        {
            if (HarmonyHelper.IsHarmonyInstalled)
            {
                new Harmony(HarmonyId).UnpatchAll(HarmonyId);
                Log.Info("Harmony patches removed");
            }
        }

        public void OnSettingsUI(UIHelperBase helper)
        {
            Settings s = Settings.Instance;

            // ── General ──────────────────────────────────────────────────────────
            var general = helper.AddGroup("Impatient Commuters — General");

            general.AddCheckbox(
                "Enable mod",
                s.Enabled,
                v => { Settings.Instance.Enabled = v; Settings.Save(); Log.Info("Enabled=" + v); });

            UILabel thresholdValue = null;
            UISlider thresholdSlider = AddSlider(
                general,
                "Capacity threshold",
                0.5f, 2.0f, 0.1f,
                s.ThresholdMultiplier,
                v =>
                {
                    Settings.Instance.ThresholdMultiplier = v;
                    Settings.Save();
                    if (thresholdValue != null)
                        thresholdValue.text = v.ToString("F1") + "×";
                },
                out thresholdValue);
            thresholdSlider.tooltip =
                "Multiplier on the average passenger capacity of vehicles serving the stop.\n"
                + "The mod only activates once this threshold is exceeded.\n"
                + "0.5 = triggers at half capacity   |   1.0 = full capacity (default)   |   2.0 = double";

            var destCheck = (UIComponent)general.AddCheckbox(
                "Scale by trip purpose",
                s.DestinationFactorEnabled,
                v => { Settings.Instance.DestinationFactorEnabled = v; Settings.Save(); });
            destCheck.tooltip =
                "Applies separate multipliers for balking (on-arrival) and frustration (long-wait).\n"
                + "Work: balks more (×1.2) — commuters know alternatives — but abandons less over time (×0.4).\n"
                + "Tourists: balks less (×0.7) — unfamiliar with the network — but grows restless faster (×1.3).";

            // ── Behaviour components ─────────────────────────────────────────────
            var behaviour = helper.AddGroup("Behaviour components");

            var balkCheck = (UIComponent)behaviour.AddCheckbox(
                "Balking — crowd deterrence on arrival",
                s.BalkingEnabled,
                v => { Settings.Instance.BalkingEnabled = v; Settings.Save(); });
            balkCheck.tooltip =
                "Citizens who just arrived at a heavily overcrowded stop have a high immediate chance\n"
                + "of turning around. The longer they have already waited, the less likely they are to leave —\n"
                + "nobody wants to abandon a stop after standing there for five minutes.";

            var freqCheck = (UIComponent)behaviour.AddCheckbox(
                "Frequency scaling — shorter headways reduce impatience",
                s.FrequencyScalingEnabled,
                v => { Settings.Instance.FrequencyScalingEnabled = v; Settings.Save(); });
            freqCheck.tooltip =
                "Headway depends on both the number of vehicles AND the length of the line.\n"
                + "A single bus covering 3 stops comes back quickly; the same bus on 100 stops barely returns.\n"
                + "Factor: √(stops ÷ vehicles ÷ 5).   10 stops / 2 vehicles = ×1.0 (baseline)\n"
                + "6 stops / 4 vehicles (metro) ≈ ×0.55   |   100 stops / 1 vehicle = ×3.0 (max)";

            var altCheck = (UIComponent)behaviour.AddCheckbox(
                "Alternative line bonus — nearby stop raises willingness to leave",
                s.AlternativeLineBonusEnabled,
                v => { Settings.Instance.AlternativeLineBonusEnabled = v; Settings.Save(); });
            altCheck.tooltip =
                "If another transport line has a stop within 150 m, each waiting citizen gets\n"
                + "a ×1.15 multiplier on their total probability.\n"
                + "Knowing a viable alternative is nearby makes rerouting more appealing.";

            // ── Patience by age group — frustration ──────────────────────────────
            var frustGroup = helper.AddGroup("How long each age group waits before giving up");

            var ddFrustChild = (UIDropDown)frustGroup.AddDropdown(
                "Children",
                Settings.PatienceLabels, s.AgeFactorChild,
                v => { Settings.Instance.AgeFactorChild = v; Settings.Save(); });
            ddFrustChild.tooltip = "Children tend to wait quietly and follow the adults around them.";


            var ddFrustTeen = (UIDropDown)frustGroup.AddDropdown(
                "Teenagers",
                Settings.PatienceLabels, s.AgeFactorTeen,
                v => { Settings.Instance.AgeFactorTeen = v; Settings.Save(); });
            ddFrustTeen.tooltip = "Teenagers can be impulsive but are often willing to wait once committed.";

            var ddFrustYoung = (UIDropDown)frustGroup.AddDropdown(
                "Young adults",
                Settings.PatienceLabels, s.AgeFactorYoungAdult,
                v => { Settings.Instance.AgeFactorYoungAdult = v; Settings.Save(); });
            ddFrustYoung.tooltip = "Young adults are familiar with public transport and generally accept reasonable waits.";

            var ddFrustAdult = (UIDropDown)frustGroup.AddDropdown(
                "Adults",
                Settings.PatienceLabels, s.AgeFactorAdult,
                v => { Settings.Instance.AgeFactorAdult = v; Settings.Save(); });
            ddFrustAdult.tooltip = "Experienced commuters with predictable routines and moderate patience.";

            var ddFrustSenior = (UIDropDown)frustGroup.AddDropdown(
                "Seniors",
                Settings.PatienceLabels, s.AgeFactorSenior,
                v => { Settings.Instance.AgeFactorSenior = v; Settings.Save(); });
            ddFrustSenior.tooltip = "Seniors are less comfortable standing in a crowd for long periods.";

            frustGroup.AddButton("Restore defaults", () =>
            {
                int[] d = Settings.FrustrationDefaults;
                Settings.Instance.AgeFactorChild      = d[0];
                Settings.Instance.AgeFactorTeen       = d[1];
                Settings.Instance.AgeFactorYoungAdult = d[2];
                Settings.Instance.AgeFactorAdult      = d[3];
                Settings.Instance.AgeFactorSenior     = d[4];
                ddFrustChild.selectedIndex  = d[0];
                ddFrustTeen.selectedIndex   = d[1];
                ddFrustYoung.selectedIndex  = d[2];
                ddFrustAdult.selectedIndex  = d[3];
                ddFrustSenior.selectedIndex = d[4];
                Settings.Save();
            });

            // ── Crowding sensitivity by age group — balking ──────────────────────
            var balkGroup = helper.AddGroup("How likely each age group is to leave immediately on seeing a crowded stop");

            var ddBalkChild = (UIDropDown)balkGroup.AddDropdown(
                "Children",
                Settings.BalkLabels, s.AgeBalkChild,
                v => { Settings.Instance.AgeBalkChild = v; Settings.Save(); });
            ddBalkChild.tooltip =
                "Children have low agency and rarely reroute independently. Default: Very Low (×0.5).";

            var ddBalkTeen = (UIDropDown)balkGroup.AddDropdown(
                "Teenagers",
                Settings.BalkLabels, s.AgeBalkTeen,
                v => { Settings.Instance.AgeBalkTeen = v; Settings.Save(); });
            ddBalkTeen.tooltip =
                "Teenagers are the most impulsive and most likely to check their phone for alternatives. Default: High (×1.3).";

            var ddBalkYoung = (UIDropDown)balkGroup.AddDropdown(
                "Young adults",
                Settings.BalkLabels, s.AgeBalkYoungAdult,
                v => { Settings.Instance.AgeBalkYoungAdult = v; Settings.Save(); });
            ddBalkYoung.tooltip =
                "Know alternatives but respond more deliberately than teenagers. Default: Normal (×1.0).";

            var ddBalkAdult = (UIDropDown)balkGroup.AddDropdown(
                "Adults",
                Settings.BalkLabels, s.AgeBalkAdult,
                v => { Settings.Instance.AgeBalkAdult = v; Settings.Save(); });
            ddBalkAdult.tooltip =
                "Experienced commuters with a controlled immediate response to crowding. Default: Normal (×1.0).";

            var ddBalkSenior = (UIDropDown)balkGroup.AddDropdown(
                "Seniors",
                Settings.BalkLabels, s.AgeBalkSenior,
                v => { Settings.Instance.AgeBalkSenior = v; Settings.Save(); });
            ddBalkSenior.tooltip =
                "Highest crowding sensitivity due to physical discomfort and low load tolerance. Default: High (×1.3).";

            balkGroup.AddButton("Restore defaults", () =>
            {
                int[] d = Settings.BalkDefaults;
                Settings.Instance.AgeBalkChild      = d[0];
                Settings.Instance.AgeBalkTeen       = d[1];
                Settings.Instance.AgeBalkYoungAdult = d[2];
                Settings.Instance.AgeBalkAdult      = d[3];
                Settings.Instance.AgeBalkSenior     = d[4];
                ddBalkChild.selectedIndex  = d[0];
                ddBalkTeen.selectedIndex   = d[1];
                ddBalkYoung.selectedIndex  = d[2];
                ddBalkAdult.selectedIndex  = d[3];
                ddBalkSenior.selectedIndex = d[4];
                Settings.Save();
            });

            // ── Debug ────────────────────────────────────────────────────────────
            var dbg = helper.AddGroup("Debug");
            dbg.AddCheckbox(
                "Enable debug logging",
                Log.DebugEnabled,
                v => { Log.DebugEnabled = v; Log.Info("Debug logging " + (v ? "enabled" : "disabled")); });
        }

        // Adds a slider with a live value label to its right.
        private static UISlider AddSlider(
            UIHelperBase group,
            string text,
            float min, float max, float step, float defaultValue,
            OnValueChanged callback,
            out UILabel valueLabel)
        {
            UIComponent root = null;
            var uiHelper = group as UIHelper;
            if (uiHelper != null)
            {
                var field = typeof(UIHelper).GetField("m_Root", BindingFlags.NonPublic | BindingFlags.Instance);
                root = field?.GetValue(uiHelper) as UIComponent;
            }

            valueLabel = root?.AddUIComponent<UILabel>();

            var slider = (UISlider)group.AddSlider(text, min, max, step, defaultValue, callback);

            UILabel nameLabel = slider.parent?.Find<UILabel>("Label");
            if (nameLabel != null)
                nameLabel.width = nameLabel.textScale * nameLabel.font.size * nameLabel.text.Length;

            if (valueLabel != null)
            {
                valueLabel.text          = defaultValue.ToString("F1") + "×";
                valueLabel.textScale     = 0.85f;
                valueLabel.AlignTo(slider, UIAlignAnchor.TopLeft);
                valueLabel.relativePosition = new UnityEngine.Vector3(slider.width + 8f, 0f, 0f);
            }

            return slider;
        }
    }
}
