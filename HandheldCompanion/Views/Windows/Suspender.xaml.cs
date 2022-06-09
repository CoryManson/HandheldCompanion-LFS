﻿using ControllerCommon;
using HandheldCompanion.Managers;
using Microsoft.Extensions.Logging;
using SharpDX.XInput;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace HandheldCompanion.Views.Windows
{
    /// <summary>
    /// Interaction logic for Suspender.xaml
    /// </summary>
    public partial class Suspender : Window
    {
        #region imports
        [DllImport("ntdll.dll", EntryPoint = "NtSuspendProcess", SetLastError = true, ExactSpelling = false)]
        private static extern UIntPtr NtSuspendProcess(IntPtr processHandle);
        [DllImport("ntdll.dll", EntryPoint = "NtResumeProcess", SetLastError = true, ExactSpelling = false)]
        private static extern UIntPtr NtResumeProcess(IntPtr processHandle);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        #endregion

        private PipeClient pipeClient;

        // Process vars
        private ProcessManager processManager;

        // Gamepad triggers
        private InputsManager inputsManager;

        public Suspender()
        {
            InitializeComponent();
        }

        public Suspender(PipeClient pipeClient, ProcessManager processManager) : this()
        {
            this.pipeClient = pipeClient;
            this.pipeClient.ServerMessage += OnServerMessage;

            this.processManager = processManager;
            this.processManager.ProcessStarted += ProcessStarted;
            this.processManager.ProcessStopped += ProcessStopped;
        }

        private void ProcessStopped(ProcessEx processEx)
        {
            this.Dispatcher.Invoke(() =>
            {
                var element = processEx.GetBorder();
                CurrentProcesses.Children.Remove(element);
            });
        }

        private void ProcessStarted(ProcessEx processEx)
        {
            this.Dispatcher.Invoke(() =>
            {
                processEx.Draw();
                var element = processEx.GetBorder();
                CurrentProcesses.Children.Add(element);
            });
        }

        private void OnServerMessage(object sender, PipeMessage e)
        {
            // do something
        }

        public void UpdateVisibility()
        {
            this.Dispatcher.Invoke(() =>
            {
                Visibility visibility = Visibility.Visible;
                switch (Visibility)
                {
                    case Visibility.Visible:
                        visibility = Visibility.Collapsed;
                        break;
                    case Visibility.Collapsed:
                    case Visibility.Hidden:
                        visibility = Visibility.Visible;
                        break;
                }
                Visibility = visibility;
            });
        }
    }
}
