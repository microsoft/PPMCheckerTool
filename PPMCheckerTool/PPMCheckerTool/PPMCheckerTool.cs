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
        public static Guid DefaultProfile = Guid.Parse("00000000-0000-0000-0000-000000000000");
        public const String DefaultProfileGuidString = "Profile_Default";

        // Kernel Processor Power - QosClassPolicyRundown
        public class QosClassPolicyRundown
        {
            public UInt16 MaxPolicyPresent;
            public UInt16 MaxEquivalentFrequencyPercent;
            public UInt16 MinPolicyPresent;
            public UInt32 AutonomousActivityWindow;
            public byte EnergyPerfPreference;
            public bool ProvideGuidance;
            public bool AllowThrottling;
            public byte PerfBoostMode;
            public byte LatencyHintPerf;
            public bool TrackDesiredCrossClass;

            public QosClassPolicyRundown(IReadOnlyList<IGenericEventField> structure)
            {
                MaxPolicyPresent = structure[0].AsUInt16;
                MaxEquivalentFrequencyPercent = structure[1].AsUInt16;
                MinPolicyPresent = structure[2].AsUInt16;
                AutonomousActivityWindow = structure[3].AsUInt32;
                EnergyPerfPreference = structure[4].AsByte;
                ProvideGuidance = structure[5].AsBoolean;
                AllowThrottling = structure[6].AsBoolean;
                PerfBoostMode = structure[7].AsByte;
                LatencyHintPerf = structure[8].AsByte;
                TrackDesiredCrossClass = structure[9].AsBoolean;
            }
        }

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

        // Core states
        public class CoreState
        {
            public UInt32 nominalCpuFrequencyMHz;
            public UInt32 maxCpuFrequencyMHz;
            public uint coreType; // 1 is big cores, 0 is little cores
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

        // Altitude enum to find out which party was responsible in changing the preset values
        public enum POWER_SETTING_ALTITUDE
        {
            NONE = -1,
            GROUP_POLICY = 0,
            USER = 1,
            RUNTIME_OVERRIDE = 2,
            PROVISIONING = 3,
            OEM_CUSTOMIZATION = 4,
            INTERNAL_OVERRIDE = 5,
            OS_DEFAULT = 6
        }

        // Running type for MultiCoreHeteroSetRundown
        public enum RunningType
        {
            Short = 0,
            Long = 1,
            Max = 2
        }

        // Hetero Cpu policy for MultiCoreHeteroSetRundown
        public enum HeteroCpuPolicy
        {
            All = 0,
            Large = 1,
            LargeOrIdle = 2,
            Small = 3,
            SmallOrIdle = 4,
            Dynamic = 5,
            BiasedSmall = 6,
            BiasedLarge = 7,
            Default = 8,
            Max = 9,
        }

        // Ppm Hetero System
        public enum PPM_HETERO_SYSTEM
        {
            PpmHeteroSystemNone = 0,
            PpmHeteroSystemSimulated = 1,
            PpmHeteroSystemEfficiencyClass = 2,
            PpmHeteroSystemFavoredCore = 3,
            PpmHeteroSystemVirtual = 4,
            PpmHeteroSystemHgs = 5,
            PpmHeteroSystemMaximum = 6
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
                bool? RundownPowerSource = null;

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

                // Initialize cores
                CoreState[] cores = new CoreState[numCores];
                for (uint i = 0; i < numCores; i++)
                {
                    cores[i] = new CoreState();
                    //cores[i].coreType = (uint)systemMetadata.Result.Processors[(int)i].EfficiencyClass ; // Find out what type of core it is - Big or Little
                }

                // Validation flags
                ProcessoPolicyValidationFlags flags = new ProcessoPolicyValidationFlags();
                SortedList<QOS_LEVEL, Tuple<uint?, uint?>> qosValues = new SortedList<QOS_LEVEL, Tuple<uint?, uint?>>();

                // Each ProfileId + PowerSetting GUID maps to a pair of settings for AC/DC respectively
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

                        // Get Nominal Frequency of one of the big cores
                        if (genericEvent.TaskName.Equals("Summary2"))
                        {
                            UInt16 processorNumber = genericEvent.Fields[1].AsByte;
                            UInt32 freq = genericEvent.Fields[4].AsUInt32;
                            cores[processorNumber].nominalCpuFrequencyMHz = genericEvent.Fields[4].AsUInt32;
                            cores[processorNumber].maxCpuFrequencyMHz = (uint)(genericEvent.Fields[5].AsUInt32 * cores[processorNumber].nominalCpuFrequencyMHz / 100);
                        }
                    }
                    else if (genericEvent.ProviderId.Equals(GUIDS.Microsoft_Windows_UserModePowerService))
                    {
                        if (genericEvent.TaskName.Equals("RundownPowerSource"))
                        {
                            RundownPowerSource = genericEvent.Fields[0].AsBoolean;
                        }

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

                //Add umpo results
                results.Add("Rundown Power Source: " + (RundownPowerSource == true ? "AC" : "DC"));
                results.Add("Rundown Power Scheme: " + RundownPowerSchemeString);
                results.Add("Rundown Effective Power Overlay: " + RundownEffectiveOverlayPowerSchemeString);
                validateQoSOrder(profileNames, flags, powSettings, qosValues, results);

                WriteOutput(results, outputPath, true); // Always No header for now
            }
        }

        /// <summary>
        /// This method validates that each QoS level values are higher or equal to the level bellow
        /// QoS Level orders High (0) > Medium (9) > Low (10) > Utility (6) > Eco (5) (Multimedia (2), Deadline (12))
        ///
        /// </summary>
        public static void validateQoSOrder(Dictionary<uint, Tuple<string, string, Guid>> profileNames, ProcessoPolicyValidationFlags flags, Dictionary<Guid, Dictionary<uint, Tuple<uint?, uint?>>> powSettings, SortedList<QOS_LEVEL, Tuple<uint?, uint?>> qosValues, List<string> results)
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