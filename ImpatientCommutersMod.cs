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
        public string Description => "Overcrowded stops make waiting passengers more likely to leave and find another route. Probability grows with wait time, age, and trip purpose.";

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
            thresholdSlider.tooltip = "× average vehicle capacity at stop\n"
                + "0.5 = triggers at half capacity | 1.0 = full | 2.0 = double";

            general.AddCheckbox(
                "Scale by trip purpose (work/school = patient, tourists = restless)",
                s.DestinationFactorEnabled,
                v => { Settings.Instance.DestinationFactorEnabled = v; Settings.Save(); });

            // ── Age-based patience ───────────────────────────────────────────────
            var age = helper.AddGroup("Patience level by age group");
            age.AddDropdown(
                "Children",
                Settings.PatienceLabels,
                s.AgeFactorChild,
                v => { Settings.Instance.AgeFactorChild = v; Settings.Save(); });
            age.AddDropdown(
                "Teenagers",
                Settings.PatienceLabels,
                s.AgeFactorTeen,
                v => { Settings.Instance.AgeFactorTeen = v; Settings.Save(); });
            age.AddDropdown(
                "Young adults",
                Settings.PatienceLabels,
                s.AgeFactorYoungAdult,
                v => { Settings.Instance.AgeFactorYoungAdult = v; Settings.Save(); });
            age.AddDropdown(
                "Adults",
                Settings.PatienceLabels,
                s.AgeFactorAdult,
                v => { Settings.Instance.AgeFactorAdult = v; Settings.Save(); });
            age.AddDropdown(
                "Seniors",
                Settings.PatienceLabels,
                s.AgeFactorSenior,
                v => { Settings.Instance.AgeFactorSenior = v; Settings.Save(); });

            // ── Debug ────────────────────────────────────────────────────────────
            var dbg = helper.AddGroup("Debug");
            dbg.AddCheckbox(
                "Enable debug logging",
                Log.DebugEnabled,
                v => { Log.DebugEnabled = v; Log.Info("Debug logging " + (v ? "enabled" : "disabled")); });
        }

        // Adds a slider with a live value label to its right.
        // The value label is added to the group root panel (sibling of the slider container)
        // so it doesn't affect the slider panel's own auto-layout height.
        // Pattern from IPTE OptionsFramework (UIHelperBaseExtensions.cs).
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
