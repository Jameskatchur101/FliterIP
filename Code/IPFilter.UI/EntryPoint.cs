﻿namespace IPFilter
{
    using System;
    using System.Deployment.Application;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Apps;
    using Microsoft;
    using Models;
    using Properties;
    using Services;
    using Views;

    static class EntryPoint
    {
        [STAThread]
        internal static void Main(string[] args)
        {
            UpgradeSettings();
            
            // TODO: Command line arguments / run silently
            if (args.Length > 0)
            {
                var commandLine = string.Join(" ", args);

                if (commandLine.IndexOf("/silent", StringComparison.OrdinalIgnoreCase) > -1)
                {
                    try
                    {
                        SilentMain().GetAwaiter().GetResult();
                    }
                    catch (AggregateException ae)
                    {
                        Trace.TraceWarning("There were one or more errors trying to update the filter: ");

                        foreach (var exception in ae.InnerExceptions)
                        {
                            Trace.TraceWarning(exception.ToString());
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceWarning("There was a problem when trying to update the filter: " + ex);
                    }
                }
                else
                {
                    Trace.TraceWarning("Invalid command line: " + commandLine);
                }
                
                return;
            }
            
            var window = new MainWindow();
            var app = new App();
            app.Run(window);
        }

        static async Task SilentMain()
        {
            var detector = new ApplicationEnumerator();

            var apps = (await detector.GetInstalledApplications()).ToList();

            if (!apps.Any())
            {
                Trace.TraceWarning("No BitTorrent applications found. Nothing to do, so exiting.");
                return;
            }

            var cancellationSource = new CancellationTokenSource();

            // Download the filter
            var downloader = new FilterDownloader();
            var progress = new Progress<ProgressModel>(delegate(ProgressModel model)
            {
                Trace.TraceInformation("{0}", model.Caption);
            });

            using (var filter = await downloader.DownloadFilter(null, cancellationSource.Token, progress))
            {
                if (filter.Exception != null) throw filter.Exception;
                
                foreach (var application in apps)
                {
                    Trace.TraceInformation("Updating app {0} {1}", application.Description, application.Version);

                    await application.Application.UpdateFilterAsync(filter, cancellationSource.Token, progress);
                }
            }
        }

        static void UpgradeSettings()
        {
            try
            {
                // Upgrade / migrate custom settings if necessary
                Settings.Default.Upgrade();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Couldn't upgrade settings: " + ex);
            }
        }


        static void AddToStartup()
        {
            const string link =
                "http://ipfilterupdate.sourceforge.net/install/IPFilter.UI.application#IPFilter.UI.application, Culture=neutral, PublicKeyToken=0000000000000000, processorArchitecture=msil";
        }

        public static void AddShortcutToStartupGroup(string publisherName, string productName)
        {
            if (ApplicationDeployment.IsNetworkDeployed && ApplicationDeployment.CurrentDeployment.IsFirstRun)
            {
                string startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);

                startupPath = Path.Combine(startupPath, productName) + ".appref-ms";
                if (!File.Exists(startupPath))
                {
                    string allProgramsPath = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                    string shortcutPath = Path.Combine(allProgramsPath, publisherName);
                    shortcutPath = Path.Combine(shortcutPath, productName) + ".appref-ms";
                    File.Copy(shortcutPath, startupPath);
                }
            }
        }
    }
}
