﻿/*++

Copyright (c) 2022 Microsoft Corporation

Module Name:

    PPMCheckerTool.cs

Abstract:

    What does this tool do?

    External executable tool that checks for less optimal PPM settings against an Good settings XML. Also, checks for inversions in settings values across profiles


Author:

    Sidharth Venkatesh (sivenkatesh) 19-Dec-2022
    Zied Ben Hamouda (zbenhamouda) 19-Dec-2022

--*/
using System;
using System.IO;
using System.Linq;
using System.Text;
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

        // Current power scheme and power overlay 
        public static Guid RundownPowerScheme = Guid.Empty;
        public static Guid RundownEffectiveOverlayPowerScheme = Guid.Empty;
        public static String RundownPowerSchemeString = String.Empty;
        public static String RundownEffectiveOverlayPowerSchemeString = String.Empty;

        // Default Overlay Scheme name
        public const String DefaultOverlayScheme = "Default Overlay Scheme: Balanced (Non-Surface) / Recommended (Surface)";

        // Validation rules associated with each PPM setting
        public struct SettingValidationRules
        {
            // Absolute value of the setting
            public uint? acValue;
            public uint? dcValue;

            // Min bar
            public uint? acMinValue;
            public uint? dcMinValue;

            // Max bar
            public uint? acMaxValue;
            public uint? dcMaxValue;

            // Min distance to the setting of another profile
            // Pair (Profile Guid, MinDistance)
            public Tuple<Guid, int> acMinDistanceToProfile;
            public Tuple<Guid, int> dcMinDistanceToProfile;

            // Max distance to the setting of another profile
            // Pair (Profile Guid, MaxDistance)
            public Tuple<Guid, int> acMaxDistanceToProfile;
            public Tuple<Guid, int> dcMaxDistanceToProfile;
        }

        // Data structure that stores the PPM setting values set in the ProfileSettingRundown ETL event
        // Maps PPM Setting --> ProfileID --> (AC value , DC value)
        public static Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>> PowSettings = new Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>>();

        // Data structure that stores the PPM profiles set in the ProfileRundown ETL event
        // Maps ProfileID --> (Name , Name, Guid)
        public static Dictionary<uint, Tuple<string, string, Guid>> ProfileNames = new Dictionary<uint, Tuple<string, string, Guid>>();

        // Local CSV generated by dumping powercfg caches a mapping of GUID to nice friendly setting name
        public static Dictionary<Guid, string> GuidToFriendlyName = new Dictionary<Guid, string>();

        // Validation rules per profile and PPM setting
        // Maps ProfileGuid --> (SettingGuid, SettingValidationRules)
        public static Dictionary<Guid, List<Tuple<Guid, SettingValidationRules>>> PerProfileValidationRules 
            = new Dictionary<Guid, List<Tuple<Guid, SettingValidationRules>>>();

        // Final restults string into the output csv file
        public static List<String> Results = new List<String>();

        // Profile hierarchy table
        // The profiles are ordered by inheritance E.g., MedQoS profile inherits from Default profie ==> Default profile order is lower than MedQoS order 
        // Each element is a pair (profile GUID, parent profile GUID)
        public static OrderedDictionary OrderedProfileHierarchy = new OrderedDictionary()
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

                // Handle invalid arguments
                if (inputFile != null && !File.Exists(inputFile))
                {
                    Console.Error.WriteLine("Failed to access input file. Please provide path to valid ETL.");
                    return -1;
                }

                // Process the input ETL trace
                AnalyzeTrace(inputFile, outputFile);

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
        /// <param name="tracePath"> Input ETL trace </param>
        /// <param name="outputPath"> Output results file </param>
        static void AnalyzeTrace(string tracePath, string outputPath)
        {
            using (ITraceProcessor trace = TraceProcessor.Create(tracePath))
            {
                // Enable DataSources
                IPendingResult<ISystemMetadata> systemMetadata = trace.UseSystemMetadata();
                IPendingResult<IGenericEventDataSource> genericEventDataSource = trace.UseGenericEvents();
                
                // Process Data Sources
                trace.Process();

                // Validate that the necessary data sources are present in order to continue analysis
                if (!systemMetadata.HasResult ||
                   (!genericEventDataSource.HasResult || genericEventDataSource.Result.Events.Count == 0) ||
                   (!genericEventDataSource.Result.Events.Any(x => x.ProviderId.Equals(GUIDS.Microsoft_Windows_Kernel_Processor_Power))) ||
                   (!genericEventDataSource.Result.Events.Any(x => x.ProviderId.Equals(GUIDS.Microsoft_Windows_UserModePowerService))))
                {
                    throw new Exception("No metadata or kernel-processor-power or usermode-power events");
                }

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

                string QoSLevelName = "";

                // Extract metadata information
                string oemModel = systemMetadata.Result.Model;
                string oemName = systemMetadata.Result.Manufacturer;
                int buildNumber = systemMetadata.Result.OSVersion.Build;
                int buildRevision = systemMetadata.Result.OSVersion.Revision;

                // If more than 1 type of core, then it is a hybrid system
                //bool isHybridSystem = systemMetadata.Result.Processors.GroupBy(x => x.EfficiencyClass).Distinct().Count() > 1 ? true : false;

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

                            ProfileNames.Add(profileID, new Tuple<string, string, Guid>(profileName, QoSLevelName ?? profileName, guid));
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

                            if (!PowSettings.TryGetValue(guidString, out Dictionary<uint, Tuple<uint?, uint?>> settings))
                            {
                                settings = new Dictionary<uint, Tuple<uint?, uint?>>();
                                PowSettings[guidString] = settings;
                            }

                            if (!PowSettings[guidString].TryGetValue(profileID, out Tuple<uint?, uint?> values))
                            {
                                if (type == "AC")
                                    values = new Tuple<uint?, uint?>(value, null);
                                else
                                    values = new Tuple<uint?, uint?>(null, value);

                                PowSettings[guidString].Add(profileID, values);
                            }
                            else
                            {
                                if (type == "AC")
                                {
                                    AC = value;
                                    DC = PowSettings[guidString][profileID].Item2;
                                }
                                else
                                {
                                    AC = PowSettings[guidString][profileID].Item1;
                                    DC = value;
                                }
                                //Replace old tuple with new tuple with both values of AC and DC
                                PowSettings[guidString].Remove(profileID);
                                PowSettings[guidString].Add(profileID, new Tuple<uint?, uint?>(AC, DC));
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

                //Add results
                if (!File.Exists(outputPath))
                {
                    Results.Add("PPM Checker Tool");
                    Results.Add("OEMModel: " + oemModel);
                    Results.Add("OEMName: " + oemName);
                    Results.Add("Build Number: " + buildNumber + "." + buildRevision);
                    //Results.Add("IsHybridSystem: " + isHybridSystem);
                    Results.Add("===============================================================================");
                }

                Results.Add("Rundown Power Scheme: " + RundownPowerSchemeString);
                Results.Add("Rundown Effective Power Overlay: " + RundownEffectiveOverlayPowerSchemeString);

                // Read the validation rules from the XML file
                ReadValidationRules();

                // Validate PPM settings
                ValidatePPMSettings();

                // Write the output results
                WriteOutput(Results, outputPath);
            }
        }

        /// <summary>
        /// This method reads the validation rules  
        /// The validation rules are specified in an XML file
        /// The method load the validation rules in the data structure ValidationRules
        /// </summary>
        public static void ReadValidationRules()
        {
            // Read the XML file
            Assembly assembly = Assembly.GetExecutingAssembly();
            string fileName = PPMSettingRulesXMLFile; 
            XmlDocument doc = new XmlDocument();
            doc.Load(fileName);

            // Parse the power overlays
            XmlNodeList overlayNodeList = doc.SelectNodes("/validationRules/overlay");
            foreach (XmlNode overlayNode in overlayNodeList)
            {
                // Get the GUID of the Overlay 
                if (overlayNode["overlayGuid"] == null) 
                    continue;

                Guid overlayGuid = new Guid(overlayNode["overlayGuid"].InnerText);

                // We load only the rules of the effective power overlay 
                if (overlayGuid != RundownEffectiveOverlayPowerScheme) 
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
                        List<Tuple<Guid, SettingValidationRules>> listPerSettingRules = new List<Tuple<Guid, SettingValidationRules>>();

                        // Get the list of settings to validate
                        XmlNodeList settingNodeList = profieNode.SelectNodes("setting");

                        // Iterate over all the settings
                        foreach (XmlNode settingNode in settingNodeList)
                        {
                            // Get the GUID of the setting
                            if (settingNode["settingGuid"] == null)
                                continue;

                            Guid settingGuid = new Guid(settingNode["settingGuid"].InnerText);
                            SettingValidationRules settingValidationRules = new SettingValidationRules();

                            // Absolute value in AC mode
                            if (settingNode["acValue"] != null)
                            {
                                settingValidationRules.acValue = Convert.ToUInt32(settingNode["acValue"].InnerText);
                            }

                            // Min bound in AC mode
                            if (settingNode["acMinValue"] != null)
                            {
                                settingValidationRules.acMinValue = Convert.ToUInt32(settingNode["acMinValue"].InnerText);
                            }

                            // Max bound in AC mode
                            if (settingNode["acMaxValue"] != null)
                            {
                                settingValidationRules.acMaxValue = Convert.ToUInt32(settingNode["acMaxValue"].InnerText);
                            }

                            // Min Distance to profile in AC mode
                            if (settingNode["acMinDistanceToProfile"] != null)
                            {
                                if (settingNode["acMinDistanceToProfile"]["profile"] != null && settingNode["acMinDistanceToProfile"]["distance"] != null)
                                {
                                    Guid refProfileGuid = new Guid(settingNode["acMinDistanceToProfile"]["profile"].InnerText);
                                    int distance = Convert.ToInt32(settingNode["acMinDistanceToProfile"]["distance"].InnerText);
                                    settingValidationRules.acMinDistanceToProfile = new Tuple<Guid, int>(refProfileGuid, distance);
                                }
                            }

                            // Max Distance to profile in AC mode
                            if (settingNode["acMaxDistanceToProfile"] != null)
                            {
                                if (settingNode["acMaxDistanceToProfile"]["profile"] != null && settingNode["acMaxDistanceToProfile"]["distance"] != null)
                                {
                                    Guid refProfileGuid = new Guid(settingNode["acMaxDistanceToProfile"]["profile"].InnerText);
                                    int distance = Convert.ToInt32(settingNode["acMaxDistanceToProfile"]["distance"].InnerText);
                                    settingValidationRules.acMaxDistanceToProfile = new Tuple<Guid, int>(refProfileGuid, distance);
                                }
                            }

                            // Absolute value in DC mode
                            if (settingNode["dcValue"] != null)
                            {
                                settingValidationRules.dcValue = Convert.ToUInt32(settingNode["dcValue"].InnerText);
                            }

                            // Min bound in DC mode
                            if (settingNode["dcMinValue"] != null)
                            {
                                settingValidationRules.dcMinValue = Convert.ToUInt32(settingNode["dcMinValue"].InnerText);
                            }

                            // Max bound in DC mode
                            if (settingNode["dcMaxValue"] != null)
                            {
                                settingValidationRules.dcMaxValue = Convert.ToUInt32(settingNode["dcMaxValue"].InnerText);
                            }

                            // Min Distance to profile in DC mode
                            if (settingNode["dcMinDistanceToProfile"] != null)
                            {
                                if (settingNode["dcMinDistanceToProfile"]["profile"] != null && settingNode["dcMinDistanceToProfile"]["distance"] != null)
                                {
                                    Guid refProfileGuid = new Guid(settingNode["dcMinDistanceToProfile"]["profile"].InnerText);
                                    int distance = Convert.ToInt32(settingNode["dcMinDistanceToProfile"]["distance"].InnerText);
                                    settingValidationRules.dcMinDistanceToProfile = new Tuple<Guid, int>(refProfileGuid, distance);
                                }
                            }

                            // Max Distance to profile in DC mode
                            if (settingNode["dcMaxDistanceToProfile"] != null)
                            {
                                if (settingNode["dcMaxDistanceToProfile"]["profile"] != null && settingNode["dcMaxDistanceToProfile"]["distance"] != null)
                                {
                                    Guid refProfileGuid = new Guid(settingNode["dcMaxDistanceToProfile"]["profile"].InnerText);
                                    int distance = Convert.ToInt32(settingNode["dcMaxDistanceToProfile"]["distance"].InnerText);
                                    settingValidationRules.dcMaxDistanceToProfile = new Tuple<Guid, int>(refProfileGuid, distance);
                                }
                            }

                            listPerSettingRules.Add(new Tuple<Guid, SettingValidationRules>(settingGuid, settingValidationRules));
                        }

                        // Add a new rule
                        PerProfileValidationRules.Add(profileGuid, listPerSettingRules);
                    }
                }
            }
        }

        /// <summary>
        /// This method validates the PPM settings against the rules specified in the XML file:
        /// (1) For each profile, all the PPM settings set in the XML rules file are checked in (we consider the order of inheritance of the profiles)
        /// (2) The rules for setting's Min/Max/Absolute values are satisfied
        /// </summary>
        public static void ValidatePPMSettings()
        {
            foreach (DictionaryEntry profile in OrderedProfileHierarchy)
            {
                // Check if this profile should be validated
                Guid profileGuid = (Guid)profile.Key;
                if (!PerProfileValidationRules.ContainsKey(profileGuid))
                    continue;

                // Get the friendly name of the profile
                string profileName;
                GuidToFriendlyName.TryGetValue(profileGuid, out profileName);

                // Iterate over the PPM settings to validate for this profile
                foreach (var settingRules in PerProfileValidationRules[profileGuid])
                {
                    Guid settingGuid = settingRules.Item1;
                    SettingValidationRules rules = settingRules.Item2;

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
                    if (!PowSettings.ContainsKey(settingGuid))
                    {
                        Results.Add($"ERROR , {profileName} , {settingName} , AC/DC , , Setting is not set in the profile and in any profile of the PPKG.");
                        continue;
                    }

                    var profile_id = ProfileNames.FirstOrDefault(x => x.Value.Item3 == profileGuid).Key;
                    if (PowSettings[settingGuid].ContainsKey(profile_id))
                    {
                        // Get the AC/DC values
                        acValue = PowSettings[settingGuid][profile_id].Item1;
                        dcValue = PowSettings[settingGuid][profile_id].Item2;
                    }

                    if (!acValue.HasValue && profileGuid != GuidDefault)
                    {
                        FindSettingInParentProfiles(true, profileGuid, settingGuid, ref acParentProfileGuid, ref acParentSettingValue);

                        if (acParentSettingValue.HasValue && acParentProfileGuid.HasValue)
                        {
                            // The setting inherits the value of the parent profile
                            acValue = acParentSettingValue;
                        }
                    }

                    if (!dcValue.HasValue && profileGuid != GuidDefault)
                    {
                        FindSettingInParentProfiles(false, profileGuid, settingGuid, ref dcParentProfileGuid, ref dcParentSettingValue);

                        if (dcParentSettingValue.HasValue && dcParentProfileGuid.HasValue)
                        {
                            // The setting inherits the value of the parent profile
                            dcValue = dcParentSettingValue;
                        }
                    }

                    // Check the AC mode setting
                    if (!acValue.HasValue)
                    {
                        // The setting doesn’t have a value
                        Results.Add($"ERROR , {profileName} , {settingName} , AC , , Setting is not set in the profile.");
                    }
                    else
                    {
                        // Validate against the validation rules 

                        // Rule "acValue"
                        if (rules.acValue.HasValue)
                        {
                            if (acValue.Value != rules.acValue.Value)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , AC , {acValue.Value}, Setting should be = {rules.acValue.Value}");
                            }
                        }

                        // Rule "acMinValue"
                        if (rules.acMinValue.HasValue)
                        {
                            if (acValue.Value < rules.acMinValue.Value)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , AC , {acValue.Value} , Setting should be >= {rules.acMinValue.Value}");
                            }
                        }

                        // Rule "acMaxValue"
                        if (rules.acMaxValue.HasValue)
                        {
                            if (acValue.Value > rules.acMaxValue.Value)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , AC , {acValue.Value} , Setting should be  <= {rules.acMaxValue.Value}");
                            }
                        }

                        // Rule "acMinDistanceToProfile"
                        if (rules.acMinDistanceToProfile != null)
                        {
                            // Get the reference profile
                            Guid refProfileGuid = rules.acMinDistanceToProfile.Item1;
                            string refProfileName;
                            GuidToFriendlyName.TryGetValue(refProfileGuid, out refProfileName);

                            // Get the distance value
                            int distance = rules.acMinDistanceToProfile.Item2;

                            // Calculate the distance to reference profile
                            int distanceToRefProfile = CalculateDistanceToProfile(settingGuid, acValue.Value, refProfileGuid, true);

                            if (distanceToRefProfile == Int32.MinValue)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , AC , {acValue.Value} , Distance to profile {refProfileName} should be >= {distance}. PPM setting is not set for the profile {refProfileName}");
                            }
                            else if (distanceToRefProfile < distance)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , AC , {acValue.Value} , Distance to profile {refProfileName} should be >= {distance}. Actual distance = {distanceToRefProfile}");
                            }
                        }

                        // Rule "acMaxDistanceToProfile"
                        if (rules.acMaxDistanceToProfile != null)
                        {
                            // Get the reference profile
                            Guid refProfileGuid = rules.acMaxDistanceToProfile.Item1;
                            string refProfileName;
                            GuidToFriendlyName.TryGetValue(refProfileGuid, out refProfileName);

                            // Get the distance 
                            int distance = rules.acMaxDistanceToProfile.Item2;

                            // Calculate the distance to reference profile
                            int distanceToRefProfile = CalculateDistanceToProfile(settingGuid, acValue.Value, refProfileGuid, true);

                            if (distanceToRefProfile == Int32.MinValue)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , AC , {acValue.Value} , Distance to profile {refProfileName} should be <= {distance}. PPM setting is not set for the profile {refProfileName}");
                            }
                            else if (distanceToRefProfile > distance)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , AC , {acValue.Value} , Distance to profile {refProfileName} should be <= {distance}. Actual distance = {distanceToRefProfile}");
                            }
                        }
                    }

                    // Check the DC mode setting 
                    if (!dcValue.HasValue)
                    {
                        // The setting doesn’t have a value
                        Results.Add($"ERROR , {profileName} , {settingName} , DC , , Setting is not set in the profile.");
                    }
                    else
                    {
                        // Validate against the validation rules 

                        // Rule "dcValue"
                        if (rules.dcValue.HasValue)
                        {
                            if (dcValue.Value != rules.dcValue.Value)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , DC , {dcValue.Value} , Setting should be = {rules.dcValue.Value}");
                            }
                        }

                        // Rule "dcMinValue"
                        if (rules.dcMinValue.HasValue)
                        {
                            if (dcValue.Value < rules.dcMinValue.Value)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , DC , {dcValue.Value} , Setting should be >= {rules.dcMinValue.Value}");
                            }
                        }

                        // Rule "dcMaxValue"
                        if (rules.dcMaxValue.HasValue)
                        {
                            if (dcValue.Value > rules.dcMaxValue.Value)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , DC , {dcValue.Value} , Setting should be  <= {rules.dcMaxValue.Value}");
                            }
                        }

                        // Rule "dcMinDistanceToProfile"
                        if (rules.dcMinDistanceToProfile != null)
                        {
                            // Get the reference profile
                            Guid refProfileGuid = rules.dcMinDistanceToProfile.Item1;
                            string refProfileName;
                            GuidToFriendlyName.TryGetValue(refProfileGuid, out refProfileName);

                            // Get the distance 
                            int distance = rules.dcMinDistanceToProfile.Item2;

                            // Calculate the distance to reference profile
                            int distanceToRefProfile = CalculateDistanceToProfile(settingGuid, dcValue.Value, refProfileGuid, false);

                            if (distanceToRefProfile == Int32.MinValue)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , DC , {dcValue.Value} , Distance to profile {refProfileName} should be >= {distance}. PPM setting is not set for the profile {refProfileName}");
                            }
                            else if (distanceToRefProfile < distance)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , DC , {dcValue.Value} , Distance to profile {refProfileName} should be >= {distance}. Actual distance = {distanceToRefProfile}");
                            }
                        }

                        // Rule "dcMaxDistanceToProfile"
                        if (rules.dcMaxDistanceToProfile != null)
                        {
                            // Get the reference profile
                            Guid refProfileGuid = rules.dcMaxDistanceToProfile.Item1;
                            string refProfileName;
                            GuidToFriendlyName.TryGetValue(refProfileGuid, out refProfileName);

                            // Get the distance value
                            int distance = rules.dcMaxDistanceToProfile.Item2;

                            // Calculate the distance to reference profile
                            int distanceToRefProfile = CalculateDistanceToProfile(settingGuid, dcValue.Value, refProfileGuid, false);

                            if (distanceToRefProfile == Int32.MinValue)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , DC , {dcValue.Value} , Distance to profile {refProfileName} should be >= {distance}. PPM setting is not set for the profile {refProfileName}");
                            }
                            else if (distanceToRefProfile > distance)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , DC , {dcValue.Value} , Distance to profile {refProfileName} should be <= {distance}. Actual distance = {distanceToRefProfile}");
                            }
                        }
                    }
                }
            }
        }


        /// <summary>
        /// Given a PPM setting and a PPM profile, this method retrieves the first parent profile for which the PPM setting is set 
        /// (i.e., the setting has a value). 
        /// </summary>
        /// <param name="isAc"> AC/DC mode </param>
        /// <param name="profileGuid"> GUID of the setting profile </param>
        /// <param name="settingGuid"> GUID of the setting </param>
        /// <paramref name="parentProfileGuid"> the GUID of the first parent profile for which the setting is set</param>
        /// <paramref name="parentSettingValue"> the value of the setting in the parent profile</param>
        public static void FindSettingInParentProfiles( bool isAc, Guid profileGuid, Guid settingGuid, ref Guid? parentProfileGuid, ref uint? parentSettingValue)
        {
            Guid tempProfileGuid = profileGuid;
            Guid tempParentProfileGuid;
            uint? value = null;
 
            if (!PowSettings.ContainsKey(settingGuid))
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
                    var parentProfileId = ProfileNames.FirstOrDefault(x => x.Value.Item3 == tempParentProfileGuid).Key;
                    if (PowSettings[settingGuid].ContainsKey(parentProfileId))
                    {
                        // Get the value of the parent profile
                        if (isAc)
                            value = PowSettings[settingGuid][parentProfileId].Item1;
                        else
                            value = PowSettings[settingGuid][parentProfileId].Item2;
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
        /// For a given setting, calculate the distance between two different profiles 
        /// E.g., EPP = 50 for default profile and = 70 for LowQoS profile --> distance between the two profiles = 20 in this case.
        /// </summary>
        /// <param name="settingGuid"> Guid of the setting </param>
        /// <param name="settingValue"> value of the setting </param>
        /// <param name="refProfileGuid"> GUID of the profile to compare with </param>
        /// <param name="isAC"> AC/DC mode </param>
        /// <returns> the distance between the two profiles. If the setting is not set for the refProfileGuid then return Int32.MinValue </returns>
        public static int CalculateDistanceToProfile(Guid settingGuid, uint settingValue, Guid refProfileGuid, bool isAC)
        {
            // Get the setting value of the reference profile
            uint? refProfileSettingValue = null;
            var refProfile_id = ProfileNames.FirstOrDefault(x => x.Value.Item3 == refProfileGuid).Key;

            if(isAC)
            {
                // AC mode
                if (PowSettings[settingGuid].ContainsKey(refProfile_id) && PowSettings[settingGuid][refProfile_id].Item1.HasValue)
                {
                    // The setting was set for the Ref profile
                    refProfileSettingValue = PowSettings[settingGuid][refProfile_id].Item1.Value;
                }
                else
                {
                    // The setting was not set for the Ref profile 
                    // Check if Ref profile inherits the settings from its parent profiles
                    Guid? refProfileParentGuid = null;
                    FindSettingInParentProfiles(true, refProfileGuid, settingGuid, ref refProfileParentGuid, ref refProfileSettingValue);
                }
            }
            else
            {
                // DC mode
                if (PowSettings[settingGuid].ContainsKey(refProfile_id) && PowSettings[settingGuid][refProfile_id].Item2.HasValue)
                {
                    // The setting was set for the Ref profile
                    refProfileSettingValue = PowSettings[settingGuid][refProfile_id].Item2.Value;
                }
                else
                {
                    // The setting was not set for the Ref profile 
                    // Check if Ref profile inherits the settings from its parent profiles
                    Guid? refProfileParentGuid = null;
                    FindSettingInParentProfiles(false, refProfileGuid, settingGuid, ref refProfileParentGuid, ref refProfileSettingValue);
                }
            }

            // Check if the setting has a value in the reference profile 
            if (!refProfileSettingValue.HasValue)
            {
                return Int32.MinValue;
            }

            // Calculate the distance to Ref profile 
            int distanceToRefProfile = (int)settingValue - (int)refProfileSettingValue;
            return distanceToRefProfile;
        }

        /// <summary>
        /// Writes a csv of the computed results to the target outputPath
        /// </summary>
        /// <param name="results"> Data structure that stores the PPM setting's validation results</param>
        /// <param name="outputFile"> Output file name</param>
        public static void WriteOutput(List<String> results, String outputPath)
        {
            StringBuilder output = new StringBuilder();

            if(File.Exists(outputPath)) 
            {
                output.AppendLine("===============================================================================");
                output.AppendLine(); 
            }

            foreach (String result in results)
            {
                output.Append(result);
                output.AppendLine();
            }

            File.AppendAllText(outputPath, output.ToString());
        }
    }
}