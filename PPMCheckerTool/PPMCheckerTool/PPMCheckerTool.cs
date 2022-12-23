/*++

Copyright (c) 2022 Microsoft Corporation

Module Name:

    PPMCheckerTool.cs

Abstract:

    What does this tool do?

        TODO: Empty example project. Clone to create additional tools.

    This tool uses WPA Data Layer (https://osgwiki.com/wiki/WPA_Data_Layer).

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
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Symbols;
using Common;
using System.Reflection;

namespace PPMCheckerTool
{
    class PPMCheckerTool : BaseAnalyzer
    {
        private const String GUIDFriendlyNameFile = "GuidToFriendlyName.csv";
        public const String NoOverlay = "No Overlay";

        // PPM settings GUIDs
        public static Guid GuidEPP = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        public static Guid GuidFrequencyCap = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
        public static Guid GuidSchedulingPolicy = new Guid("93b8b6dc-0698-4d1c-9ee4-0644e900c85d");
        public static Guid GuidShortSchedulingPolicy = new Guid("bae08b81-2d5e-4688-ad6a-13243356654b");
        public static Guid GuidCPMinCores = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");
        public static Guid GuidCPMaxCores = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334028");

        // Power profiles GUIDs
        public static Guid GuidDefault = new Guid("00000000-0000-0000-0000-000000000000");
        public static Guid GuidLowQoS = new Guid("c04a802d-2205-4910-ae98-3b51e3bb72f2");
        public static Guid GuidEcoQoS = new Guid("336c7511-f109-4172-bb3a-3ea51f815ada");
        public static Guid GuidUtilityQoS = new Guid("33cc3a0d-45ee-43ca-86c4-695bfc9a313b");
        public static Guid GuidConstrained = new Guid("ee1e4f72-e368-46b1-b3c6-5048b11c2dbd");
        public static Guid GuidStandby = new Guid("8bc6262c-c026-411d-ae3b-7e2f70811a13");

        // GUIDs of all the settings to validate
        static public List<Guid> GuidPPMSettings = new List<Guid>
        {
            GuidEPP,                    
            GuidFrequencyCap,           
            GuidSchedulingPolicy,       
            GuidShortSchedulingPolicy, 
            GuidCPMinCores,       
            GuidCPMaxCores
        };

        // GUIDs of all the profiles to validate
        static public List<Guid> GuidPPMProfiles = new List<Guid>
        {
            GuidDefault,
            GuidLowQoS,
            GuidEcoQoS,
            GuidUtilityQoS,
            GuidConstrained,
            GuidStandby
        };

        // Class to flag objective errors in Qos
        public class ProcessoPolicyValidationFlags
        {
            public Dictionary<string, int> FreqPolicyAC;
            public Dictionary<string, int> FreqPolicyDC;

            public Dictionary<string, int> QoSPolicyAC;
            public Dictionary<string, int> QoSPolicyDC;

            public Dictionary<string, int> EPPAcDc;

            public ProcessoPolicyValidationFlags()
            {
                FreqPolicyAC = new Dictionary<string, int>();
                FreqPolicyDC = new Dictionary<string, int>();

                QoSPolicyAC = new Dictionary<string, int>();
                QoSPolicyDC = new Dictionary<string, int>();
                EPPAcDc = new Dictionary<string, int>();
            }
        }

        // Class for settings values
        public class SettingValues
        {
            public uint? DC;
            public uint? AC;
            public string? altitudeDC;
            public string? altitudeAC;
        }

        [Flags]
        public enum PPM_PERF_QOS_DISABLE_REASON
        {
            None = 0,
            PpmPerfQosDisableInternal = 1,              // Legacy                   The NtPowerInformation API to explicitly disable BAM-PPM
            PpmPerfQosDisableNoProfile = 2,             // No Povisioning package   None of the policy settings relevant to BAM-PPM have been configured (provisioning package missing).
            PpmPerfQosDisableNoPolicy = 4,              // On AC                    None of the policy settings relevant to BAM-PPM have been configured (on AC).
            PpmPerfQosDisableInsufficientPolicy = 8,    // Slider @ MaxPerf         There are BAM-PPM setting configured, but they aren’t any more restrictive than normal settings 
            PpmPerfQosDisableMaxOverride = 16,          // Boot Perf Boost          A component has requested the system run all-out (this happens for a short period of time after boot, sleep/hibernate, or CS exit)
            PpmPerfQosDisableLowLatency = 32,           // Low Latency
            PpmPerfQosDisableSmtScheduler = 64,         // The heterogeneous scheduler is disabled for some reason (usually due to No Profile or No Hardware Support)
            PpmPerfQosDisableNoHardwareSupport = 128,   // The platform’s perf state implementation isn’t compatible with BAM-PPM
            //PpmPerfQosDisableMax,
        };

        public enum QOS_LEVEL
        {
            High = 0,
            Medium = 1,
            Low = 2,
            Utility = 3,
            Eco = 4,
            Multimedia = 5,
            enum_Max = Multimedia
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

                // Validation flags
                ProcessoPolicyValidationFlags flags = new ProcessoPolicyValidationFlags();
                SortedList<QOS_LEVEL, Tuple<uint?, uint?>> qosValues = new SortedList<QOS_LEVEL, Tuple<uint?, uint?>>();

                // Maps PPM Setting --> Profile --> (AC value , DC value)
                Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>> powSettings = new Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>>();
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
                            if (RundownEffectiveOverlayPowerScheme == Guid.Empty) // No Overlay
                            {
                                RundownEffectiveOverlayPowerSchemeString = NoOverlay;
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

                // Validate that they key PPM settings are checked in
                CheckPPMSettings(powSettings, profileNames, GuidToFriendlyName, results);

                // Validate QoS Order 
                ValidateQoSOrder(profileNames, flags, powSettings, qosValues, results);

                WriteOutput(results, outputPath, true); // Always No header for now
            }
        }

        /// <summary>
        /// This method validates that all the "key" PPM settings for Power and Perf are present across all the profiles 
        /// 
        /// </summary>
        public static void CheckPPMSettings(Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>> powSettings,
            Dictionary<uint, Tuple<string, string, Guid>> profileNames,
            Dictionary<Guid, string> GuidToFriendlyName,
            List<string> results)
        {
            // List of waivers ( we can accept in some cases when a specific PPM setting is not used for a specific profile)
            // Each element in the waiver list is a pair PPMSetting-Profile
            List<Tuple<Guid, Guid>> waivers = new List<Tuple<Guid, Guid>>();
            waivers.Add(new Tuple<Guid, Guid>(GuidCPMinCores, GuidLowQoS));
            waivers.Add(new Tuple<Guid, Guid>(GuidCPMinCores, GuidEcoQoS));
            waivers.Add(new Tuple<Guid, Guid>(GuidCPMinCores, GuidUtilityQoS));
            waivers.Add(new Tuple<Guid, Guid>(GuidCPMaxCores, GuidLowQoS));
            waivers.Add(new Tuple<Guid, Guid>(GuidCPMaxCores, GuidEcoQoS));
            waivers.Add(new Tuple<Guid, Guid>(GuidCPMaxCores, GuidUtilityQoS));

            // Iterate across all the PPM settings 
            foreach (Guid settingGuid in GuidPPMSettings)
            {
                string settingName;
                GuidToFriendlyName.TryGetValue(settingGuid, out settingName);

                if (!powSettings.ContainsKey(settingGuid))
                {
                    results.Add(String.Format("Setting: {0} was not used for any of the profiles", settingName));
                    continue;
                }

                // Iterate across all the profiles
                foreach (Guid profileGuid in GuidPPMProfiles)
                {
                    string profileName;
                    GuidToFriendlyName.TryGetValue(profileGuid, out profileName);

                    // Check if there is a waiver for this case
                    Tuple<Guid, Guid> pairSettingProfile = new Tuple<Guid, Guid>(settingGuid, profileGuid);
                    if (waivers.Contains(pairSettingProfile))
                    {
                        continue;
                    }

                    // Check if this profile is supported
                    var profile_id = profileNames.FirstOrDefault(x => x.Value.Item3 == profileGuid).Key;
                    if (profile_id == null)
                    {
                        results.Add(String.Format("Profile {0} not supported", profileName));
                        continue;
                    }

                    // Check if this profile is configured
                    if (!powSettings[settingGuid].ContainsKey(profile_id))
                    {
                        results.Add(String.Format("Setting {0} not configured for profile {1}", settingName, profileNames[profile_id].Item1));
                        continue;
                    }

                    // Check AC mode is configured 
                    if (powSettings[settingGuid][profile_id].Item1 == null)
                    {
                        results.Add(String.Format("Setting {0} not configured for profile {1} in AC mode", settingName, profileNames[profile_id].Item1));
                    }
                    // Check DC mode is configured
                    if (powSettings[settingGuid][profile_id].Item2 == null)
                    {
                        results.Add(String.Format("Setting {0} not configured for profile {1} in DC mode", settingName, profileNames[profile_id].Item1));
                    }
                }
            }
        }

        /// <summary>
        /// This method validates that each QoS level values are higher or equal to the level bellow
        /// QoS Level orders High (0) > Medium (9) > Low (10) > Utility (6) > Eco (5) (Multimedia (2), Deadline (12))
        ///
        /// </summary>
        public static void ValidateQoSOrder(Dictionary<uint, Tuple<string, string, Guid>> profileNames, ProcessoPolicyValidationFlags flags, Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>> powSettings, SortedList<QOS_LEVEL, Tuple<uint?, uint?>> qosValues, List<string> results)
        {
            Guid perfEppGuid = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
            Dictionary<uint, Tuple<uint?, uint?>> energyPerfPreference;
            bool foundIssue = false;

            powSettings.TryGetValue(perfEppGuid, out energyPerfPreference);

            if (energyPerfPreference != null && energyPerfPreference.Count != 0)
            {
                foreach (var profile in energyPerfPreference)
                {
                    if (profileNames.ContainsKey(profile.Key))
                    {
                        switch (profileNames[profile.Key].Item2)
                        {
                            case "HighQoS":
                                qosValues.Add(QOS_LEVEL.High, profile.Value);
                                break;
                            case "MediumQoS":
                                qosValues.Add(QOS_LEVEL.Medium, profile.Value);
                                break;
                            case "LowQoS":
                                qosValues.Add(QOS_LEVEL.Low, profile.Value);
                                break;
                            case "UtilityQoS":
                                qosValues.Add(QOS_LEVEL.Utility, profile.Value);
                                break;
                            case "EcoQoS":
                                qosValues.Add(QOS_LEVEL.Eco, profile.Value);
                                break;
                            //case "MultimediaQoS":
                            //    qosValues.Add(QOS_LEVEL.Multimedia, profile.Value);
                            //    break;
                        }
                    }
                }
            }

            if (qosValues.Count != 0)
            {
                var HighQoSLevel = qosValues.First();
                for (int index = 0; index < qosValues.Count; index++)
                {
                    // Validate that DC EPP values are more efficient than AC.
                    if (qosValues.ElementAt(index).Value.Item1 > qosValues.ElementAt(index).Value.Item2)
                    {
                        flags.EPPAcDc.Add(qosValues.ElementAt(index).Key.ToString(), 1);
                        results.Add(String.Format("EPP: AC value of {0}Qos is more efficient than DC value. AC = {1} | DC = {2}", qosValues.ElementAt(index).Key.ToString(), qosValues.ElementAt(index).Value.Item1, qosValues.ElementAt(index).Value.Item2));
                    }
                }

                for (int index = 1; index < qosValues.Count; index++)
                {

                    // Multimedia logic still need to work on

                    for (int revIndex = index - 1; revIndex >= 0; revIndex--)
                    {
                        if (qosValues.ElementAt(index).Value.Item1 < qosValues.ElementAt(revIndex).Value.Item1)
                        {
                            foundIssue = true;
                            flags.QoSPolicyAC.Add((qosValues.ElementAt(index).Key).ToString(), 1);
                            results.Add(String.Format("EPP: AC {0}Qos is lower than {1}Qos. {0} = {2} | {1} = {3}", qosValues.ElementAt(index).Key.ToString(), qosValues.ElementAt(revIndex).Key.ToString(), qosValues.ElementAt(index).Value.Item1, qosValues.ElementAt(revIndex).Value.Item1));
                        }
                        if (qosValues.ElementAt(index).Value.Item2 < qosValues.ElementAt(revIndex).Value.Item2)
                        {
                            foundIssue = true;
                            flags.QoSPolicyDC.Add((qosValues.ElementAt(index).Key).ToString(), 1);
                            results.Add(String.Format("EPP: DC {0}Qos is lower than {1}Qos. {0} = {2} | {1} = {3}", qosValues.ElementAt(index).Key.ToString(), qosValues.ElementAt(revIndex).Key.ToString(), qosValues.ElementAt(index).Value.Item2, qosValues.ElementAt(revIndex).Value.Item2));
                        }
                        if (foundIssue)
                        {
                            foundIssue = false;
                            break;
                        }
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