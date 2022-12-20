using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;

namespace Common
{
    public static class ProgramHelper
    {
        // https://msdn.microsoft.com/en-us/library/ms680313
#pragma warning disable 649
        struct _IMAGE_FILE_HEADER
        {
            public ushort Machine;
            public ushort NumberOfSections;
            public uint TimeDateStamp;
            public uint PointerToSymbolTable;
            public uint NumberOfSymbols;
            public ushort SizeOfOptionalHeader;
            public ushort Characteristics;
        };

        static public string GetBuildDateTime()
        {
            try
            {
                Assembly assembly = Assembly.GetCallingAssembly();

                string path = new Uri(assembly.GetName().CodeBase).LocalPath;

                var headerDefinition = typeof(_IMAGE_FILE_HEADER);

                var buffer = new byte[Math.Max(Marshal.SizeOf(headerDefinition), 4)];
                using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    fileStream.Position = 0x3C;
                    fileStream.Read(buffer, 0, 4);
                    fileStream.Position = BitConverter.ToUInt32(buffer, 0); // COFF header offset
                    fileStream.Read(buffer, 0, 4); // "PE\0\0"
                    fileStream.Read(buffer, 0, buffer.Length);
                }
                var pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    var addr = pinnedBuffer.AddrOfPinnedObject();
                    var coffHeader = (_IMAGE_FILE_HEADER)Marshal.PtrToStructure(addr, headerDefinition);

                    var epoch = new DateTime(1970, 1, 1);
                    var sinceEpoch = new TimeSpan(coffHeader.TimeDateStamp * TimeSpan.TicksPerSecond);
                    var buildDate = epoch + sinceEpoch;
                    var utcTime = TimeZone.CurrentTimeZone.ToUniversalTime(buildDate);

                    return utcTime.ToString("UTC 0:MM/dd/yy H:mm:ss zzz");
                }
                finally
                {
                    pinnedBuffer.Free();
                }
            }
            catch
            {
                return "unknown";
            }
        }
    }

    public static class GUIDS
    {
        public static Guid Microsoft_Windows_Kernel_Processor_Power = new Guid("0f67e49f-fe51-4e9f-b490-6f2948cc6027");
        public static Guid Microsoft_Windows_Win32k = new Guid("8c416c79-d49b-4f01-a467-e56d3aa8234c");
        public static Guid Microsoft_Windows_Kernel_Power = new Guid("331c3b3a-2005-44c2-ac5e-77220c37d6b4");
        public static Guid Microsoft_Windows_UserModePowerService = new Guid("ce8dee0b-d539-4000-b0f8-77bed049c590");
        // /base/eco/asmts/Energy/EnergyJob/exe/PowerProfileSettingMetrics.h
        public static Guid Device_Power_Policy_Video_Brightness = new Guid("aded5e82-b909-4619-9949-f5d71dac0bcb");
        public static Guid Microsoft_Windows_DgxKrnl = new Guid("802ec45a-1e99-4b83-9920-87c98277ba9d");
        public static Guid Microsoft_Windows_MediaEngine = new Guid("8f2048e0-f260-4f57-a8d1-932376291682");
        public static Guid Microsoft_Windows_MMCSS = new Guid("36008301-e154-466c-acec-5f4cbd6b4694");
        public static Guid Microsoft_Windows_PDC = new Guid("a6bf0deb-3659-40ad-9f81-e25af62ce3c7");
        public static Guid IntelPepTraceLoggingProvider = new Guid("9b82887e-0d74-4a2e-85ff-521f93c9d4a0");
    }

    public class BaseAnalyzer
    {
        protected static string GetArgument(IEnumerable<string> args, string option) => args.SkipWhile(i => !i.Equals(option, StringComparison.OrdinalIgnoreCase)).Skip(1).Take(1).FirstOrDefault();
        protected static bool GetSwitch(IEnumerable<string> args, string option) => args.SkipWhile(i => !i.Equals(option, StringComparison.OrdinalIgnoreCase)).Take(1).Any();

        protected static string GetRequiredArgument(IEnumerable<string> args, string option)
        {
            var result = args.SkipWhile(i => !i.Equals(option, StringComparison.OrdinalIgnoreCase)).Skip(1).Take(1).FirstOrDefault();
            if (result == null)
                throw new ArgumentException("Missing required argument " + option);

            return result;
        }

        protected static string GetOptionalArgument(IEnumerable<string> args, string option)
        {
            var result = args.SkipWhile(i => !i.Equals(option, StringComparison.OrdinalIgnoreCase)).Skip(1).Take(1).FirstOrDefault();
            return result;
        }

        /*
        protected static string getHeaderXML()
        {
            string path = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory.ToString(), "Header.xml");
            XmlTextReader reader = new XmlTextReader(path);
            string header = "";

            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (reader.Name.Equals("Heading"))
                            header += reader.GetAttribute("Name") + ", ";
                        break;
                }
            }
            return header;
        }
        */

        protected static string getHeader()
        {
            string path = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory.ToString(), "Header.csv");
            if (System.IO.File.Exists(path))
                return System.IO.File.ReadAllText(path);
            else
                throw new Exception("No Header.csv in System.AppDomain.CurrentDomain.FriendlyName");
        }
    }
}
