/*++

Copyright (c) 2022 Microsoft Corporation

Module Name:

    EmptyExample.cs

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

namespace PPMCheckerTool
{
    class PPMCheckerTool : BaseAnalyzer
    {
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
                IPendingResult<ISymbolDataSource> symSourcePending = trace.Enable(TraceDataSource.Symbols);
                IPendingResult<IGenericEventDataSource> genericEventDataSource = trace.Enable(TraceDataSource.GenericEvents);
                IPendingResult<ICpuSchedulingDataSource> cpuSchedulingDataSource = trace.Enable(TraceDataSource.CpuScheduling);

                // Process Data Sources
                trace.Process();

                // Optional : Construct list of relevant generic events
                List<IGenericEvent> QoSSupportChangedEvents = new List<IGenericEvent>();
                foreach (var genericEvent in genericEventDataSource.Result.Events)
                {
                    if (genericEvent.ProviderId.Equals(GUIDS.Microsoft_Windows_Kernel_Processor_Power))
                    {
                        if (genericEvent.TaskName.Equals("QoSSupportChanged"))
                        {
                            QoSSupportChangedEvents.Add(genericEvent);
                        }
                    }
                }

                // Validate that the necessary data sources are present in order to continue analysis
                if (!cpuSchedulingDataSource.HasResult || cpuSchedulingDataSource.Result.ContextSwitches.Count <= 0)
                {
                    throw new Exception("Trace missing ContextSwitch events.");
                }
                if (!cpuSchedulingDataSource.HasResult || cpuSchedulingDataSource.Result.ReadyThreadEvents.Count <= 0)
                {
                    throw new Exception("Trace missing ReadyThread events.");
                }


                // Create chonological enumerator for iteration over the various data sources
                ChronologicalEnumerator timelineEnumerator = new ChronologicalEnumerator();
                timelineEnumerator.addCollection(MulticoreChronologicalEnumerator.Create((cpuSchedulingDataSource.Result.ContextSwitches), (IContextSwitch x) => x.Timestamp, (IContextSwitch x) => x.Processor));
                timelineEnumerator.addCollection(QoSSupportChangedEvents.GetEnumerator(), (IGenericEvent i) => i.Timestamp);


                // Optional: Load Symbols 
                //ISymCachePath symCachePath;
                //ISymbolPath symPath;
                //if (System.Environment.GetEnvironmentVariable("_NT_SYMCACHE_PATH") != null)
                //{
                //    symCachePath = new SymCachePath(System.Environment.GetEnvironmentVariable("_NT_SYMCACHE_PATH"));
                //}
                //else
                //{
                //    throw new Exception("Missing environment variable _NT_SYMCACHE_PATH");
                //}

                //if (System.Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH") != null)
                //{
                //    symPath = new SymbolPath(System.Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));
                //}
                //else
                //{
                //    throw new Exception("Missing environment variable _NT_SYMBOL_PATH");
                //}

                //Task.WaitAll(symSourcePending.Result.LoadSymbolsAsync(symCachePath, symPath));


                // Extract System Metadata, or other static rundown information that may be worth logging
                uint numCores = systemMetadata.Result.CpuCount;

                // Optional : Potentially consider advancing to the first reliable event, or first reliable cswitch event
                Timestamp firstReliableCswitch = Timestamp.FromNanoseconds(Math.Max(startTime.Nanoseconds, cpuSchedulingDataSource.Result.FirstReliableContextSwitchTime.Nanoseconds));
                startTime = firstReliableCswitch;

                // Some tools may only analyze a single range. Others may return multiple rows (e.g. analyze ever UTC scenario in a trace)
                List<AnalysisResult> results = new List<AnalysisResult>();

                AnalysisResult result = AnalyzeRegion(timelineEnumerator, startTime, stopTime);
                result.fileName = tracePath;
                results.Add(result);

                WriteOutput(results, outputPath, noHeader);
            }
        }

        public class AnalysisResult
        {
            public string fileName;
            public Timestamp startTime;
            public Timestamp stopTime;
        }

        /// <summary>
        /// Iterates over the provided enumerator and computes the relevant statistics in the requested time range. 
        /// </summary>
        /// <param name="timelineEnumerator"></param>
        /// <param name="startTime"></param>
        /// <param name="stopTime"></param>
        /// <returns></returns>
        private static AnalysisResult AnalyzeRegion(ChronologicalEnumerator timelineEnumerator, Timestamp startTime, Timestamp stopTime)
        {
            // Advance the timeline 
            timelineEnumerator.MoveUntil(startTime);

            // Initialize result set
            AnalysisResult result = new AnalysisResult
            {
                startTime = startTime,
                stopTime = stopTime
            };

            // Iterate over the interval and handle events chronologically. Update state and compute result statistics
            do
            {
                TemporalEvent t = timelineEnumerator.Current;

                if (t.Type == typeof(IContextSwitch))
                {

                }
                else if (t.Type == typeof(IGenericEvent))
                {

                }
            }
            while (timelineEnumerator.MoveNext(stopTime));

            return result;
        }


        /// <summary>
        /// Writes a csv of the computed results to the target outputPath
        /// </summary>
        /// <param name="results"></param>
        /// <param name="outputFile"></param>
        /// <param name="noHeader"></param>
        public static void WriteOutput(List<AnalysisResult> results, String outputPath, bool noHeader)
        {
            StringBuilder output = new StringBuilder();
            if (!noHeader)
            {
                output.Append(getHeader());
                output.AppendLine();
            }

            foreach (AnalysisResult result in results)
            {
                output.Append(result.fileName);
                output.Append(","); output.Append(result.startTime);
                output.Append(","); output.Append(result.stopTime);
                output.AppendLine();
            }

            File.WriteAllText(outputPath, output.ToString());
        }
    }
}