/*++

Copyright (c) 2022 Microsoft Corporation

Module Name:

    PPMCheckerTool.cs

Abstract:

    What does this tool do?

    External executable tool that checks for less optimal PPM settings against an Good settings XML. Also, checks for inversions in settings values across profiles


Author:

    Mark Bellon (mabellon) 1-Nov-2017
    Sidharth Venkatesh (sivenkatesh) 19-Dec-2022
    Zied Ben Hamouda (zbenhamouda) 19-Dec-2022

--*/
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Events;
using Microsoft.Windows.EventTracing.Metadata;
using Common;
using System.Reflection;
using System.Collections;
using System.Collections.Specialized;
using System.Xml;

namespace PPMCheckerTool
{
    class PPMCheckerTool : BaseAnalyzer
    {
        // Default Overlay Scheme
        public const String DefaultOverlayScheme = "Default Overlay Scheme";

        // Files
        private const String GUIDFriendlyNameFile = "GuidToFriendlyName.csv";
        private const String PPMSettingRulesXMLFile = "PPMSettingRules.xml";


        // Overlay power schemes GUIDs
        public static Guid GuidDefaultOverlay = new Guid("00000000-0000-0000-0000-000000000000");
        public static Guid GuidBetterBatteryOverlay = new Guid("961CC777-2547-4F9D-8174-7D86181b8A7A");
        public static Guid GuidBetterPerfOverlay = new Guid("381B4222-F694-41F0-9685-FF5BB260DF2E");
        public static Guid GuidBestPerfOverlay = new Guid("DED574B5-45A0-4F42-8737-46345C09C238");
        public static Guid GuidSurfaceBetterPerfOverlay = new Guid("3af9b8d9-7c97-431d-ad78-34a8bfea439f");


        // PPM settings GUIDs
        public static Guid GuidEPP = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        public static Guid GuidFrequencyCap = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
        public static Guid GuidSchedulingPolicy = new Guid("93b8b6dc-0698-4d1c-9ee4-0644e900c85d");
        public static Guid GuidShortSchedulingPolicy = new Guid("bae08b81-2d5e-4688-ad6a-13243356654b");
        public static Guid GuidCPMinCores = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");
        public static Guid GuidCPMaxCores = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334028");

        // Power profiles GUIDs
        public static Guid GuidDefault = new Guid("00000000-0000-0000-0000-000000000000");
        public static Guid GuidSustainedPerf = new Guid("0aabb002-a307-447e-9b81-1d819df6c6d0");
        public static Guid GuidMultimediaQos = new Guid("0c3d5326-944b-4aab-8ad8-fe422a0e50e0");
        public static Guid GuidLowLatency = new Guid("0da965dc-8fcf-4c0b-8efe-8dd5e7bc959a");
        public static Guid GuidScreenOff = new Guid("2e92e666-c3f6-42c3-89bd-94d40fabcde5");
        public static Guid GuidLowQoS = new Guid("c04a802d-2205-4910-ae98-3b51e3bb72f2");
        public static Guid GuidEcoQoS = new Guid("336c7511-f109-4172-bb3a-3ea51f815ada");
        public static Guid GuidUtilityQoS = new Guid("33cc3a0d-45ee-43ca-86c4-695bfc9a313b");
        public static Guid GuidConstrained = new Guid("ee1e4f72-e368-46b1-b3c6-5048b11c2dbd");
        public static Guid GuidStandby = new Guid("8bc6262c-c026-411d-ae3b-7e2f70811a13");
        public static Guid GuidLowPower = new Guid("4569e601-272e-4869-bcab-1c6c03d7966f");
        public static Guid GuidEntryLevelPerf = new Guid("a4a61b5f-f42c-4d23-b3ab-5c27df9f0f18");
        public static Guid GuidGameMode = new Guid("d4140c81-ebba-4e60-8561-6918290359cd");

        // Profile hierarchy table
        // The profiles are ordered by inheritance E.g., LowQoS inherits from MedQoS ==> MedQoS order is lower than LowQoS order 
        // Each element is a pair (profile GUID, parent profile GUID)
        static public OrderedDictionary OrderedProfileHierarchy = new OrderedDictionary()
        {
            { GuidDefault, GuidDefault },
            { GuidEntryLevelPerf,  GuidDefault },
            { GuidLowQoS,  GuidEntryLevelPerf },
            { GuidUtilityQoS,  GuidLowQoS },
            { GuidEcoQoS,  GuidUtilityQoS },
            { GuidMultimediaQos,  GuidDefault },
            { GuidSustainedPerf,  GuidDefault },
            { GuidScreenOff,  GuidDefault },
            { GuidConstrained,  GuidDefault },
            { GuidStandby,  GuidDefault },
            { GuidLowPower,  GuidDefault },
            { GuidGameMode,  GuidDefault },
        };

        // Validation rules 
        // Maps ProfileGuid --> (SettingGuid, AC MinValue, AC MaxValue, DC MinValue, DC MaxValue)
        static public Dictionary <Guid, List<Tuple <Guid, uint? , uint?, uint?, uint? >>> ValidationRules 
            = new Dictionary<Guid, List<Tuple<Guid, uint?, uint?, uint?, uint? >>>();

        // Class for settings values
        public class SettingValues
        {
            public uint? DC;
            public uint? AC;
            public string? altitudeDC;
            public string? altitudeAC;
        }

        // Legacy to FriendlyNames for Qos levels
        static public Dictionary<string, string> QoSLevelNames = new Dictionary<string, string>
        {
            {"Default", "HighQoS"},
            {"EntryLevelPerf", "MediumQoS"},
            {"Background", "LowQoS"},
            {"UtilityQos", "UtilityQoS"},
            {"EcoQos", "EcoQoS"},
            {"MultimediaQos", "MultimediaQoS"}
        };

        /// <summary>
        /// Writes the command line usage, arguments and examples to the console
        /// </summary>
        static void OutputCommandLineUsage()
        {
            Console.Out.WriteLine("Usage: " + System.AppDomain.CurrentDomain.FriendlyName + " -i <trace.etl>  -o <output.csv>  [-start <StartTime(ns)>]  [-stop <StopTime(ns)>]  [-noHeader]");
        }

        /// <summary>
        /// Main entry point, handles argument parsing
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        static int Main(string[] args)
        {
            try
            {
                if (GetSwitch(args, "-?"))
                {
                    OutputCommandLineUsage();
                    return 0;
                }

                var inputFile = GetRequiredArgument(args, "-i");
                var outputFile = GetRequiredArgument(args, "-o");
                bool noHeader = GetSwitch(args, "-noHeader");

                var startTime = GetArgument(args, "-start");
                Timestamp start = (startTime != null) ? Timestamp.FromSeconds(decimal.Parse(startTime)) : Timestamp.Zero;

                var stopTime = GetArgument(args, "-stop");
                Timestamp stop = (stopTime != null) ? Timestamp.FromSeconds(decimal.Parse(stopTime)) : Timestamp.MaxValue;

                // Handle invalid arguments
                if (inputFile != null && !File.Exists(inputFile))
                {
                    Console.Error.WriteLine("Failed to access input file. Please provide path to valid ETL.");
                    return -1;
                }

                // Process the input ETL trace
                AnalyzeTrace(inputFile, outputFile, start, stop, noHeader);

            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                OutputCommandLineUsage();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.ToString());
            }

            return 0;
        }

        /// <summary>
        /// Cracks the trace, loads the required datasources, validates the necessary data is present for analysis, constructs 
        /// enumerator of events to process, analyzes relevent time spans and outputs results to csv file.
        /// </summary>
        /// <param name="tracePath"></param>
        /// <param name="outputPath"></param>
        /// <param name="startTime"></param>
        /// <param name="stopTime"></param>
        /// <param name="noHeader"></param>
        static void AnalyzeTrace(string tracePath, string outputPath, Timestamp startTime, Timestamp stopTime, bool noHeader)
        {
            using (ITraceProcessor trace = TraceProcessor.Create(tracePath, true, true))
            {
                // Enable DataSources
                IPendingResult<ISystemMetadata> systemMetadata = trace.Enable(TraceDataSource.SystemMetadata);
                IPendingResult<IGenericEventDataSource> genericEventDataSource = trace.Enable(TraceDataSource.GenericEvents);
                
                // Process Data Sources
                trace.Process();

                // Validate that the necessary data sources are present in order to continue analysis
                if (!systemMetadata.HasResult ||
                   (!genericEventDataSource.HasResult || genericEventDataSource.Result.Events.Count == 0) ||
                   (!genericEventDataSource.Result.Events.Any(x => x.ProviderId.Equals(GUIDS.Microsoft_Windows_Kernel_Processor_Power))) ||
                   (!genericEventDataSource.Result.Events.Any(x => x.ProviderId.Equals(GUIDS.Microsoft_Windows_UserModePowerService))))
                {
                    throw new Exception("No metadata or kernel-power or usermode-power events");
                }

                // Final restults string into the csv file
                List<String> results = new List<String>();

                // Powerscheme guids
                Guid RundownPowerScheme = Guid.Empty;
                Guid RundownEffectiveOverlayPowerScheme = Guid.Empty;
                String RundownPowerSchemeString = String.Empty;
                String RundownEffectiveOverlayPowerSchemeString = String.Empty;
                //String RundownEffectiveOverlayPowerSchemeString = String.Empty;

                string QoSLevelName = "";
                // Extract System Metadata, or other static rundown information that may be worth logging
                // OEM Model cannot be null
                string oemModel = systemMetadata.Result.Model;
                string oemName = systemMetadata.Result.Manufacturer;

                string processorModel = "";
                if (systemMetadata.Result.Processors.Count > 0)
                {
                    processorModel = systemMetadata.Result.Processors[0].Name;
                }
                uint numCores = systemMetadata.Result.CpuCount;

                // Add metadata to results
                results.Add("OEMModel: " + oemModel);
                results.Add("OEMName: " + oemName);
                results.Add("ProcessorModel: " + processorModel);
                results.Add("Num Cores: " + numCores);

                // Maps PPM Setting --> Profile --> (AC value , DC value)
                Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>> powSettings = new Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>>();

                // Maps ProfileID --> (Name , Name, Guid)
                Dictionary<uint, Tuple<string, string, Guid>> profileNames = new Dictionary<uint, Tuple<string, string, Guid>>();

                // Local CSV generated by dumping powercfg caches a mapping of GUID to nice friendly setting name
                Dictionary<Guid, string> GuidToFriendlyName = new Dictionary<Guid, string>();

                try
                {
                    // Read from .csv file
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    string fileName = $"{assembly.GetName().Name}.{GUIDFriendlyNameFile}";

                    StreamReader reader = new StreamReader(assembly.GetManifestResourceStream(fileName));
                    IEnumerable<string> lines = reader.ReadToEnd().Split(new string[] { "\r\n" }, StringSplitOptions.None); // TODO - need to check

                    foreach (string line in lines)
                    {
                        if (line.Length > 0)
                        {
                            Guid guid = Guid.Parse(line.Split(',')[0]);
                            string friendlyname = line.Split(',')[1];
                            GuidToFriendlyName.Add(guid, friendlyname);
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to read/open file.", ex);
                }

                // Optional : Construct list of relevant generic events
                List<IGenericEvent> QoSSupportChangedEvents = new List<IGenericEvent>();
                foreach (var genericEvent in genericEventDataSource.Result.Events)
                {
                    if (genericEvent.ProviderId.Equals(GUIDS.Microsoft_Windows_Kernel_Processor_Power))
                    {
                        // Get all the profile IDs and profile GUIDS present
                        if (genericEvent.TaskName.Equals("ProfileRundown"))
                        {
                            string profileName = genericEvent.Fields[0].AsString;
                            uint profileID = genericEvent.Fields[1].AsByte;
                            Guid guid = genericEvent.Fields[4].AsGuid;
                            QoSLevelNames.TryGetValue(profileName, out QoSLevelName);

                            profileNames.Add(profileID, new Tuple<string, string, Guid>(profileName, QoSLevelName ?? profileName, guid));
                        }

                        // get all the setting Guids and values
                        else if (genericEvent.TaskName.Equals("ProfileSettingRundown"))
                        {
                            uint profileID = genericEvent.Fields[0].AsByte;
                            string name = genericEvent.Fields[1].AsString;
                            string type = genericEvent.Fields[2].EnumValue;
                            uint efficiencyClass = genericEvent.Fields[3].AsByte;
                            Guid guidString = genericEvent.Fields[4].AsGuid;
                            uint valueSize = genericEvent.Fields[5].AsUInt32;
                            uint value = 0;
                            if (valueSize == 1)
                            {
                                value = genericEvent.Fields[6].AsBinary.ToArray()[0];
                            }
                            else if (valueSize == 4)
                            {
                                value = BitConverter.ToUInt32(genericEvent.Fields[6].AsBinary.ToArray(), 0);
                            }

                            Tuple<uint, Guid> key = new Tuple<uint, Guid>(profileID, guidString);
                            uint? AC = null;
                            uint? DC = null;

                            if (!powSettings.TryGetValue(guidString, out Dictionary<uint, Tuple<uint?, uint?>> settings))
                            {
                                settings = new Dictionary<uint, Tuple<uint?, uint?>>();
                                powSettings[guidString] = settings;
                            }

                            if (!powSettings[guidString].TryGetValue(profileID, out Tuple<uint?, uint?> values))
                            {
                                if (type == "AC")
                                    values = new Tuple<uint?, uint?>(value, null);
                                else
                                    values = new Tuple<uint?, uint?>(null, value);

                                powSettings[guidString].Add(profileID, values);
                            }
                            else
                            {
                                if (type == "AC")
                                {
                                    AC = value;
                                    DC = powSettings[guidString][profileID].Item2;
                                }
                                else
                                {
                                    AC = powSettings[guidString][profileID].Item1;
                                    DC = value;
                                }
                                //Replace old tuple with new tuple with both values of AC and DC
                                powSettings[guidString].Remove(profileID);
                                powSettings[guidString].Add(profileID, new Tuple<uint?, uint?>(AC, DC));
                            }
                        }
                    }
                    else if (genericEvent.ProviderId.Equals(GUIDS.Microsoft_Windows_UserModePowerService))
                    {
                        // PowerScheme - Should mostly always be Balanced
                        if (genericEvent.TaskName.Equals("RundownPowerScheme"))
                        {
                            RundownPowerScheme = new Guid(genericEvent.Fields[0].AsGuid.ToString());
                            GuidToFriendlyName.TryGetValue(RundownPowerScheme, out RundownPowerSchemeString);
                        }

                        // Effective Overlay - Default, Battery bias, Better Perf, Best Perf
                        else if (genericEvent.TaskName.Equals("RundownEffectiveOverlayPowerScheme"))
                        {
                            RundownEffectiveOverlayPowerScheme = new Guid(genericEvent.Fields[0].AsGuid.ToString());
                            if (RundownEffectiveOverlayPowerScheme == Guid.Empty) // Out Of Box  Overlay
                            {
                                RundownEffectiveOverlayPowerSchemeString = DefaultOverlayScheme;
                            }
                            else
                            {
                                GuidToFriendlyName.TryGetValue(RundownEffectiveOverlayPowerScheme, out RundownEffectiveOverlayPowerSchemeString);
                            }
                        }
                    }
                }

                //Add  results
                results.Add("Rundown Power Scheme: " + RundownPowerSchemeString);
                results.Add("Rundown Effective Power Overlay: " + RundownEffectiveOverlayPowerSchemeString);

                // Read the validation rules
                ReadValidationRules(RundownEffectiveOverlayPowerScheme);

                // Validate PPM settings
                ValidatePPMSettings(powSettings, profileNames, GuidToFriendlyName, results);

                WriteOutput(results, outputPath, true); // Always No header for now
            }
        }

        /// <summary>
        /// This method reads the validation rules  
        /// The validation rules are set in an XML file
        /// 
        /// </summary>
        public static void ReadValidationRules(
            Guid rundownEffectiveOverlayPowerScheme
            )
        {
            // Read the XML file
            Assembly assembly = Assembly.GetExecutingAssembly();
            string fileName = PPMSettingRulesXMLFile; 
            XmlDocument doc = new XmlDocument();
            doc.Load(fileName);

            // Parse the XML file

            // Parse the power overlays
            XmlNodeList overlayNodeList = doc.SelectNodes("/validationRules/overlay");
            foreach (XmlNode overlayNode in overlayNodeList)
            {
                // Get the GUID of the Overlay 
                if (overlayNode["overlayGuid"] == null) 
                    continue;

                Guid overlayGuid = new Guid(overlayNode["overlayGuid"].InnerText);

                // We load only the rules of the effective power overlay 
                if (overlayGuid != rundownEffectiveOverlayPowerScheme) 
                    continue;

                // Get the list of profiles to validate
                XmlNodeList profileNodeList = overlayNode.SelectNodes("profile");

                // Iterate over all the profiles
                {
                    foreach (XmlNode profieNode in profileNodeList)
                    {
                        // Get the GUID of the profile 
                        if (profieNode["profileGuid"] == null)
                            continue;

                        Guid profileGuid = new Guid(profieNode["profileGuid"].InnerText);
                        List<Tuple<Guid, uint?, uint?, uint?, uint?>> listSettings = new List<Tuple<Guid, uint?, uint?, uint?, uint?>>();

                        // Get the list of settings to validate
                        XmlNodeList settingNodeList = profieNode.SelectNodes("setting");

                        // Iterate over all the settings
                        foreach (XmlNode settingNode in settingNodeList)
                        {
                            // Get the GUID of the setting
                            if (settingNode["settingGuid"] == null)
                                continue;

                            Guid settingGuid = new Guid(settingNode["settingGuid"].InnerText);
                            uint? acMin = null;
                            uint? acMax = null;
                            uint? dcMin = null;
                            uint? dcMax = null;

                            // Min bound in AC mode
                            if (settingNode["acMinValue"] != null)
                            {
                                acMin = Convert.ToUInt32(settingNode["acMinValue"].InnerText);
                            }

                            // Max bound in AC mode
                            if (settingNode["acMaxValue"] != null)
                            {
                                acMax = Convert.ToUInt32(settingNode["acMaxValue"].InnerText);
                            }

                            // Min bound in DC mode
                            if (settingNode["dcMinValue"] != null)
                            {
                                dcMin = Convert.ToUInt32(settingNode["dcMinValue"].InnerText);
                            }

                            // Max bound in DC mode
                            if (settingNode["dcMaxValue"] != null)
                            {
                                dcMax = Convert.ToUInt32(settingNode["dcMaxValue"].InnerText);
                            }

                            listSettings.Add(new Tuple<Guid, uint?, uint?, uint?, uint?>(settingGuid, acMin, acMax, dcMin, dcMax));

                        }

                        // Add a new rule
                        ValidationRules.Add(profileGuid, listSettings);
                    }
                }
            }
        }

        /// <summary>
        /// This method validates the PPM settings using the rules specified in the XML file:
        /// (1) Validate that all the PPM settings are set across the profile (take into account the order of inheritance of the profiles)
        /// (2) No inversions between the profiles (e.g. Default profile EPP should be <= LowQoS profile EPP)
        /// (3) The Min/Max bound rules of each setting are respected
        /// 
        /// </summary>
        public static void ValidatePPMSettings(
            Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>> powSettings,
            Dictionary<uint, Tuple<string, string, Guid>> profileNames,
            Dictionary<Guid, string> GuidToFriendlyName,
            List<string> results
            )
        {
            foreach (DictionaryEntry profile in OrderedProfileHierarchy)
            {
                // Check if this profile should be validated
                Guid profileGuid = (Guid)profile.Key;
                if (!ValidationRules.ContainsKey(profileGuid))
                    continue;


                // Get the friendly name of the profile
                string profileName;
                GuidToFriendlyName.TryGetValue(profileGuid, out profileName);

                // Iterate over the PPM settings to validate for this profile
                foreach (var setting in ValidationRules[profileGuid])
                {
                    Guid settingGuid = setting.Item1;
                    string settingName;
                    uint? acValue = null;
                    uint? dcValue = null;
                    Guid? acParentProfileGuid = null;
                    Guid? dcParentProfileGuid = null;

                    uint? acParentSettingValue = null;
                    uint? dcParentSettingValue = null;

                    // Get the friendly name of the PPM setting
                    GuidToFriendlyName.TryGetValue(settingGuid, out settingName);

                    // Validate that the setting is set for any profile 
                    if (!powSettings.ContainsKey(settingGuid))
                    {
                        results.Add($"ERROR : {profileName} : {settingName} : AC/DC : Setting is not set in the profile and in any profile of the PPKG.");
                        continue;
                    }

                    var profile_id = profileNames.FirstOrDefault(x => x.Value.Item3 == profileGuid).Key;
                    if (powSettings[settingGuid].ContainsKey(profile_id))
                    {
                        // Get the AC/DC values
                        acValue = powSettings[settingGuid][profile_id].Item1;
                        dcValue = powSettings[settingGuid][profile_id].Item2;
                    }

                    // Default profile must have a value for this setting
                    if (profileGuid == GuidDefault)
                    {
                        // Check the AC mode setting
                        if (!acValue.HasValue)
                            results.Add($"ERROR : {profileName} : {settingName} : AC : Setting is not set in the profile.");
                       
                        // Check the DC mode setting
                        if (!dcValue.HasValue)
                            results.Add($"ERROR : {profileName} : {settingName} : DC : Setting is not set in the profile.");
                    }
                    else
                    {
                        // This is not the default profile ==> the profile has a parent profile
                        // Find the value of the setting for the parent profile
                        FindSettingInParentProfiles(true, profileGuid, settingGuid, powSettings, profileNames, ref acParentProfileGuid, ref acParentSettingValue);
                        FindSettingInParentProfiles(false, profileGuid, settingGuid, powSettings, profileNames, ref dcParentProfileGuid, ref dcParentSettingValue);

                        // Get the names of the parent profiles
                        string acParentProfileName = "";
                        if (acParentProfileGuid.HasValue)
                            GuidToFriendlyName.TryGetValue(acParentProfileGuid.Value, out acParentProfileName);


                        string dcParentProfileName = "";
                        if (dcParentProfileGuid.HasValue)
                            GuidToFriendlyName.TryGetValue(dcParentProfileGuid.Value, out dcParentProfileName);

                        // Validate the AC value
                        if (!acValue.HasValue)
                        {
                            // Check the setting is set for one of the parent profile
                            if (acParentProfileGuid.HasValue && acParentSettingValue.HasValue)
                            {
                                results.Add($"WARNING : {profileName} : {settingName} : AC : Setting is not set in the profile. It inherits the value {acParentSettingValue.Value.ToString()} set for the profile {acParentProfileName}");
                            }
                            else
                            {
                                results.Add($"ERROR : {profileName} : {settingName} : AC : Setting is not set in the profile and in any of its parent profiles.");
                            }
                        }
                        else
                        {
                            // Check inversions
                            if (acParentSettingValue.HasValue)
                            {
                                if (!ValidateInversions(settingGuid, acValue.Value, acParentSettingValue.Value))
                                {
                                    results.Add($"ERROR : {profileName} : {settingName} : AC : The setting value is more agressive than the setting used for the parent profile {acParentProfileName} ({acValue} vs. {acParentSettingValue})");
                                }
                            }
                        }

                        // Validate the DC value
                        if (!dcValue.HasValue)
                        {
                            if (dcParentProfileGuid.HasValue && dcParentSettingValue.HasValue)
                            {
                                results.Add($"WARNING : {profileName} : {settingName} : DC : Setting is not set in the profile. It inherits the value {dcParentSettingValue.Value.ToString()} set for the profile {dcParentProfileName}");
                            }
                            else
                            {
                                results.Add($"ERROR : {profileName} : {settingName} : DC : Setting is not set in the profile and in any of its parent profiles.");
                            }
                        }
                        else
                        {
                            // Check inversions
                            if (dcParentSettingValue.HasValue)
                            {
                                if (!ValidateInversions(settingGuid, dcValue.Value, dcParentSettingValue.Value))
                                {
                                    results.Add($"ERROR : {profileName} : {settingName} : DC : The setting value is more agressive than the setting used for the parent profile {dcParentProfileName} ({dcValue} vs. {dcParentSettingValue})");
                                }
                            }
                        }
                    }

                    // Validate the setting against the Min/Max bounds 

                    // AC MinValue rule
                    if (setting.Item2.HasValue)
                    {
                        if (!ValidateMinMaxBoundRules(acValue, acParentSettingValue, setting.Item2.Value, false))
                        {
                            uint value = (acValue.HasValue) ? acValue.Value : acParentSettingValue.Value;
                            results.Add($"ERROR : {profileName} : {settingName} : AC : Setting value {value} < min bound {setting.Item2.Value.ToString()}");
                        }
                    }

                    // AC MaxValue rule
                    if (setting.Item3.HasValue)
                    {
                        if (!ValidateMinMaxBoundRules(acValue, acParentSettingValue, setting.Item3.Value, true))
                        {
                            uint value = (acValue.HasValue) ? acValue.Value : acParentSettingValue.Value;
                            results.Add($"ERROR : {profileName} : {settingName} : AC : Setting value  {value} > max bound {setting.Item3.Value.ToString()}");
                        }
                    }

                    // DC MinValue rule
                    if (setting.Item4.HasValue)
                    {
                        if (!ValidateMinMaxBoundRules(dcValue, dcParentSettingValue, setting.Item4.Value, false))
                        {
                            uint value = (dcValue.HasValue) ? dcValue.Value : dcParentSettingValue.Value;
                            results.Add($"ERROR : {profileName} : {settingName} : DC : Setting value {value} < min bound {setting.Item4.Value.ToString()}");
                        }
                    }

                    // DC MaxValue rule
                    if (setting.Item5.HasValue)
                    {
                        if (!ValidateMinMaxBoundRules(dcValue, dcParentSettingValue, setting.Item5.Value, true))
                        {
                            uint value = (dcValue.HasValue) ? dcValue.Value : dcParentSettingValue.Value;
                            results.Add($"ERROR : {profileName} : {settingName} : DC : Setting value {value} > max bound {setting.Item5.Value.ToString()}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// This method returns false if there is inversion between the PPM profiles 
        /// </summary>
        public static bool ValidateInversions(
            Guid settingGuid,
            uint settingValue,
            uint parentSettingValue
            )
        {
            // EPP
            if (settingGuid == GuidEPP)
            {
                if (settingValue < parentSettingValue)
                    return false;
            }

            // FrequencyCap
            else if (settingGuid == GuidFrequencyCap)
            {
                if (settingValue == 0 && parentSettingValue > 0) 
                    return false;
                if (parentSettingValue != 0 && settingValue > parentSettingValue) 
                    return false;
            }

            // Min/Max Cores
            else if (settingGuid == GuidCPMaxCores || settingGuid == GuidCPMinCores)
            {
                if (settingValue > parentSettingValue)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// This method returns false if the rules of min/max bounds are not satisfied 
        /// </summary>
        public static bool ValidateMinMaxBoundRules(
            uint? settingValue,
            uint? parentSettingValue,
            uint targetValue,
            bool compareToMax
            )
        {
            uint? value = null;

            if (settingValue.HasValue)
                value = settingValue;
            else if (parentSettingValue.HasValue)
                value = parentSettingValue;

            if (value.HasValue)
            {
                if (compareToMax && value.Value > targetValue)
                    return false;

                if (!compareToMax && value.Value < targetValue)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Given a PPM setting and a PPM profile, this method retrieves the parent profile for which the PPM setting is set (i.e., has a value). 
        /// </summary>
        public static void FindSettingInParentProfiles(
            bool ac,
            Guid profileGuid,
            Guid settingGuid,
            Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>> powSettings,
            Dictionary<uint, Tuple<string, string, Guid>> profileNames,
            ref Guid? parentProfileGuid,
            ref uint? parentSettingValue
            )

        {
            Guid tempProfileGuid = profileGuid;
            Guid tempParentProfileGuid;
            uint? value = null;
 
            if (!powSettings.ContainsKey(settingGuid))
            {
                return;
            }

            while (true)
            {
                if (tempProfileGuid == GuidDefault)
                {
                    // Default profile doesn't have parents 
                    break;
                }
                else
                {
                    // Get the parent profile
                    tempParentProfileGuid = (Guid)OrderedProfileHierarchy[tempProfileGuid];
                    var parentProfileId = profileNames.FirstOrDefault(x => x.Value.Item3 == tempParentProfileGuid).Key;
                    if (powSettings[settingGuid].ContainsKey(parentProfileId))
                    {
                        // Get the value of the parent profile
                        if (ac)
                            value = powSettings[settingGuid][parentProfileId].Item1;
                        else
                            value = powSettings[settingGuid][parentProfileId].Item2;
                    }

                    if (value.HasValue)
                    {
                        // Setting found in one of the parent profiles
                        parentProfileGuid = tempParentProfileGuid;
                        parentSettingValue = value;
                        break;
                    }
                    else
                    {
                        // The direct parent doesn’t have the setting set
                        // Go higher in the hierarchy of the profiles
                        tempProfileGuid = tempParentProfileGuid;
                    }
                }
            }
        }

        /// <summary>
        /// Writes a csv of the computed results to the target outputPath
        /// </summary>
        /// <param name="results"></param>
        /// <param name="outputFile"></param>
        /// <param name="noHeader"></param>
        public static void WriteOutput(List<String> results, String outputPath, bool noHeader)
        {
            StringBuilder output = new StringBuilder();
            if (!noHeader)
            {
                output.Append(getHeader());
                output.AppendLine();
            }

            foreach (String result in results)
            {
                output.Append(result);
                output.AppendLine();
            }

            File.WriteAllText(outputPath, output.ToString());
        }
    }
}