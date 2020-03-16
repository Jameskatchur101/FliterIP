using IPFilter.Cli;

namespace IPFilter.ViewModels
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows;
    using System.Windows.Forms;
    using System.Windows.Input;
    using System.Windows.Interop;
    using System.Windows.Threading;
    using Apps;
    using ListProviders;
    using Microsoft.Win32;
    using Models;
    using Native;
    using Services;
    using Services.Deployment;
    using UI.Annotations;
    using Views;
    using Application = System.Windows.Application;

    using IPFilter.Formats;
    using IPFilter.Logging;

    public class MainWindowViewModel : INotifyPropertyChanged
    {
        IMirrorProvider selectedMirrorProvider;
        UpdateState state;
        IProgress<ProgressModel> progress;
        readonly ApplicationEnumerator applicationEnumerator;
        CancellationTokenSource cancellationToken;
        List<ApplicationDetectionResult> apps;
        readonly FilterDownloader downloader;
        int progressValue;
        string statusText;
        readonly StringBuilder log = new StringBuilder(500);
        readonly Dispatcher dispatcher;
        
        public MainWindowViewModel()
        {
            //Trace.TraceInformation("Initializing...");
            //Trace.Listeners.Add(new DelegateTraceListener(null,LogLineAction ));

            dispatcher = Dispatcher.CurrentDispatcher;

            StatusText = "Ready";
            State = UpdateState.Ready;
            ProgressMax = 100;
            
            Options = new OptionsViewModel();
            Update = new UpdateModel();
            MirrorProviders = MirrorProvidersFactory.Get();
            LaunchHelpCommand = new DelegateCommand(LaunchHelp);
            ShowOptionsCommand = new DelegateCommand(ShowOptions);
            ShowLogCommand = new DelegateCommand(ShowLog);
            StartCommand = new DelegateCommand(Start, IsStartEnabled);
            applicationEnumerator = new ApplicationEnumerator();
            downloader = new FilterDownloader();
            
            cancellationToken = new CancellationTokenSource();
        }

        void ShowLog(object obj)
        {
            try
            {
                var listener = Trace.Listeners["file"] as FileTraceListener;
                if (listener?.fileName == null) return;
                if (!File.Exists(listener.fileName)) return;
                Process.Start(listener.fileName);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Couldn't launch support URL: " + ex);
            }
        }

        void ShowOptions(object obj)
        {
            var options = new OptionsWindow();
            options.ShowDialog();
        }

        void OnNotifyIconClick(object sender, EventArgs e)
        {
            Application.Current.MainWindow.Activate();
            var helper = new WindowInteropHelper(Application.Current.MainWindow);
            Win32Api.BringToFront(helper.Handle);
        }

        bool IsStartEnabled(object arg)
        {
            return Update == null || !Update.IsUpdating;
        }
        
        void ProgressHandler(ProgressModel progressModel)
        {
            if (progressModel == null) return;

            ProgressValue = progressModel.Value;
            StatusText = progressModel.Caption;
            State = progressModel.State;
        }

        void LaunchHelp(object uri)
        {
            try
            {
                var url = (Uri) uri;
                Process.Start( url.ToString() );
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Couldn't launch support URL: " + ex);
            }
        }
        
        void Start(object o)
        {
            switch (State)
            {
                case UpdateState.Done:
                case UpdateState.Ready:
                case UpdateState.Cancelled:
                    cancellationToken.Dispose();
                    cancellationToken = new CancellationTokenSource();
                    Task.Run(StartAsync, cancellationToken.Token);//, TaskCreationOptions.None);//, TaskScheduler.FromCurrentSynchronizationContext());
                    break;
                    
                case UpdateState.Downloading:
                case UpdateState.Decompressing:
                    cancellationToken.Cancel();
                    break;
            }
        }

        internal async Task StartAsync()
        {
            var message = "Done";

            progress.Report(UpdateState.Downloading, "Starting...");

            try
            {
                if (SelectedMirrorProvider == null)
                {
                    progress.Report(new ProgressModel(UpdateState.Cancelled, "Please select a filter source",0));
                    return;
                }

                var uri = SelectedMirrorProvider.GetUrlForMirror();

                using (var filter = await downloader.DownloadFilter(new Uri(uri), cancellationToken.Token, progress))
                {
                    cancellationToken.Token.ThrowIfCancellationRequested();

                    if (filter == null)
                    {
                        progress.Report(new ProgressModel(UpdateState.Cancelled, "A filter wasn't downloaded successfully.", 0));
                    }
                    else if (filter.Exception != null)
                    {
                        if (filter.Exception is OperationCanceledException) throw filter.Exception;
                        Trace.TraceError("Problem when downloading: " + filter.Exception);
                        progress.Report(new ProgressModel(UpdateState.Cancelled, "Problem when downloading: " + filter.Exception.Message, 0));
                        return;
                    }
                    else
                    {
                        filter.Stream.Seek(0, SeekOrigin.Begin);
                        using (var reader = new StreamReader(filter.Stream, Encoding.Default, false, 65535, true))
                        {
                            var line = await reader.ReadLineAsync();
                            
                            while (line != null)
                            {
                                var entry = DatParser.ParseEntry(line);
                                if( entry != null) filter.Entries.Add(entry);
                                var percent = (int)Math.Floor( (double)filter.Stream.Position / filter.Stream.Length * 100);
                                await Task.Yield();
                                if( percent > ProgressValue) progress.Report(new ProgressModel(UpdateState.Decompressing, "Parsed " + filter.Entries.Count + " entries",  percent));
                                line = await reader.ReadLineAsync();
                            }
                        }

                        foreach (var application in apps)
                        {
                            Trace.TraceInformation("Updating app {0} {1}", application.Description, application.Version);
                            await application.Application.UpdateFilterAsync(filter, cancellationToken.Token, progress);
                        }
                    }

                    if (filter?.FilterTimestamp != null)
                    {
                        message = $"Done. List timestamp: {filter.FilterTimestamp.Value.ToLocalTime()}";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Trace.TraceWarning("Update was cancelled.");
                progress.Report(new ProgressModel(UpdateState.Cancelled, "Update was cancelled.", 0));
                return;
            }
            catch (Exception ex)
            {
                Trace.TraceError("Problem when updating: " + ex);
                progress.Report(new ProgressModel(UpdateState.Cancelled, "Problem when updating: " + ex.Message, 0));
                return;
            }

            progress.Report(UpdateState.Decompressing, "Cleaning up...", -1);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Trace.TraceInformation(message);
            progress.Report(new ProgressModel(UpdateState.Done, message, 100));
            ShowNotification("Updated IP Filter", message, ToolTipIcon.Info);
        }

        public UpdateModel Update { get; set; }

        public IList<IMirrorProvider> MirrorProviders { get; set; }

        public OptionsViewModel Options { get; private set; }
        public IEnumerable<ApplicationDetectionResult> Providers { get; set; }
        public event PropertyChangedEventHandler PropertyChanged;

        public string StatusText
        {
            get { return statusText; }
            set
            {
                if (value == statusText) return;
                statusText = value;
                OnPropertyChanged();
            }
        }

        public string ButtonText
        {
            get
            {
                switch (State)
                {
                    case UpdateState.Downloading:
                    case UpdateState.Decompressing:
                        return "Cancel";

                    case UpdateState.Cancelling:
                        return "Cancelling...";
                        
                    default:
                        return "Go";
                }
            }
        }

        public int ProgressValue
        {
            get { return progressValue; }
            set
            {
                if (value == progressValue) return;
                ProgressIsIndeterminate = value < 0;
                progressValue = value;
                OnPropertyChanged();
            }
        }

        public int ProgressMin { get; set; }
        public int ProgressMax { get; set; }

        public IMirrorProvider SelectedMirrorProvider
        {
            get { return selectedMirrorProvider; }
            set
            {
                if (Equals(value, selectedMirrorProvider)) return;
                selectedMirrorProvider = value;
                OnPropertyChanged();
            }
        }
        
        public ICommand LaunchHelpCommand { get; private set; }

        public UpdateState State
        {
            get { return state; }
            set
            {
                if (value == state) return;
                state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ButtonText));
            }
        }

        public ICommand StartCommand { get; set; }
        
        public bool ProgressIsIndeterminate { get; set; }

        public Action<string, string, ToolTipIcon> ShowNotification { get; set; }

        public ICommand ShowOptionsCommand { get; private set; }

        public ICommand ShowLogCommand { get; private set; }
        
        public async Task Initialize()
        {
            progress = new Progress<ProgressModel>(ProgressHandler);

            // Check for updates
            await CheckForUpdates();
            
            SelectedMirrorProvider = MirrorProviders.First();
            
            apps = (await applicationEnumerator.GetInstalledApplications()).ToList();

            if (!apps.Any())
            {
                Trace.TraceWarning("No BitTorrent applications found.");
                return;
            }

            foreach (var result in apps)
            {
                Trace.TraceInformation("Found app {0} version {1} at {2}", result.Description, result.Version, result.InstallLocation);
            }
        }

        async Task ScheduledUpdate()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(1));
                await StartAsync();
            }
        }

        async Task CheckForUpdates()
        {
            try
            {
                if (Config.Default.settings.update.isDisabled)
                {
                    Trace.TraceInformation("The software update check is disabled by the user; skipping check");
                    return;
                }

                // Remove any old ClickOnce installs
                try
                {
                    var uninstallInfo = UninstallInfo.Find("IPFilter Updater");
                    if (uninstallInfo != null)
                    {
                        Trace.TraceWarning("Old ClickOnce app installed! Trying to remove...");
                            var uninstaller = new Uninstaller();
                            uninstaller.Uninstall(uninstallInfo);
                            Trace.TraceInformation("Successfully removed ClickOnce app");
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Failed to remove old ClickOnce app: " + ex);
                }

                var applicationDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPFilter");
                var installerDir = new DirectoryInfo(Path.Combine(applicationDirectory, "installer"));
                
                // Detect the current running version. The ProductVersion contains the informational, semantic version e.g. "3.0.0-beta"
                var versionInfo = Process.GetCurrentProcess().MainModule.FileVersionInfo;
                var currentVersion = new SemanticVersion(versionInfo.ProductVersion);

                // Remove any old installers
                try
                {
                    if (!installerDir.Exists)
                    {
                        installerDir.Create();
                    }
                    else if (!Config.Default.settings.update.isCleanupDisabled)
                    {
                        // Don't delete the MSI for the current installed version
                        var currentMsiName = "IPFilter." + currentVersion.ToNormalizedString() + ".msi";

                        // Scan the directory for all installers
                        foreach (var fileInfo in installerDir.GetFiles("IPFilter.*.msi"))
                        {
                            // Don't remove the installer for the installed version
                            if (fileInfo.Name.Equals(currentMsiName, StringComparison.OrdinalIgnoreCase)) continue;

                            Trace.TraceInformation("Removing cached installer: " + fileInfo.Name);
                            fileInfo.SafeDelete();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Couldn't clean up old installers: " + ex);
                }

                Trace.TraceInformation("Checking for software updates...");
                progress.Report(new ProgressModel(UpdateState.Downloading, "Checking for software updates...", -1));

                var updater = new Updater();

                var result = await updater.CheckForUpdateAsync(Config.Default.settings.update.isPreReleaseEnabled);
                if (result == null) return;
                
                var latestVersion = new SemanticVersion(result.Version);
                
                Update.IsUpdateAvailable = latestVersion > currentVersion;

                if (Update.IsUpdateAvailable)
                {
                    Update.AvailableVersion = latestVersion;
                    Update.IsUpdateRequired = true;
                    Update.MinimumRequiredVersion = latestVersion;
                    Update.UpdateSizeBytes = 2000000;
                }

                Trace.TraceInformation("Current version: {0}", Update.CurrentVersion);
                Trace.TraceInformation("Available version: {0}", Update.AvailableVersion?.ToString() ?? "<no updates>");

                if (!Update.IsUpdateAvailable ) return;
                
                if (MessageBoxHelper.Show(dispatcher, "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes,
                    "An update to version {0} is available. Would you like to update now?", Update.AvailableVersion) != MessageBoxResult.Yes)
                {
                    return;
                }
                
                Trace.TraceInformation("Starting application update...");

                // If we're not "installed", then don't check for updates. This is so the
                // executable can be stand-alone. Stand-alone self-update to come later.
                using (var key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\IPFilter"))
                {
                    var installPath = (string) key?.GetValue("InstallPath");
                    if (installPath == null)
                    {
                        using (var process = new Process())
                        {
                            process.StartInfo = new ProcessStartInfo("https://www.ipfilter.app/")
                            {
                                UseShellExecute = true
                            };

                            process.Start();
                            return;
                        }
                    }
                }

                // Download the MSI to the installer directory
                var msiPath = Path.Combine(installerDir.FullName, "IPFilter." + Update.AvailableVersion + ".msi");

                // Download the installer
                using (var handler = new WebRequestHandler())
                {
                    handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                    var uri = new Uri($"{result.Uri}?{DateTime.Now.ToString("yyyyMMddHHmmss")}");

                    using (var httpClient = new HttpClient(handler))
                    using (var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken.Token))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            progress.Report(new ProgressModel(UpdateState.Ready, "Update cancelled. Ready.", 100));
                            Update.IsUpdating = false;
                            return;
                        }

                        var length = response.Content.Headers.ContentLength;
                        double lengthInMb = !length.HasValue ? -1 : (double)length.Value / 1024 / 1024;
                        double bytesDownloaded = 0;

                        using(var stream = await response.Content.ReadAsStreamAsync())
                        using(var msi = File.Open(msiPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                        {
                            var buffer = new byte[65535 * 4];

                            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken.Token);
                            while (bytesRead != 0)
                            {
                                await msi.WriteAsync(buffer, 0, bytesRead, cancellationToken.Token);
                                bytesDownloaded += bytesRead;

                                if (length.HasValue)
                                {
                                    double downloadedMegs = bytesDownloaded / 1024 / 1024;
                                    var percent = (int)Math.Floor((bytesDownloaded / length.Value) * 100);

                                    var status = string.Format(CultureInfo.CurrentUICulture, "Downloaded {0:F2} MB of {1:F2} MB", downloadedMegs, lengthInMb);

                                    Update.IsUpdating = true;
                                    Update.DownloadPercentage = percent;
                                    progress.Report(new ProgressModel(UpdateState.Downloading, status, percent));
                                }

                                if (cancellationToken.IsCancellationRequested)
                                {
                                    progress.Report(new ProgressModel(UpdateState.Ready, "Update cancelled. Ready.", 100));
                                    Update.IsUpdating = false;
                                    return;
                                }

                                bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken.Token);
                            }
                        }
                    }
                }

                progress.Report(new ProgressModel(UpdateState.Ready, "Launching update...", 100));
                Update.IsUpdating = false;
                
                // Now run the installer
                var sb = new StringBuilder("msiexec.exe ");

                // Enable logging for the installer
                var installLog = Path.Combine(applicationDirectory, "install.log");
                sb.AppendFormat(" /l*v \"{0}\"", installLog);
                
                sb.AppendFormat(" /i \"{0}\"", msiPath);

                //sb.Append(" /passive");

                ProcessInformation processInformation = new ProcessInformation();
                StartupInfo startupInfo = new StartupInfo();
                SecurityAttributes processSecurity = new SecurityAttributes();
                SecurityAttributes threadSecurity = new SecurityAttributes();
                processSecurity.nLength = Marshal.SizeOf(processSecurity);
                threadSecurity.nLength = Marshal.SizeOf(threadSecurity);

                const int NormalPriorityClass = 0x0020;

                if (!ProcessManager.CreateProcess(null, sb, processSecurity,
                    threadSecurity, false, NormalPriorityClass,
                    IntPtr.Zero, null, startupInfo, processInformation))
                {
                    throw Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error());
                }
                
                try
                {
                    //dispatcher.Invoke(DispatcherPriority.Normal, new Action(Application.Current.Shutdown));
                    Application.Current.Shutdown();
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Exception when shutting down app for update: " + ex);
                    Update.ErrorMessage = "Couldn't shutdown the app to apply update.";
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Application update check failed: " + ex);
            }
            finally
            {
                progress.Report(new ProgressModel(UpdateState.Ready, "Ready", 0));
            }
        }


        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Shutdown()
        {
            
        }
    }
}