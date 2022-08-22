﻿using ControllerCommon.Managers;
using ControllerCommon.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ControllerCommon.Processor.Intel
{
    public class KX
    {
        private ProcessStartInfo startInfo;
        private string path;

        private string mchbar;

        // Package Power Limit (PACKAGE_RAPL_LIMIT_0_0_0_MCHBAR_PCU) — Offset 59A0h
        private const string pnt_limit = "59";
        private const string pnt_clock = "94";

        public KX()
        {
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Intel", "KX", "KX.exe");

            if (!File.Exists(path))
            {
                LogManager.LogError("KX.exe is missing. Power Manager won't work.");
                return;
            }

            startInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
        }

        internal bool init()
        {
            if (startInfo == null)
                return false;

            startInfo.Arguments = "/RdPci32 0 0 0 0x48";
            using (var ProcessOutput = Process.Start(startInfo))
            {
                while (!ProcessOutput.StandardOutput.EndOfStream)
                {
                    string line = ProcessOutput.StandardOutput.ReadLine();

                    if (!line.Contains("Return"))
                        continue;

                    // parse result
                    line = CommonUtils.Between(line, "Return ");
                    long returned = long.Parse(line);
                    string output = "0x" + returned.ToString("X2").Substring(0, 4);

                    mchbar = output;

                    ProcessOutput.Close();
                    return true;
                }
                ProcessOutput.Close();
            }

            return false;
        }

        internal int get_short_limit(bool msr)
        {
            switch (msr)
            {
                default:
                case false:
                    return get_limit("a4");
                case true:
                    return get_msr_limit(0);
            }
        }

        internal int get_long_limit(bool msr)
        {
            switch (msr)
            {
                default:
                case false:
                    return get_limit("a0");
                case true:
                    return get_msr_limit(1);
            }
        }

        internal int get_limit(string pointer)
        {
            startInfo.Arguments = $"/rdmem16 {mchbar}{pnt_limit}{pointer}";
            using (var ProcessOutput = Process.Start(startInfo))
            {
                try
                {
                    while (!ProcessOutput.StandardOutput.EndOfStream)
                    {
                        string line = ProcessOutput.StandardOutput.ReadLine();

                        if (!line.Contains("Return"))
                            continue;

                        // parse result
                        line = CommonUtils.Between(line, "Return ");
                        long returned = long.Parse(line);
                        var output = ((double)returned + short.MinValue) / 8.0d;

                        ProcessOutput.Close();
                        return (int)output;
                    }
                }
                catch (Exception) { }
                ProcessOutput.Close();
            }

            return -1; // failed
        }

        internal int get_msr_limit(int pointer)
        {
            startInfo.Arguments = $"/rdmsr 0x610";
            using (var ProcessOutput = Process.Start(startInfo))
            {
                try
                {
                    while (!ProcessOutput.StandardOutput.EndOfStream)
                    {
                        string line = ProcessOutput.StandardOutput.ReadLine();

                        if (!line.Contains("Msr Data"))
                            continue;

                        // parse result
                        line = CommonUtils.Between(line, "Msr Data     : ");

                        var values = line.Split(" ");
                        var hex = values[pointer];
                        hex = values[pointer].Substring(hex.Length - 3);
                        var output = Convert.ToInt32(hex, 16) / 8;

                        ProcessOutput.Close();
                        return (int)output;
                    }
                }
                catch (Exception) { }
                ProcessOutput.Close();
            }

            return -1; // failed
        }

        internal int get_short_value()
        {
            return -1; // not supported
        }

        internal int get_long_value()
        {
            return -1; // not supported
        }

        internal int set_short_limit(int limit)
        {
            return set_limit("a4", limit);
        }

        internal int set_long_limit(int limit)
        {
            return set_limit("a0", limit);
        }

        internal int set_limit(string pointer1, int limit)
        {
            string hex = TDPToHex(limit);

            // register command
            startInfo.Arguments = $"/wrmem16 {mchbar}{pnt_limit}{pointer1} 0x8{hex.Substring(0, 1)}{hex.Substring(1)}";
            using (var ProcessOutput = Process.Start(startInfo))
            {
                ProcessOutput.StandardOutput.ReadToEnd();
                ProcessOutput.Close();
            }

            return 0; // implement error code support
        }

        internal int set_msr_limits(int PL1, int PL2)
        {
            string hexPL1 = TDPToHex(PL1);
            string hexPL2 = TDPToHex(PL2);

            // register command
            startInfo.Arguments = $"/wrmsr 0x610 0x00438{hexPL2} 00DD8{hexPL1}";
            using (var ProcessOutput = Process.Start(startInfo))
            {
                ProcessOutput.StandardOutput.ReadToEnd();
                ProcessOutput.Close();
            }

            return 0; // implement error code support
        }

        private string TDPToHex(int decValue)
        {
            decValue *= 8;
            string output = decValue.ToString("X3");
            return output;
        }

        private string ClockToHex(int decValue)
        {
            decValue /= 50;
            string output = "0x" + decValue.ToString("X2");
            return output;
        }

        internal int set_gfx_clk(int clock)
        {
            string hex = ClockToHex(clock);

            string command = $"/wrmem8 {mchbar}{pnt_clock} {hex}";

            startInfo.Arguments = command;
            using (var ProcessOutput = Process.Start(startInfo))
            {
                ProcessOutput.StandardOutput.ReadToEnd();
                ProcessOutput.Close();
            }

            return 0; // implement error code support
        }

        internal int get_gfx_clk()
        {
            startInfo.Arguments = $"/rdmem8 {mchbar}{pnt_clock}";
            using (var ProcessOutput = Process.Start(startInfo))
            {
                try
                {
                    while (!ProcessOutput.StandardOutput.EndOfStream)
                    {
                        string line = ProcessOutput.StandardOutput.ReadLine();

                        if (!line.Contains("Return"))
                            continue;

                        // parse result
                        line = CommonUtils.Between(line, "Return ");
                        int returned = int.Parse(line);
                        var clock = returned * 50;

                        ProcessOutput.Close();
                        return clock;
                    }
                }
                catch (Exception) { }
                ProcessOutput.Close();
            }

            return -1; // failed
        }
    }
}
