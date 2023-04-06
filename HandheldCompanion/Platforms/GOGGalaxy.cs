﻿using ControllerCommon.Managers;
using ControllerCommon.Platforms;
using ControllerCommon.Utils;
using HandheldCompanion.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HandheldCompanion.Platforms
{
    public class GOGGalaxy : IPlatform
    {
        public GOGGalaxy()
        {
            Name = "GOG Galaxy";
            ExecutableName = "GalaxyClient.exe";

            // store specific modules
            Modules = new List<string>()
            {
                "Galaxy.dll",
                "GalaxyClient.exe",
                "GalaxyClientService.exe",
            };

            // check if platform is installed
            InstallPath = RegistryUtils.GetString(@"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths", "client");
            if (Path.Exists(InstallPath))
            {
                // update paths
                SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"GOG.com\Galaxy\Configuration\config.json");
                ExecutablePath = Path.Combine(InstallPath, ExecutableName);

                // check executable
                IsInstalled = File.Exists(ExecutablePath);
            }

            base.PlatformType = PlatformType.GOG;
        }

        public override bool IsRunning()
        {
            return Process is not null;
        }

        public override bool Start()
        {
            if (!IsInstalled)
                return false;

            if (!IsRunning())
                return false;

            var process = Process.Start(new ProcessStartInfo()
            {
                FileName = ExecutablePath,
                // ArgumentList = { "-gamepadui" },
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            return process is not null;
        }

        public override bool Stop()
        {
            if (!IsInstalled)
                return false;

            if (IsRunning())
                return false;

            var process = Process.Start(new ProcessStartInfo()
            {
                FileName = ExecutablePath,
                ArgumentList = { "-shutdown" },
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            return process is not null;
        }
    }
}