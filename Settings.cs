using System;
using System.IO;
using System.Xml.Serialization;
using ColossalFramework;
using ColossalFramework.IO;
using UnityEngine;

namespace ImpatientCommuters
{
    [XmlRoot("ImpatientCommutersSettings")]
    public class Settings
    {
        private static Settings _instance;
        public static Settings Instance => _instance ?? (_instance = Load());

        // General
        public bool Enabled = true;
        public float ThresholdMultiplier = 1.0f;
        public bool DestinationFactorEnabled = true;

        // Per-age patience indices (0=Very Patient … 4=Very Impatient)
        public int AgeFactorChild      = 1; // Patient  (×0.7)
        public int AgeFactorTeen       = 2; // Normal   (×1.0)
        public int AgeFactorYoungAdult = 2; // Normal   (×1.0)
        public int AgeFactorAdult      = 2; // Normal   (×1.0)
        public int AgeFactorSenior     = 3; // Impatient (×1.3)

        public static readonly float[] PatienceMultipliers = { 0.5f, 0.7f, 1.0f, 1.3f, 1.6f };
        public static readonly string[] PatienceLabels =
        {
            "Very Patient (×0.5)",
            "Patient (×0.7)",
            "Normal (×1.0)",
            "Impatient (×1.3)",
            "Very Impatient (×1.6)"
        };

        public static float GetAgeFactor(Citizen.AgeGroup group)
        {
            Settings s = Instance;
            int idx;
            switch (group)
            {
                case Citizen.AgeGroup.Child:  idx = s.AgeFactorChild;      break;
                case Citizen.AgeGroup.Teen:   idx = s.AgeFactorTeen;       break;
                case Citizen.AgeGroup.Young:  idx = s.AgeFactorYoungAdult; break;
                case Citizen.AgeGroup.Adult:  idx = s.AgeFactorAdult;      break;
                case Citizen.AgeGroup.Senior: idx = s.AgeFactorSenior;     break;
                default:                      idx = 2;                       break;
            }
            return PatienceMultipliers[idx];
        }

        private static string SettingsPath =>
            Path.Combine(DataLocation.localApplicationData, "ImpatientCommuters.xml");

        private static Settings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    using (StreamReader r = new StreamReader(SettingsPath))
                        return (Settings)new XmlSerializer(typeof(Settings)).Deserialize(r);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            return new Settings();
        }

        public static void Save()
        {
            try
            {
                using (StreamWriter w = new StreamWriter(SettingsPath))
                    new XmlSerializer(typeof(Settings)).Serialize(w, Instance);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
