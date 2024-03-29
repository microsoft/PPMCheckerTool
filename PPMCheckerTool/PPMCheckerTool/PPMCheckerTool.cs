﻿/*++

Copyright (c) Microsoft Corporation.
Licensed under the MIT License.

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
        private const String FriendlyNameGUIDFile = "FriendlyNameToGuid.csv";
        private const String PPMSettingRulesXMLFile = "PPMSettingRules.xml";

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
        public static Guid GuidHeteroDecreaseThreshold = new Guid("f8861c27-95e7-475c-865b-13c0cb3f9d6b");
        public static Guid GuidHeteroDecreaseThreshold1 = new Guid("f8861c27-95e7-475c-865b-13c0cb3f9d6c");
        public static Guid GuidHeteroIncreaseThreshold = new Guid("b000397d-9b0b-483d-98c9-692a6060cfbf");
        public static Guid GuidHeteroIncreaseThreshold1 = new Guid("b000397d-9b0b-483d-98c9-692a6060cfc0");
        public static Guid GuidClass0Fmax = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
        public static Guid GuidClass1Fmax = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e101");

        // Current power scheme and power overlay 
        public static Guid RundownPowerScheme = Guid.Empty;
        public static Guid RundownEffectiveOverlayPowerScheme = Guid.Empty;
        public static String RundownPowerSchemeString = String.Empty;
        public static String RundownEffectiveOverlayPowerSchemeString = String.Empty;

        // Default Overlay Scheme name
        public const String DefaultOverlayScheme = "OEM Default Overlay Scheme";

        // Fmax
        public const uint FMAX = 100000;

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

            // Min distance to another setting in the same profile
            // Pair (Setting Guid, MinDistance)
            public Tuple<Guid, int> acMinDistanceToSetting;
            public Tuple<Guid, int> dcMinDistanceToSetting;

            // Min distance to the setting of another profile
            // Pair (Profile Guid, MinDistance)
            public Tuple<Guid, int> acMinDistanceToProfile;
            public Tuple<Guid, int> dcMinDistanceToProfile;

            // Min distance to another setting in the same profile
            // Pair (Setting Guid, MinDistance)
            public Tuple<Guid, int> acMaxDistanceToSetting;
            public Tuple<Guid, int> dcMaxDistanceToSetting;

            // Max distance to the setting of another profile
            // Pair (Profile Guid, MaxDistance)
            public Tuple<Guid, int> acMaxDistanceToProfile;
            public Tuple<Guid, int> dcMaxDistanceToProfile;
        }

        // Data structure that stores the PPM setting values set in the ProfileSettingRundown ETL event
        // Maps PPM Setting GUID --> ProfileID --> (AC value , DC value)
        public static Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>> PowSettings 
            = new Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>>();

        // Hetero Parking Thresholds (HeteroIncreaseThreshold, HeteroDecreaseThreshold)
        // Maps PPM Setting GUID --> Concurrency level --> (AC value , DC value)
        public static Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>> HeteroSettings
            = new Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>>();

        // Data structure that stores the PPM profiles set in the ProfileRundown ETL event
        // Maps ProfileID --> (Name , Name, Guid)
        public static Dictionary<uint, Tuple<string, string, Guid>> ProfileNames = new Dictionary<uint, Tuple<string, string, Guid>>();

        // Local CSV generated by dumping powercfg caches a mapping of GUID to nice friendly setting name
        public static Dictionary<Guid, string> GuidToFriendlyName = new Dictionary<Guid, string>();

        // Local CSV generated by dumping powercfg caches a mapping of nice friendly name to GUID
        public static Dictionary<string, Guid> FriendlyNameToGuid = new Dictionary<string, Guid>();
        
        // Validation rules per profile and PPM setting
        // Maps ProfileGuid --> SettingGuid --> SettingValidationRules
        public static Dictionary<Guid, Dictionary<Guid, SettingValidationRules>> PerProfileValidationRules
            = new Dictionary<Guid, Dictionary<Guid, SettingValidationRules>>();

        // HeteroParking validation rules
        // Maps Setting --> Concurrency level --> SettingValidationRules
        public static Dictionary<Guid, Dictionary<uint, SettingValidationRules>> HeteroParkingValidationRules
            = new Dictionary<Guid, Dictionary<uint, SettingValidationRules>>();

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
                var targetCPU = GetRequiredArgument(args, "-t");

                // Handle invalid arguments
                if (inputFile != null && !File.Exists(inputFile))
                {
                    Console.Error.WriteLine("Failed to access input file. Please provide path to valid ETL.");
                    return -1;
                }

                // Process the input ETL trace
                AnalyzeTrace(inputFile, targetCPU, outputFile);

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
        /// <param name="targetCPU"> Target CPU string </param>
        /// <param name="outputPath"> Output results file </param>
        static void AnalyzeTrace(string tracePath, string targetCPU, string outputPath)
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

                // Guid to Friendly Name file
                try
                {
                    // Read from .csv file
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    string fileName = $"{assembly.GetName().Name}.{GUIDFriendlyNameFile}";

                    StreamReader reader = new StreamReader(assembly.GetManifestResourceStream(fileName));
                    IEnumerable<string> lines = reader.ReadToEnd().Split(new string[] { "\r\n" }, StringSplitOptions.None);

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
                    throw new Exception("Failed to read/open GuidToFriendlyName file.", ex);
                }

                // Friendly Name to Guid file
                try
                {
                    // Read from .csv file
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    string fileName = $"{assembly.GetName().Name}.{FriendlyNameGUIDFile}";

                    StreamReader reader = new StreamReader(assembly.GetManifestResourceStream(fileName));
                    IEnumerable<string> lines = reader.ReadToEnd().Split(new string[] { "\r\n" }, StringSplitOptions.None);

                    foreach (string line in lines)
                    {
                        if (line.Length > 0)
                        {
                            string friendlyname = line.Split(',')[0];
                            Guid guid = Guid.Parse(line.Split(',')[1]);
                            FriendlyNameToGuid.Add(friendlyname, guid);
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to read/open FriendlyNameToGuid file.", ex);
                }

                string QoSLevelName = "";

                // Extract metadata information
                string oemModel = systemMetadata.Result.Model;
                string oemName = systemMetadata.Result.Manufacturer;
                int buildNumber = systemMetadata.Result.OSVersion.Build;
                int buildRevision = systemMetadata.Result.OSVersion.Revision; 
                string processorModel = "";
                bool? isHybridSystem = null;

                // Let's enable this code later when Sylvain adds the right providers
                // The empty Try catch block is to circumvent a TraceData exception due to two data sources disagreeing on the number of processors
                try
                {
                    if (systemMetadata.Result.Processors.Count > 0)
                    {
                        processorModel = systemMetadata.Result.Processors[0].Name;

                        // If more than 1 type of core, then it is a hybrid system
                        isHybridSystem = systemMetadata.Result.Processors.GroupBy(x => x.EfficiencyClass).Distinct().Count() > 1 ? true : false;
                    }
                } catch (InvalidTraceDataException ex) { }

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

                        // Read all the setting Guids and values
                        else if (genericEvent.TaskName.Equals("ProfileSettingRundown"))
                        {
                            uint profileID = genericEvent.Fields[0].AsByte;
                            string name = genericEvent.Fields[1].AsString;
                            string type = genericEvent.Fields[2].EnumValue;
                            uint efficiencyClass = genericEvent.Fields[3].AsByte;
                            Guid guidString = genericEvent.Fields[4].AsGuid;
                            uint valueSize = genericEvent.Fields[5].AsUInt32;
                            uint value = 0;
                            uint[] heteroThresholds = new uint[4];

                            if (valueSize == 1)
                            {
                                value = genericEvent.Fields[6].AsBinary.ToArray()[0];
                            }
                            else if (valueSize == 4)
                            {
                                value = BitConverter.ToUInt32(genericEvent.Fields[6].AsBinary.ToArray(), 0);
                            }
                            else if (valueSize == 64)
                            {
                                // This setting if for hetero core parking thresholds
                                for (uint i = 0; i < 4; i++)
                                {
                                    value = genericEvent.Fields[6].AsBinary.ToArray()[i];
                                    heteroThresholds[i] = value;
                                }
                            }

                            if (!IsHeteroCoreParkingSetting(guidString)) 
                            {
                                // This is not a hetero parking setting

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
                            else
                            {
                                // Hetero Parking setting
                                if (!HeteroSettings.TryGetValue(guidString, out Dictionary<uint, Tuple<uint?, uint?>> heteroParkingThresholds))
                                {
                                    heteroParkingThresholds = new Dictionary<uint, Tuple<uint?, uint?>>();
                                    HeteroSettings[guidString] = heteroParkingThresholds;
                                }

                                for (uint concurrency_level = 1; concurrency_level <= 4; concurrency_level ++)
                                {
                                    uint threshold = heteroThresholds[concurrency_level - 1];

                                    if (!HeteroSettings[guidString].TryGetValue(concurrency_level, out Tuple<uint?, uint?> acdcValues))
                                    {
                                        if (type == "AC")
                                            acdcValues = new Tuple<uint?, uint?>(threshold, null);
                                        else
                                            acdcValues = new Tuple<uint?, uint?>(null, threshold);

                                        HeteroSettings[guidString].Add(concurrency_level, acdcValues);
                                    }
                                    else
                                    {
                                        uint? ac = null;
                                        uint? dc = null;

                                        if (type == "AC")
                                        {
                                            ac = threshold;
                                            dc = HeteroSettings[guidString][concurrency_level].Item2;
                                        }
                                        else
                                        {
                                            ac = HeteroSettings[guidString][concurrency_level].Item1;
                                            dc = threshold;
                                        }
                                        //Replace old tuple with new tuple with both values of AC and DC
                                        HeteroSettings[guidString].Remove(concurrency_level);
                                        HeteroSettings[guidString].Add(concurrency_level, new Tuple<uint?, uint?>(ac, dc));
                                    }
                                }
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

                // Open the XML file of the validation rules
                XmlDocument xmlRulesDoc;
                try
                {
                    xmlRulesDoc = new XmlDocument();
                    xmlRulesDoc.Load(PPMSettingRulesXMLFile);
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to read/open the XML file that sets the validation rules.", ex);
                }

                //Add results
                if (!File.Exists(outputPath))
                {
                    Results.Add("PPM Checker Tool");
                    Results.Add("Rule XML file version: " + xmlRulesDoc.SelectSingleNode("/PpmValidationRules").Attributes["version"].Value);
                    Results.Add("Rules Target CPU: " + targetCPU);
                    Results.Add("Date: " + System.DateTime.Now.ToString());
                    Results.Add("OEM Model: " + oemModel);
                    Results.Add("OEM Name: " + oemName);
                    Results.Add("Build Number: " + buildNumber + "." + buildRevision);
                    Results.Add("Processor Model: " + processorModel);
                    Results.Add("Is Hybrid System: " + isHybridSystem);
                    Results.Add("===============================================================================");
                }

                Results.Add("Rundown Power Scheme: " + RundownPowerSchemeString);
                Results.Add("Rundown Effective Power Overlay: " + RundownEffectiveOverlayPowerSchemeString);

                // Read the validation rules from the XML file
                ReadValidationRules(xmlRulesDoc, targetCPU);

                //Validate PPM settings
                ValidateNonHeteroPPMSettings();

                // Validate Non-hetero PPM settings
                ValidateHeteroPPMSettings();

                // Write the output results
                WriteOutput(Results, outputPath);
            }
        }

        /// <summary>
        /// This method reads the validation rules  
        /// The validation rules are specified in an XML file
        /// The method load the validation rules in the data structure ValidationRules
        /// </summary>
        /// <param name="doc"> XML doc that sets the validation rules </param>
        /// <param name="targetCpu"> Target CPU Id  </param>
        public static void ReadValidationRules(XmlDocument doc, string targetCpu)
        {
            // Extract the rules associated with the target CPU
            XmlNodeList cpuNodeList = doc.SelectNodes("/PpmValidationRules/TargetCPU");
            foreach (XmlNode cpuNode in cpuNodeList)
            {
                String cpuName = cpuNode.Attributes["name"].Value;
                if (cpuName != targetCpu) continue;

                // Iterate over the power overlays
                XmlNodeList overlayNodeList = cpuNode.SelectNodes("Overlay");
                foreach (XmlNode overlayNode in overlayNodeList)
                {
                    // Get the GUID of the Overlay 
                    String overlayName = overlayNode.Attributes["name"].Value;
                    Guid overlayGuid;
                    FriendlyNameToGuid.TryGetValue(overlayName, out overlayGuid);

                    // We load only the rules of the effective power overlay 
                    if (overlayGuid != RundownEffectiveOverlayPowerScheme)
                        continue;

                    // Get the list of profiles to validate
                    XmlNodeList profileNodeList = overlayNode.SelectNodes("Profile");

                    // Iterate over all the profiles
                    {
                        foreach (XmlNode profieNode in profileNodeList)
                        {
                            String profileName = profieNode.Attributes["name"].Value; ;
                            Guid profileGuid;
                            FriendlyNameToGuid.TryGetValue(profileName, out profileGuid);

                            List<Tuple<Guid, SettingValidationRules>> listPerSettingRules = new List<Tuple<Guid, SettingValidationRules>>();

                            // Get the list of settings to validate
                            XmlNodeList settingNodeList = profieNode.SelectNodes("Setting");

                            // Iterate over all the settings
                            foreach (XmlNode settingNode in settingNodeList)
                            {
                                // Get the GUID of the setting
                                String SettingName = settingNode.Attributes["name"].Value;
                                Guid settingGuid;
                                FriendlyNameToGuid.TryGetValue(SettingName, out settingGuid);

                                if (IsHeteroCoreParkingSetting(settingGuid))
                                {
                                    // Hetero Core parking setting
                                    ReadHeteroParkingSettingRules(settingGuid, settingNode);
                                }
                                else
                                {
                                    // Non-Hetero Core parking setting
                                    ReadNonHeteroParkingSettingRules(profileGuid, settingGuid, settingNode);
                                }
                            }
                        }
                    }
                }
                break;
            }
        }

        /// <summary>
        /// This method validates the PPM settings (Non Hetero) against the rules specified in the XML file:
        /// (1) For each profile, all the PPM settings set in the XML rules file are checked in (we consider the order of inheritance of the profiles)
        /// (2) The rules for setting's Min/Max/Absolute values are satisfied
        /// </summary>
        public static void ValidateNonHeteroPPMSettings()
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
                    Guid settingGuid = settingRules.Key;
                    SettingValidationRules rules = settingRules.Value;

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
                        Results.Add($"ERROR , {profileName} , {settingName} , AC/DC , NULL , Setting is not set in the profile and in any profile of the PPKG.");
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
                        Results.Add($"ERROR , {profileName} , {settingName} , AC , NULL , Setting is not set in the profile.");
                    }
                    else
                    {
                        // Validate against the validation rules 

                        // Case of Fmax = 0
                        if (IsFrequencyCapSetting(settingGuid) && acValue.Value == 0)
                        {
                            acValue = FMAX;
                        }

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

                        // Rule acMinDistanceToSetting
                        if (rules.acMinDistanceToSetting != null)
                        {
                            Guid targetSettingGuid = rules.acMinDistanceToSetting.Item1;
                            string targetSettingName;
                            GuidToFriendlyName.TryGetValue(targetSettingGuid, out targetSettingName);

                            // Get the distance value
                            int distance = rules.acMinDistanceToSetting.Item2;

                            // Calculate the distance to the target setting
                            int distanceToSetting = CalculateDistanceToSetting(acValue.Value, targetSettingGuid, profileGuid, true);
                            if (distanceToSetting == Int32.MinValue)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , AC , {acValue.Value} , Distance to setting {targetSettingName} should be >= {distance}. Setting {targetSettingName} was not set");

                            }
                            else if (distanceToSetting < distance)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , AC , {acValue.Value} , Distance to setting {targetSettingName} should be >= {distance}.");
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
                                Results.Add($"ERROR , {profileName} , {settingName} , AC , {acValue.Value} , Distance to profile {refProfileName} should be >= {distance}.");
                            }
                        }

                        // Rule acMaxDistanceToSetting
                        if (rules.acMaxDistanceToSetting != null)
                        {
                            Guid targetSettingGuid = rules.acMaxDistanceToSetting.Item1;
                            string targetSettingName;
                            GuidToFriendlyName.TryGetValue(targetSettingGuid, out targetSettingName);
                            int distance = rules.acMaxDistanceToSetting.Item2;

                            // Calculate the distance to the target setting
                            int distanceToSetting = CalculateDistanceToSetting(acValue.Value, targetSettingGuid, profileGuid, true);
                            if (distanceToSetting == Int32.MinValue)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , AC , {acValue.Value} , Distance to setting {targetSettingName} should be <= {distance}. Setting {targetSettingName} was not set");

                            }
                            else if (distanceToSetting > distance)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , AC , {acValue.Value} , Distance to setting {targetSettingName} should be <= {distance}.");
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
                                Results.Add($"ERROR , {profileName} , {settingName} , AC , {acValue.Value} , Distance to profile {refProfileName} should be <= {distance}.");
                            }
                        }
                    }

                    // Check the DC mode setting 
                    if (!dcValue.HasValue)
                    {
                        // The setting doesn’t have a value
                        Results.Add($"ERROR , {profileName} , {settingName} , DC , NULL , Setting is not set in the profile.");
                    }
                    else
                    {
                        // Validate against the validation rules 

                        // Case of Fmax = 0
                        if (IsFrequencyCapSetting(settingGuid) && dcValue.Value == 0)
                        {
                            dcValue = FMAX;
                        }

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

                        // Rule dcMinDistanceToSetting
                        if (rules.dcMinDistanceToSetting != null)
                        {
                            Guid targetSettingGuid = rules.dcMinDistanceToSetting.Item1;
                            string targetSettingName;
                            GuidToFriendlyName.TryGetValue(targetSettingGuid, out targetSettingName);

                            // Get the distance value
                            int distance = rules.dcMinDistanceToSetting.Item2;

                            // Calculate the distance to the target setting
                            int distanceToSetting = CalculateDistanceToSetting(dcValue.Value, targetSettingGuid, profileGuid, false);
                            if (distanceToSetting == Int32.MinValue)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , DC , {dcValue.Value} , Distance to setting {targetSettingName} should be >= {distance}. Setting {targetSettingName} was not set");

                            }
                            else if (distanceToSetting < distance)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , DC , {dcValue.Value} , Distance to setting {targetSettingName} should be >= {distance}.");
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
                                Results.Add($"ERROR , {profileName} , {settingName} , DC , {dcValue.Value} , Distance to profile {refProfileName} should be >= {distance}.");
                            }
                        }

                        // Rule dcMaxDistanceToSetting
                        if (rules.dcMaxDistanceToSetting != null)
                        {
                            Guid targetSettingGuid = rules.dcMaxDistanceToSetting.Item1;
                            string targetSettingName;
                            GuidToFriendlyName.TryGetValue(targetSettingGuid, out targetSettingName);
                            int distance = rules.dcMaxDistanceToSetting.Item2;

                            // Calculate the distance to the target setting
                            int distanceToSetting = CalculateDistanceToSetting(dcValue.Value, targetSettingGuid, profileGuid, false);
                            if (distanceToSetting == Int32.MinValue)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , DC , {dcValue.Value} , Distance to setting {targetSettingName} should be <= {distance}. Setting {targetSettingName} was not set");

                            }
                            else if (distanceToSetting > distance)
                            {
                                Results.Add($"ERROR , {profileName} , {settingName} , DC , {dcValue.Value} , Distance to setting {targetSettingName} should be <= {distance}.");
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
                                Results.Add($"ERROR , {profileName} , {settingName} , DC , {dcValue.Value} , Distance to profile {refProfileName} should be <= {distance}.");
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// This method validates the Hetero Core parking thresholds (HeteroIncreaseThreshold, HeteroDecreaseThreshold) against the rules specified in the XML file:
        /// </summary>

        public static void ValidateHeteroPPMSettings()
        {
            // Iterate Over all the rules
            foreach (var settingRules in HeteroParkingValidationRules)
            {
                Guid settingGuid = settingRules.Key;
                string settingName;

                // Get the friendly name of the PPM setting
                GuidToFriendlyName.TryGetValue(settingGuid, out settingName);

                // Validate that the setting is set for any profile 
                if (!HeteroSettings.ContainsKey(settingGuid))
                {
                    Results.Add($"ERROR , {"Profile_Default"} , {settingName} , AC/DC , NULL , Setting is not set in the PPKG.");
                    continue;
                }

                foreach (var concurrencyRules in settingRules.Value)
                {
                    // Get the concurrency level and its associated rules
                    uint concurrencyLevel = concurrencyRules.Key;
                    SettingValidationRules rules = concurrencyRules.Value;

                    if (!HeteroSettings[settingGuid].ContainsKey(concurrencyLevel))
                    {
                        Results.Add($"ERROR , {"Profile_Default"} , {settingName} , AC/DC , NULL , Concurrency level {concurrencyLevel} not set.");
                        continue;
                    }

                    uint? ppkgThreholdAc = HeteroSettings[settingGuid][concurrencyLevel].Item1;
                    uint? ppkgThreholdDc = HeteroSettings[settingGuid][concurrencyLevel].Item2;

                    if (rules.acValue.HasValue) 
                    {
                        if(!ppkgThreholdAc.HasValue)
                        {
                            Results.Add($"ERROR , {"Profile_Default"} , {settingName} , AC , NULL , Concurrency level {concurrencyLevel} not set. Value should be = {rules.acValue.Value}");
                        }
                        else
                        {
                            if (ppkgThreholdAc.Value != rules.acValue.Value)
                            {
                                Results.Add($"ERROR , {"Profile_Default"} , {settingName}, AC , {ppkgThreholdAc.Value} , Value of oncurrency level {concurrencyLevel} should be = {rules.acValue.Value}");
                            }
                        }
                    }

                    if (rules.acMinValue.HasValue)
                    {
                        if (!ppkgThreholdAc.HasValue)
                        {
                            Results.Add($"ERROR , {"Profile_Default"} , {settingName} , AC , NULL , Concurrency level {concurrencyLevel} not set. Value should be >= {rules.acMinValue.Value}");
                        }
                        else
                        {
                            if (ppkgThreholdAc.Value < rules.acMinValue.Value)
                            {
                                Results.Add($"ERROR , {"Profile_Default"} , {settingName}, AC , {ppkgThreholdAc.Value} , Value of oncurrency level {concurrencyLevel} should be >= {rules.acMinValue.Value}");
                            }
                        }
                    }

                    if (rules.acMaxValue.HasValue)
                    {
                        if (!ppkgThreholdAc.HasValue)
                        {
                            Results.Add($"ERROR , {"Profile_Default"} , {settingName} , AC , NULL , Concurrency level {concurrencyLevel} not set. Value should be <= {rules.acMaxValue.Value}");
                        }
                        else
                        {
                            if (ppkgThreholdAc.Value > rules.acMaxValue.Value)
                            {
                                Results.Add($"ERROR , {"Profile_Default"} , {settingName} , AC , {ppkgThreholdAc.Value} , Value of oncurrency level {concurrencyLevel} should be <= {rules.acMaxValue.Value}");
                            }
                        }
                    }

                    if (rules.dcValue.HasValue)
                    {
                        if (!ppkgThreholdDc.HasValue)
                        {
                            Results.Add($"ERROR , {"Profile_Default"} , {settingName} , DC , NULL , Concurrency level {concurrencyLevel} not set. Value should be = {rules.dcValue.Value}");
                        }
                        else
                        {
                            if (ppkgThreholdDc.Value != rules.dcValue.Value)
                            {
                                Results.Add($"ERROR , {"Profile_Default"} , {settingName} , DC , {ppkgThreholdDc.Value} , Value of oncurrency level {concurrencyLevel} should be = {rules.dcValue.Value}");
                            }
                        }
                    }

                    if (rules.dcMinValue.HasValue)
                    {
                        if (!ppkgThreholdDc.HasValue)
                        {
                            Results.Add($"ERROR , {"Profile_Default"} , {settingName} , DC , NULL , Concurrency level {concurrencyLevel} not set. Value should be >= {rules.dcMinValue.Value}");
                        }
                        else
                        {
                            if (ppkgThreholdDc.Value < rules.dcMinValue.Value)
                            {
                                Results.Add($"ERROR , {"Profile_Default"} , {settingName} , DC , {ppkgThreholdDc.Value} , Value of oncurrency level {concurrencyLevel} should be >= {rules.dcMinValue.Value}");
                            }
                        }
                    }

                    if (rules.dcMaxValue.HasValue)
                    {
                        if (!ppkgThreholdDc.HasValue)
                        {
                            Results.Add($"ERROR , {"Profile_Default"} , {settingName} , DC , NULL , Concurrency level {concurrencyLevel} not set. Value should be <= {rules.dcMaxValue.Value}");
                        }
                        else
                        {
                            if (ppkgThreholdDc.Value > rules.dcMaxValue.Value)
                            {
                                Results.Add($"ERROR , {"Profile_Default"} , {settingName} , DC , {ppkgThreholdDc.Value} , Value of oncurrency level {concurrencyLevel} should be <= {rules.dcMaxValue.Value}");
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
        /// Calculate the distance between two different settings  
        /// The two settings should be in the same profile
        /// E.g. Fmax for P-cores, vs. Fmax for E-cores
        /// </summary>
        /// <param name="settingValue"> Value the first setting </param>
        /// <param name="targetSettingGuid"> The setting's guid that we will use to measure the distance </param>
        /// <param name="profileGuid"> GUID of the profile of the two settings</param>
        /// <param name="isAC"> AC/DC mode </param>
        /// <returns> the distance between the two settings or Int32.MinValue </returns>
        public static int CalculateDistanceToSetting(uint settingValue, Guid targetSettingGuid, Guid profileGuid, bool isAC)
        {
            // Get the value of the setting that we will use to measure the distance
            var profileId = ProfileNames.FirstOrDefault(x => x.Value.Item3 == profileGuid).Key;

            uint? targetSettingValue = null;

            if (PowSettings.ContainsKey(targetSettingGuid))
            {
                if (PowSettings[targetSettingGuid].ContainsKey(profileId))
                {
                    if (isAC)
                    {
                        targetSettingValue = PowSettings[targetSettingGuid][profileId].Item1;
                    }
                    else
                    {
                        targetSettingValue = PowSettings[targetSettingGuid][profileId].Item2;
                    }
                }
                else
                {
                    // The setting was not set for the profileGuid 
                    // Check if profileGuid inherits the settings from another profile

                    Guid? refProfileParentGuid = null;
                    FindSettingInParentProfiles(isAC, profileGuid, targetSettingGuid, ref refProfileParentGuid, ref targetSettingValue);
                }
            }

            // Check if we found a value for the setting that will use to measure the distance
            if (targetSettingValue.HasValue)
            {
                // Case of Fmax = 0
                if (IsFrequencyCapSetting(targetSettingGuid) && targetSettingValue.Value == 0)
                {
                    targetSettingValue = FMAX;
                }

                // Calculate the distance between the two settings 
                int distance = (int)settingValue - (int)targetSettingValue.Value;
                return distance;
            }

            return Int32.MinValue;
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

            // Case of Fmax = 0
            if(IsFrequencyCapSetting(settingGuid) && refProfileSettingValue.Value == 0)
            {
                refProfileSettingValue = FMAX;
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

        /// <summary>
        /// Writes the command line usage, arguments and examples to the console
        /// </summary>
        static void OutputCommandLineUsage()
        {
            Console.Out.WriteLine("Usage: " + System.AppDomain.CurrentDomain.FriendlyName + " -i <trace.etl>  -o <output.csv/txt>  -t <target CPU>");
            Console.Out.WriteLine("Possible target CPUs: ADL_U, ADL_H, ADL_P, RPL_U, RPL_P, MTL_H, MTL_U ...");
            Console.Out.WriteLine("Please confirm if the PPM Rules XML File has the definitions for the target CPU");
        }

        /// <summary>
        /// Helper function 
        /// Create a vector and put in it the numbers from a string that has a sequence of them 
        /// </summary>
        /// <param name="numbersString"> String of numbers</param>
        /// <returns> a vector of numbers or NULL </returns>
        static uint[] GetNumbersFromString(string numbersString)
        {
            uint[] numbers = null;
            string[] numberStrings = numbersString.Trim().Split(' ');
            numbers = new uint[numberStrings.Length];

            for (int i = 0; i < numberStrings.Length; i++)
            {
                if (int.TryParse(numberStrings[i], out int parsedNumber))
                {
                    numbers[i] = (uint)parsedNumber;
                }
            }

            return numbers;
        }

        /// <summary>
        /// Helper function 
        /// Verify if a setting is among the heterosexual core parking settings 
        /// </summary>
        /// <param name="settingGuid"> GUID of the setting</param>
        /// <returns> True is the setting is a hetero core parking setting </returns>

        static bool IsHeteroCoreParkingSetting(Guid settingGuid)
        {
            return (settingGuid == GuidHeteroDecreaseThreshold ||
                    settingGuid == GuidHeteroDecreaseThreshold1 ||
                    settingGuid == GuidHeteroIncreaseThreshold ||
                    settingGuid == GuidHeteroIncreaseThreshold1);
        }

        /// <summary>
        /// Helper function 
        /// Verify if a setting is one of the settings to cap the frequency
        /// </summary>
        /// <param name="settingGuid"> GUID of the setting</param>
        /// <returns> True is the setting is a frequency cap setting </returns>
        static bool IsFrequencyCapSetting(Guid settingGuid)
        {
            return (settingGuid == GuidClass0Fmax ||
                    settingGuid == GuidClass1Fmax);
        }

        /// <summary>
        /// Read from the XML file the validation rules of a given hetero core parking setting  
        /// </summary>
        /// <param name="settingGuid"> GUID of the setting</param>
        /// <param name="settingNode"> Node of the setting in the XML file</param>
        static void ReadHeteroParkingSettingRules(Guid settingGuid, XmlNode settingNode)
        {
            Dictionary<uint, SettingValidationRules> settingRulesByConcurrencyLevel = new Dictionary<uint, SettingValidationRules>();

            for (uint concurrency = 1; concurrency <= 4; concurrency++)
            {
                SettingValidationRules rules = new SettingValidationRules();
                settingRulesByConcurrencyLevel.Add(concurrency, rules);
            }

            // Value in AC mode
            if (settingNode["AcValue"] != null)
            {
                uint[] threasholds = GetNumbersFromString(settingNode["AcValue"].InnerText);
                if (threasholds != null)
                {
                    uint concurrency = 0;
                    foreach (uint thd in threasholds)
                    {
                        concurrency++;
                        var rules = settingRulesByConcurrencyLevel[concurrency];
                        rules.acValue = thd;
                        settingRulesByConcurrencyLevel[concurrency] = rules;
                    }
                }
            }

            // Min bound in AC mode
            if (settingNode["AcMinValue"] != null)
            {
                uint[] threasholds = GetNumbersFromString(settingNode["AcMinValue"].InnerText);
                if (threasholds != null)
                {
                    uint concurrency = 0;
                    foreach (uint thd in threasholds)
                    {
                        concurrency++;
                        var rules = settingRulesByConcurrencyLevel[concurrency];
                        rules.acMinValue = thd;
                        settingRulesByConcurrencyLevel[concurrency] = rules;
                    }
                }
            }

            // Max bound in AC mode
            if (settingNode["AcMaxValue"] != null)
            {
                uint[] threasholds = GetNumbersFromString(settingNode["AcMaxValue"].InnerText);
                if (threasholds != null)
                {
                    uint concurrency = 0;
                    foreach (uint thd in threasholds)
                    {
                        concurrency++;
                        var rules = settingRulesByConcurrencyLevel[concurrency];
                        rules.acMaxValue = thd;
                        settingRulesByConcurrencyLevel[concurrency] = rules;
                    }
                }
            }

            // Value in DC mode
            if (settingNode["DcValue"] != null)
            {
                uint[] threasholds = GetNumbersFromString(settingNode["DcValue"].InnerText);
                if (threasholds != null)
                {
                    uint concurrency = 0;
                    foreach (uint thd in threasholds)
                    {
                        concurrency++;
                        var rules = settingRulesByConcurrencyLevel[concurrency];
                        rules.dcValue = thd;
                        settingRulesByConcurrencyLevel[concurrency] = rules;
                    }
                }
            }

            // Min bound in DC mode
            if (settingNode["DcMinValue"] != null)
            {
                uint[] threasholds = GetNumbersFromString(settingNode["DcMinValue"].InnerText);
                if (threasholds != null)
                {
                    uint concurrency = 0;
                    foreach (uint thd in threasholds)
                    {
                        concurrency++;
                        var rules = settingRulesByConcurrencyLevel[concurrency];
                        rules.dcMinValue = thd;
                        settingRulesByConcurrencyLevel[concurrency] = rules;
                    }
                }
            }

            // Max bound in DC mode
            if (settingNode["DcMaxValue"] != null)
            {
                uint[] threasholds = GetNumbersFromString(settingNode["DcMaxValue"].InnerText);
                if (threasholds != null)
                {
                    uint concurrency = 0;
                    foreach (uint thd in threasholds)
                    {
                        concurrency++;
                        var rules = settingRulesByConcurrencyLevel[concurrency];
                        rules.dcMaxValue = thd;
                        settingRulesByConcurrencyLevel[concurrency] = rules;
                    }
                }
            }

            // Add the hetero parking validation rules 
            HeteroParkingValidationRules.Add(settingGuid, settingRulesByConcurrencyLevel);
        }

        /// <summary>
        /// Read from the XML file the validation rules of a given Non-hetero core parking setting  
        /// </summary>
        /// <param name="settingGuid"> GUID of the setting</param>
        /// <param name="settingNode"> Node of the setting in the XML file</param>

        static void ReadNonHeteroParkingSettingRules(Guid profileGuid, Guid settingGuid, XmlNode settingNode)
        {
            SettingValidationRules settingValidationRules = new SettingValidationRules();

            if (settingNode["AcValue"] != null)
            {
                settingValidationRules.acValue = Convert.ToUInt32(settingNode["AcValue"].InnerText);
            }

            // Min bound in AC mode
            if (settingNode["AcMinValue"] != null)
            {
                settingValidationRules.acMinValue = Convert.ToUInt32(settingNode["AcMinValue"].InnerText);
            }

            // Max bound in AC mode
            if (settingNode["AcMaxValue"] != null)
            {
                settingValidationRules.acMaxValue = Convert.ToUInt32(settingNode["AcMaxValue"].InnerText);
            }

            // Min Distance to profile in AC mode
            if (settingNode["AcMinDistanceToProfile"] != null)
            {
                if (settingNode["AcMinDistanceToProfile"]["Profile"] != null && settingNode["AcMinDistanceToProfile"]["Distance"] != null)
                {
                    String refProfileName = settingNode["AcMinDistanceToProfile"]["Profile"].InnerText.Replace(" ", "");
                    Guid refProfileGuid;
                    FriendlyNameToGuid.TryGetValue(refProfileName, out refProfileGuid);

                    int distance = Convert.ToInt32(settingNode["AcMinDistanceToProfile"]["Distance"].InnerText);
                    settingValidationRules.acMinDistanceToProfile = new Tuple<Guid, int>(refProfileGuid, distance);
                }
            }

            // Min Distance to another setting in AC mode
            if (settingNode["AcMinDistanceToSetting"] != null)
            {
                if (settingNode["AcMinDistanceToSetting"]["Setting"] != null && settingNode["AcMinDistanceToSetting"]["Distance"] != null)
                {
                    String refSettingeName = settingNode["AcMinDistanceToSetting"]["Setting"].InnerText.Replace(" ", "");
                    Guid refSettingGuid;
                    FriendlyNameToGuid.TryGetValue(refSettingeName, out refSettingGuid);

                    int distance = Convert.ToInt32(settingNode["AcMinDistanceToSetting"]["Distance"].InnerText);
                    settingValidationRules.acMinDistanceToSetting = new Tuple<Guid, int>(refSettingGuid, distance);
                }
            }

            // Max Distance to profile in AC mode
            if (settingNode["AcMaxDistanceToProfile"] != null)
            {
                if (settingNode["AcMaxDistanceToProfile"]["Profile"] != null && settingNode["AcMaxDistanceToProfile"]["Distance"] != null)
                {
                    String refProfileName = settingNode["AcMaxDistanceToProfile"]["Profile"].InnerText.Replace(" ", "");
                    Guid refProfileGuid;
                    FriendlyNameToGuid.TryGetValue(refProfileName, out refProfileGuid);

                    int distance = Convert.ToInt32(settingNode["AcMaxDistanceToProfile"]["Distance"].InnerText);
                    settingValidationRules.acMaxDistanceToProfile = new Tuple<Guid, int>(refProfileGuid, distance);
                }
            }

            // Max Distance to another setting in AC mode
            if (settingNode["AcMaxDistanceToSetting"] != null)
            {
                if (settingNode["AcMaxDistanceToSetting"]["Setting"] != null && settingNode["AcMaxDistanceToSetting"]["Distance"] != null)
                {
                    String refSettingeName = settingNode["AcMaxDistanceToSetting"]["Setting"].InnerText.Replace(" ", "");
                    Guid refSettingGuid;
                    FriendlyNameToGuid.TryGetValue(refSettingeName, out refSettingGuid);

                    int distance = Convert.ToInt32(settingNode["AcMaxDistanceToSetting"]["Distance"].InnerText);
                    settingValidationRules.acMaxDistanceToSetting = new Tuple<Guid, int>(refSettingGuid, distance);
                }
            }

            // Value in DC mode
            if (settingNode["DcValue"] != null)
            {
                settingValidationRules.dcValue = Convert.ToUInt32(settingNode["DcValue"].InnerText);
            }

            // Min bound in DC mode
            if (settingNode["DcMinValue"] != null)
            {
                settingValidationRules.dcMinValue = Convert.ToUInt32(settingNode["DcMinValue"].InnerText);
            }

            // Max bound in DC mode
            if (settingNode["DcMaxValue"] != null)
            {
                settingValidationRules.dcMaxValue = Convert.ToUInt32(settingNode["DcMaxValue"].InnerText);
            }

            // Min Distance to profile in DC mode
            if (settingNode["DcMinDistanceToProfile"] != null)
            {
                if (settingNode["DcMinDistanceToProfile"]["Profile"] != null && settingNode["DcMinDistanceToProfile"]["Distance"] != null)
                {
                    String refProfileName = settingNode["DcMinDistanceToProfile"]["Profile"].InnerText.Replace(" ", "");
                    Guid refProfileGuid;
                    FriendlyNameToGuid.TryGetValue(refProfileName, out refProfileGuid);

                    int distance = Convert.ToInt32(settingNode["DcMinDistanceToProfile"]["Distance"].InnerText);
                    settingValidationRules.dcMinDistanceToProfile = new Tuple<Guid, int>(refProfileGuid, distance);
                }
            }

            // Min Distance to another setting in DC mode
            if (settingNode["DcMinDistanceToSetting"] != null)
            {
                if (settingNode["DcMinDistanceToSetting"]["Setting"] != null && settingNode["DcMinDistanceToSetting"]["Distance"] != null)
                {
                    String refSettingeName = settingNode["DcMinDistanceToSetting"]["Setting"].InnerText.Replace(" ", "");
                    Guid refSettingGuid;
                    FriendlyNameToGuid.TryGetValue(refSettingeName, out refSettingGuid);

                    int distance = Convert.ToInt32(settingNode["DcMinDistanceToSetting"]["Distance"].InnerText);
                    settingValidationRules.dcMinDistanceToSetting = new Tuple<Guid, int>(refSettingGuid, distance);
                }
            }

            // Max Distance to profile in DC mode
            if (settingNode["DcMaxDistanceToProfile"] != null)
            {
                if (settingNode["DcMaxDistanceToProfile"]["Profile"] != null && settingNode["DcMaxDistanceToProfile"]["Distance"] != null)
                {
                    String refProfileName = settingNode["DcMaxDistanceToProfile"]["Profile"].InnerText.Replace(" ", "");
                    Guid refProfileGuid;
                    FriendlyNameToGuid.TryGetValue(refProfileName, out refProfileGuid);

                    int distance = Convert.ToInt32(settingNode["DcMaxDistanceToProfile"]["Distance"].InnerText);
                    settingValidationRules.dcMaxDistanceToProfile = new Tuple<Guid, int>(refProfileGuid, distance);
                }
            }

            // Max Distance to another setting in DC mode
            if (settingNode["DcMaxDistanceToSetting"] != null)
            {
                if (settingNode["DcMaxDistanceToSetting"]["Setting"] != null && settingNode["DcMaxDistanceToSetting"]["Distance"] != null)
                {
                    String refSettingeName = settingNode["DcMaxDistanceToSetting"]["Setting"].InnerText.Replace(" ", "");
                    Guid refSettingGuid;
                    FriendlyNameToGuid.TryGetValue(refSettingeName, out refSettingGuid);

                    int distance = Convert.ToInt32(settingNode["DcMaxDistanceToSetting"]["Distance"].InnerText);
                    settingValidationRules.dcMaxDistanceToSetting = new Tuple<Guid, int>(refSettingGuid, distance);
                }
            }

            // Add the validation rules 
            if (!PerProfileValidationRules.ContainsKey(profileGuid))
            {
                PerProfileValidationRules.Add(profileGuid, new Dictionary<Guid, SettingValidationRules> { { settingGuid, settingValidationRules } });
            }
            else
            {
                if (!PerProfileValidationRules[profileGuid].ContainsKey(settingGuid))
                {
                    PerProfileValidationRules[profileGuid].Add(settingGuid, settingValidationRules);
                }
                else
                {
                    PerProfileValidationRules[profileGuid][settingGuid] = settingValidationRules;
                }
            }
        }
    }
}