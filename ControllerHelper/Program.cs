using CommandLine;
using ControllerCommon;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualBasic.ApplicationServices;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using static ControllerHelper.Options;

namespace ControllerHelper
{
    static class Program
    {
        static ControllerHelper MainForm;
        static PipeClient PipeClient;
        static string[] args;

        static Mutex mutex = new Mutex(true, "1DDFB948-19F1-417C-903D-BE05335DB8A4");
        static AutoResetEvent autoEvent = new AutoResetEvent(false);

        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(params string[] Arguments)
        {
            if (mutex.WaitOne(TimeSpan.Zero, true))
            {
                Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

                var configuration = new ConfigurationBuilder()
                            .AddJsonFile("helpersettings.json")
                            .Build();

                var serilogLogger = new LoggerConfiguration()
                    .ReadFrom.Configuration(configuration)
                    .CreateLogger();

                var microsoftLogger = new SerilogLoggerFactory(serilogLogger).CreateLogger("ControllerHelper");

                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                MainForm = new ControllerHelper(Arguments, microsoftLogger);
                Application.Run(MainForm);

                mutex.ReleaseMutex();
            }
            else
            {
                args = Arguments;

                PipeClient = new PipeClient("ControllerHelper");
                PipeClient.Connected += OnServerConnected;
                PipeClient.ServerMessage += OnServerMessage;
                PipeClient.Start();
                
                // Wait for work method to signal and kill after 4seconds
                autoEvent.WaitOne(4000);
            }
        }

        private static void OnServerMessage(object sender, PipeMessage e)
        {
            switch (e.code)
            {
                case PipeCode.FORCE_SHUTDOWN:
                    autoEvent.Set();
                    break;
            }
        }

        private static void OnServerConnected(object sender)
        {
            PipeClient.SendMessage(new PipeConsoleArgs() { args = args });
        }
    }
}
