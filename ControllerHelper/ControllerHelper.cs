﻿using ControllerService;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Serilog;
using Serilog.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Timers;
using System.Windows.Forms;
using Timer = System.Timers.Timer;

namespace ControllerHelper
{
    public partial class ControllerHelper : Form
    {
        #region imports
        [DllImport("User32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out IntPtr lpdwProcessId);
        #endregion

        public static PipeClient PipeClient;
        private Timer MonitorTimer;
        private IntPtr CurrentProcess;

        public static Controller CurrentController;

        private MouseHook m_Hook;

        private FormWindowState CurrentWindowState;
        private object updateLock = new();

        private HIDmode HideDS4 = new HIDmode("DualShock4Controller", "DualShock 4 emulation");
        private HIDmode HideXBOX = new HIDmode("Xbox360Controller", "Xbox 360 emulation");
        private Dictionary<string, HIDmode> HIDmodes = new();

        public static string CurrentExe, CurrentPath, CurrentPathService, CurrentPathProfiles, CurrentPathLogs;

        private bool RunAtStartup, StartMinimized, CloseMinimises, HookMouse;
        private bool IsAdmin;

        public ProfileManager ProfileManager;
        public ServiceManager ServiceManager;
        private readonly Logger logger;

        public ControllerHelper()
        {
            InitializeComponent();

            // paths
            CurrentExe = Process.GetCurrentProcess().MainModule.FileName;
            CurrentPath = AppDomain.CurrentDomain.BaseDirectory;
            CurrentPathProfiles = Path.Combine(CurrentPath, "profiles");
            CurrentPathService = Path.Combine(CurrentPath, "ControllerService.exe");
            CurrentPathLogs = Path.Combine(CurrentPath, "Logs");
            
            IsAdmin = Utils.IsAdministrator();

            // initialize logger
            logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File($"{CurrentPathLogs}\\ControllerHelper.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

            // initialize pipe client
            PipeClient = new PipeClient("ControllerService", this, logger);

            // initialize mouse hook
            m_Hook = new MouseHook(PipeClient);

            cB_HidMode.Items.Add(HideDS4);
            cB_HidMode.Items.Add(HideXBOX);

            HIDmodes.Add("DualShock4Controller", HideDS4);
            HIDmodes.Add("Xbox360Controller", HideXBOX);

            // settings
            cB_RunAtStartup.Checked = RunAtStartup = Properties.Settings.Default.RunAtStartup;
            cB_StartMinimized.Checked = StartMinimized = Properties.Settings.Default.StartMinimized;
            cB_CloseMinimizes.Checked = CloseMinimises = Properties.Settings.Default.CloseMinimises;
            cB_touchpad.Checked = HookMouse = Properties.Settings.Default.HookMouse;

            if (StartMinimized)
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
            }
        }

        private void ControllerHelper_Load(object sender, EventArgs e)
        {
            // initialize GUI
            this.BeginInvoke((MethodInvoker)delegate ()
            {
                if (!IsAdmin)
                {
                    foreach (Control ctrl in gb_SettingsService.Controls)
                        ctrl.Visible = false;
                    lb_Service_Error.Visible = true;
                }
            });

            UpdateStatus(false);

            // start Service Manager
            if (IsAdmin) ServiceManager = new ServiceManager("ControllerService", this, Properties.Settings.Default.ServiceName, Properties.Settings.Default.ServiceDescription);

            // start pipe client
            PipeClient.Start();

            // initialize Profile Manager
            ProfileManager = new ProfileManager(CurrentPathProfiles, this, logger);

            // start mouse hook
            if (HookMouse) m_Hook.Start();

            // monitor processes
            MonitorTimer = new Timer(1000) { Enabled = true, AutoReset = true };
            MonitorTimer.Elapsed += MonitorHelper;
        }

        public void UpdateProcess(int ProcessId, string ProcessPath)
        {
            try
            {
                string ProcessExec = Path.GetFileName(ProcessPath);

                if (ProfileManager.profiles.ContainsKey(ProcessExec))
                {
                    Profile CurrentProfile = ProfileManager.profiles[ProcessExec];
                    CurrentProfile.fullpath = ProcessPath;
                    CurrentProfile.Update();

                    PipeClient.SendMessage(new PipeMessage
                    {
                        Code = PipeCode.CLIENT_PROFILE,
                        args = new Dictionary<string, string>
                        {
                            { "muted", Convert.ToString(CurrentProfile.whitelisted) },
                            { "gyrometer", Convert.ToString(CurrentProfile.gyrometer) },
                            { "accelerometer", Convert.ToString(CurrentProfile.accelerometer) }
                        }
                    });

                    logger.Information("Profile {0} applied.", CurrentProfile.name);
                }
                else
                {
                    PipeClient.SendMessage(new PipeMessage
                    {
                        Code = PipeCode.CLIENT_PROFILE,
                        args = new Dictionary<string, string> {
                            { "muted", Convert.ToString(false) },
                            { "gyrometer", Convert.ToString(1.0f) },
                            { "accelerometer", Convert.ToString(1.0f) }
                        }
                    });
                }
            }
            catch (Exception) { }
        }

        private void ControllerHelper_Shown(object sender, EventArgs e)
        {
        }

        private void ControllerHelper_Resize(object sender, EventArgs e)
        {
            if (CurrentWindowState == WindowState)
                return;

            if (WindowState == FormWindowState.Minimized)
            {
                notifyIcon1.Visible = true;
                ShowInTaskbar = false;
            }
            else if (WindowState == FormWindowState.Normal)
            {
                notifyIcon1.Visible = false;
                ShowInTaskbar = true;
            }

            CurrentWindowState = WindowState;
        }

        private void ControllerHelper_Close(object sender, FormClosingEventArgs e)
        {
            if (CloseMinimises && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                WindowState = FormWindowState.Minimized;
            }
        }

        private void notifyIcon1_DoubleClick(object sender, EventArgs e)
        {
            WindowState = FormWindowState.Normal;
        }

        private void ControllerHelper_Closed(object sender, FormClosedEventArgs e)
        {
            PipeClient.Stop();
            m_Hook.Stop();
        }

        private void MonitorHelper(object sender, ElapsedEventArgs e)
        {
            lock (updateLock)
            {
                // refresh current process
                IntPtr hWnd = GetForegroundWindow();
                IntPtr processId;

                if (GetWindowThreadProcessId(hWnd, out processId) == 0)
                    return;

                if (processId != CurrentProcess)
                {
                    Process proc = Process.GetProcessById((int)processId);
                    string path = Utils.GetPathToApp(proc);

                    UpdateProcess((int)processId, path);

                    CurrentProcess = processId;
                }
            }
        }

        public void UpdateStatus(bool status)
        {
            this.BeginInvoke((MethodInvoker)delegate ()
            {
                foreach (Control ctl in tabDevices.Controls)
                    ctl.Enabled = status;
                gb_SettingsUDP.Enabled = status;
            });
        }

        public void UpdateScreen()
        {
            PipeClient.SendMessage(new PipeMessage
            {
                Code = PipeCode.CLIENT_SIZE_DETAILS,
                args = new Dictionary<string, string>
                    {
                        { "Bounds.Width", Convert.ToString(Screen.PrimaryScreen.Bounds.Width) },
                        { "Bounds.Height", Convert.ToString(Screen.PrimaryScreen.Bounds.Height) }
                    }
            });
        }

        public void UpdateController(Dictionary<string, string> args)
        {
            CurrentController = new Controller(args["ProductName"], Guid.Parse(args["InstanceGuid"]), Guid.Parse(args["ProductGuid"]), int.Parse(args["ProductIndex"]));

            this.BeginInvoke((MethodInvoker)delegate ()
            {
                lB_Devices.Items.Clear();
                lB_Devices.Items.Add(CurrentController);

                lB_Devices.SelectedItem = CurrentController;
            });
        }

        public void UpdateSettings(Dictionary<string, string> args)
        {
            this.BeginInvoke((MethodInvoker)delegate ()
            {
                cB_HidMode.SelectedItem = HIDmodes[args["HIDmode"]];
                cB_HIDcloak.SelectedItem = args["HIDcloaked"];
                cB_uncloak.Checked = bool.Parse(args["HIDuncloakonclose"]);

                cB_gyro.Checked = bool.Parse(args["gyrometer"]);
                cB_accelero.Checked = bool.Parse(args["accelerometer"]);

                tB_PullRate.Value = int.Parse(args["HIDrate"]);
                m_Hook.SetInterval(tB_PullRate.Value);

                cB_UDPEnable.Checked = bool.Parse(args["DSUEnabled"]);
                tB_UDPIP.Text = args["DSUip"];
                tB_UDPPort.Value = int.Parse(args["DSUport"]);
            });
        }

        private void quitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        #region GUI
        private void lB_Devices_SelectedIndexChanged(object sender, EventArgs e)
        {
            Controller con = (Controller)lB_Devices.SelectedItem;

            if (con == null)
                return;

            this.BeginInvoke((MethodInvoker)delegate ()
            {
                tB_InstanceID.Text = $"{con.InstanceGuid}";
                tB_ProductID.Text = $"{con.ProductGuid}";
            });

        }

        private void cB_HIDcloak_SelectedIndexChanged(object sender, EventArgs e)
        {
            PipeClient.SendMessage(new PipeMessage
            {
                Code = PipeCode.CLIENT_SETTINGS,
                args = new Dictionary<string, string>
                {
                    { "HIDcloaked", cB_HIDcloak.Text }
                }
            });
        }

        private void tB_PullRate_Scroll(object sender, EventArgs e)
        {
            // update mouse hook delay based on controller pull rate
            m_Hook.SetInterval(tB_PullRate.Value);

            this.BeginInvoke((MethodInvoker)delegate ()
            {
                toolTip1.SetToolTip(tB_PullRate, $"{tB_PullRate.Value} Miliseconds");
            });

            PipeClient.SendMessage(new PipeMessage
            {
                Code = PipeCode.CLIENT_SETTINGS,
                args = new Dictionary<string, string>
                {
                    { "HIDrate", $"{tB_PullRate.Value}" }
                }
            });
        }

        private void b_UDPApply_Click(object sender, EventArgs e)
        {
            PipeClient.SendMessage(new PipeMessage
            {
                Code = PipeCode.CLIENT_SETTINGS,
                args = new Dictionary<string, string>
                {
                    { "DSUip", $"{tB_UDPIP.Text}" },
                    { "DSUport", $"{tB_UDPPort.Value}" },
                    { "DSUEnabled", $"{cB_UDPEnable.Checked}" }
                }
            });
        }

        private void b_CreateProfile_Click(object sender, EventArgs e)
        {
            if (openFileDialog1.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    var path = openFileDialog1.FileName;
                    var name = openFileDialog1.SafeFileName;

                    ProfileManager.profiles[name] = new Profile(name, path);
                    ProfileManager.profiles[name].Serialize();
                }
                catch (Exception ex)
                {
                    logger.Error(ex.Message);
                }
            }
        }

        private void b_DeleteProfile_Click(object sender, EventArgs e)
        {
            Profile profile = (Profile)lB_Profiles.SelectedItem;
            profile.Delete();

            lB_Profiles.SelectedIndex = -1;
        }

        private void cB_RunAtStartup_CheckedChanged(object sender, EventArgs e)
        {
            RegistryKey rWrite = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);

            if (cB_RunAtStartup.Checked)
                rWrite.SetValue("ControllerHelper", AppDomain.CurrentDomain.BaseDirectory + $"{AppDomain.CurrentDomain.FriendlyName}.exe");
            else
                rWrite.DeleteValue("ControllerHelper");

            RunAtStartup = cB_RunAtStartup.Checked;
            Properties.Settings.Default.RunAtStartup = RunAtStartup;
            Properties.Settings.Default.Save();
        }

        private void cB_uncloak_CheckedChanged(object sender, EventArgs e)
        {
            PipeClient.SendMessage(new PipeMessage
            {
                Code = PipeCode.CLIENT_SETTINGS,
                args = new Dictionary<string, string>
                {
                    { "HIDuncloakonclose", $"{cB_uncloak.Checked}" }
                }
            });
        }

        private void cB_touchpad_CheckedChanged(object sender, EventArgs e)
        {
            HookMouse = cB_touchpad.Checked;
            Properties.Settings.Default.HookMouse = HookMouse;
            Properties.Settings.Default.Save();

            if (HookMouse) m_Hook.Start(); else m_Hook.Stop();
        }

        private void cB_StartMinimized_CheckedChanged(object sender, EventArgs e)
        {
            StartMinimized = cB_StartMinimized.Checked;
            Properties.Settings.Default.StartMinimized = StartMinimized;
            Properties.Settings.Default.Save();
        }

        private void cB_CloseMinimizes_CheckedChanged(object sender, EventArgs e)
        {
            CloseMinimises = cB_CloseMinimizes.Checked;
            Properties.Settings.Default.CloseMinimises = CloseMinimises;
            Properties.Settings.Default.Save();
        }

        private void lB_Profiles_SelectedIndexChanged(object sender, EventArgs e)
        {
            Profile profile = (Profile)lB_Profiles.SelectedItem;

            this.BeginInvoke((MethodInvoker)delegate ()
            {
                if (profile == null)
                {
                    gB_ProfileDetails.Enabled = false;
                    gB_ProfileOptions.Enabled = false;
                }
                else
                {
                    gB_ProfileDetails.Enabled = true;
                    gB_ProfileOptions.Enabled = true;

                    tB_ProfileName.Text = profile.name;
                    tB_ProfilePath.Text = profile.path;
                    toolTip1.SetToolTip(tB_ProfilePath, profile.error != Profile.ErrorCode.None ? $"Can't reach: {profile.path}" : $"{profile.path}");

                    cB_Whitelist.Checked = profile.whitelisted;
                    cB_Wrapper.Checked = profile.use_wrapper;

                    tb_ProfileGyroValue.Value = (int)(profile.gyrometer * 10.0f);
                    tb_ProfileAcceleroValue.Value = (int)(profile.accelerometer * 10.0f);
                }
            });
        }

        private void tb_ProfileGyroValue_Scroll(object sender, EventArgs e)
        {
            Profile profile = (Profile)lB_Profiles.SelectedItem;
            if (profile == null)
                return;

            float value = tb_ProfileGyroValue.Value / 10.0f;

            this.BeginInvoke((MethodInvoker)delegate ()
            {
                toolTip1.SetToolTip(tb_ProfileGyroValue, $"value: {value}");
            });
        }

        private void tb_ProfileAcceleroValue_Scroll(object sender, EventArgs e)
        {
            Profile profile = (Profile)lB_Profiles.SelectedItem;
            if (profile == null)
                return;

            float value = tb_ProfileAcceleroValue.Value / 10.0f;

            this.BeginInvoke((MethodInvoker)delegate ()
            {
                toolTip1.SetToolTip(tb_ProfileAcceleroValue, $"value: {value}");
            });
        }

        private void b_ApplyProfile_Click(object sender, EventArgs e)
        {
            Profile profile = (Profile)lB_Profiles.SelectedItem;
            if (profile == null)
                return;

            float gyro_value = tb_ProfileGyroValue.Value / 10.0f;
            float acce_value = tb_ProfileAcceleroValue.Value / 10.0f;

            profile.gyrometer = gyro_value;
            profile.accelerometer = acce_value;
            profile.whitelisted = cB_Whitelist.Checked;
            profile.use_wrapper = cB_Wrapper.Checked;

            profile.Update();
            profile.Serialize();
        }

        private void cB_gyro_CheckedChanged(object sender, EventArgs e)
        {
            this.BeginInvoke((MethodInvoker)delegate ()
            {
                cB_gyro.Text = cB_gyro.Checked ? "Gyrometer detected" : "No gyrometer detected";
            });
        }

        private void cB_accelero_CheckedChanged(object sender, EventArgs e)
        {
            this.BeginInvoke((MethodInvoker)delegate ()
            {
                cB_accelero.Text = cB_accelero.Checked ? "Accelerometer detected" : "No accelerometer detected";
            });
        }

        public void UpdateProfile(Profile profile)
        {
            this.BeginInvoke((MethodInvoker)delegate ()
            {
                int idx = lB_Profiles.Items.IndexOf(profile);

                foreach (Profile pr in lB_Profiles.Items)
                    if (pr.path == profile.path)
                    {
                        // IndexOf will always fail !
                        idx = lB_Profiles.Items.IndexOf(pr);
                        break;
                    }

                if (idx == -1)
                    lB_Profiles.Items.Add(profile);
                else
                    lB_Profiles.Items[idx] = profile;

                lB_Profiles.SelectedItem = profile;
            });
        }

        public void DeleteProfile(Profile profile)
        {
            this.BeginInvoke((MethodInvoker)delegate ()
            {
                int idx = lB_Profiles.Items.IndexOf(profile);
                if (idx != -1)
                    lB_Profiles.Items.RemoveAt(idx);
            });
        }

        public void UpdateService(ServiceControllerStatus status)
        {
            this.BeginInvoke((MethodInvoker)delegate ()
            {
                gb_SettingsService.SuspendLayout();

                switch (status)
                {
                    case ServiceControllerStatus.Paused:
                    case ServiceControllerStatus.Stopped:
                        if (b_ServiceInstall.Enabled == true) b_ServiceInstall.Enabled = false;
                        if (b_ServiceDelete.Enabled == false) b_ServiceDelete.Enabled = true;
                        if (b_ServiceStart.Enabled == false) b_ServiceStart.Enabled = true;
                        if (b_ServiceStop.Enabled == true) b_ServiceStop.Enabled = false;
                        break;
                    case ServiceControllerStatus.Running:
                        if (b_ServiceInstall.Enabled == true) b_ServiceInstall.Enabled = false;
                        if (b_ServiceDelete.Enabled == true) b_ServiceDelete.Enabled = false;
                        if (b_ServiceStart.Enabled == true) b_ServiceStart.Enabled = false;
                        if (b_ServiceStop.Enabled == false) b_ServiceStop.Enabled = true;
                        break;
                    default:
                        if (b_ServiceInstall.Enabled == false) b_ServiceInstall.Enabled = true;
                        if (b_ServiceDelete.Enabled == true) b_ServiceDelete.Enabled = false;
                        if (b_ServiceStart.Enabled == true) b_ServiceStart.Enabled = false;
                        if (b_ServiceStop.Enabled == true) b_ServiceStop.Enabled = false;
                        break;
                }

                gb_SettingsService.ResumeLayout();
            });
        }

        private void b_ServiceInstall_Click(object sender, EventArgs e)
        {
            this.BeginInvoke((MethodInvoker)delegate ()
            {
                foreach (Control ctrl in gb_SettingsService.Controls)
                    ctrl.Enabled = false;

                ServiceManager.CreateService(CurrentPathService);
            });
        }

        private void b_ServiceDelete_Click(object sender, EventArgs e)
        {
            this.BeginInvoke((MethodInvoker)delegate ()
            {
                foreach (Control ctrl in gb_SettingsService.Controls)
                    ctrl.Enabled = false;

                ServiceManager.DeleteService();
            });
        }

        private void b_ServiceStart_Click(object sender, EventArgs e)
        {
            this.BeginInvoke((MethodInvoker)delegate ()
            {
                foreach (Control ctrl in gb_SettingsService.Controls)
                    ctrl.Enabled = false;

                ServiceManager.StartService();
            });
        }

        private void b_ServiceStop_Click(object sender, EventArgs e)
        {
            this.BeginInvoke((MethodInvoker)delegate ()
            {
                foreach (Control ctrl in gb_SettingsService.Controls)
                    ctrl.Enabled = false;

                ServiceManager.StopService();
            });
        }
        #endregion
    }
}
