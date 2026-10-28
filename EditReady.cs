// EditReady: makes edit-friendly copies of videos (a full frame every few frames) so
// Premiere can jump to any cut instantly. Exports and timeline scrubbing get much faster.
//
// The window layout lives in MainWindow.xaml (embedded at build time). Build with build.ps1,
// which uses the C# compiler that ships with Windows; nothing else to install.
// Needs ffmpeg + ffprobe (found automatically on PATH / winget / next to the exe).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

[assembly: AssemblyTitle("EditReady")]
[assembly: AssemblyDescription("Makes videos fast to cut and export in Premiere")]
[assembly: AssemblyProduct("EditReady")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace EditReady
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show(e.Exception.ToString(), "EditReady hit a problem", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            var ui = new MainUI(args);
            return app.Run(ui.Win);
        }
    }

    class MediaInfo
    {
        public bool HasVideo;
        public double Duration;
        public int Width = 1920;
        public int Height = 1080;
        public double Fps = 60;
        public bool AudioCopyOk = true;
        public long FileBytes;
    }

    // One video in the list. The card in MainWindow.xaml binds to these properties.
    class JobVM : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        string state = "Waiting", meta = "Reading video info\u2026", statusText = "Waiting", percentText = "";
        double progress;

        public string FileName { get; set; }
        public string FullPath { get; set; }
        public string State { get { return state; } set { Set(ref state, value, "State"); } }
        public string Meta { get { return meta; } set { Set(ref meta, value, "Meta"); } }
        public string StatusText { get { return statusText; } set { Set(ref statusText, value, "StatusText"); } }
        public string PercentText { get { return percentText; } set { Set(ref percentText, value, "PercentText"); } }
        public double Progress { get { return progress; } set { Set(ref progress, value, "Progress"); } }

        public MediaInfo Info;
        public string Output;
        public string Error = "";
        public bool InRun;

        void Set<T>(ref T field, T value, string name)
        {
            field = value;
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    class MainUI
    {
        public Window Win;

        static readonly string[] VideoExts = { ".mp4", ".mkv", ".mov", ".avi", ".m4v", ".webm", ".flv", ".ts", ".mts", ".m2ts", ".wmv", ".mxf", ".3gp", ".mpg", ".mpeg" };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        Button browseBtn, addBtn, clearBtn, tipClose, goBtn, folderChange;
        FrameworkElement emptyState, listHeader, listScroll, tip, dragOverlay, folderRow, settingsPanel;
        Border engineBadge;
        System.Windows.Shapes.Rectangle dropRect;
        System.Windows.Shapes.Ellipse engineDot;
        TextBlock engineText, countText, statusText, warnText, folderText;
        RadioButton saveNext, saveFolder, gopFast, gopBal, gopSmall;
        ItemsControl jobList;

        readonly ObservableCollection<JobVM> jobs = new ObservableCollection<JobVM>();
        readonly BlockingCollection<JobVM> probeQueue = new BlockingCollection<JobVM>();
        readonly ManualResetEvent toolsKnown = new ManualResetEvent(false);
        readonly DispatcherTimer dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

        string ffmpeg, ffprobe;
        bool ready, gpu, forceCpu, autoStart, tipDismissed, running;
        string folder = "";
        string idleMessage;
        Thread worker;
        volatile bool cancelRequested;
        Process current;
        readonly object procLock = new object();
        string runFolder;
        int runGop = 10;
        double lastSpeed;
        int runDone, runFailed;
        IntPtr hwnd;

        static string SettingsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EditReady"); } }
        static string SettingsFile { get { return Path.Combine(SettingsDir, "settings.txt"); } }
        static string FfmpegPathFile { get { return Path.Combine(SettingsDir, "ffmpeg_path.txt"); } }

        T F<T>(string name) where T : class { return Win.FindName(name) as T; }

        public MainUI(string[] args)
        {
            var asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream("MainWindow.xaml"))
                Win = (Window)XamlReader.Load(s);
            try
            {
                using (Stream s = asm.GetManifestResourceStream("EditReady.ico"))
                    if (s != null) Win.Icon = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            catch { }

            browseBtn = F<Button>("BrowseBtn"); addBtn = F<Button>("AddBtn"); clearBtn = F<Button>("ClearBtn");
            tipClose = F<Button>("TipClose"); goBtn = F<Button>("GoBtn"); folderChange = F<Button>("FolderChange");
            emptyState = F<FrameworkElement>("EmptyState"); listHeader = F<FrameworkElement>("ListHeader");
            listScroll = F<FrameworkElement>("ListScroll"); tip = F<FrameworkElement>("Tip");
            dragOverlay = F<FrameworkElement>("DragOverlay"); folderRow = F<FrameworkElement>("FolderRow");
            settingsPanel = F<FrameworkElement>("SettingsPanel");
            engineBadge = F<Border>("EngineBadge");
            dropRect = F<System.Windows.Shapes.Rectangle>("DropRect");
            engineDot = F<System.Windows.Shapes.Ellipse>("EngineDot");
            engineText = F<TextBlock>("EngineText"); countText = F<TextBlock>("CountText");
            statusText = F<TextBlock>("StatusText"); warnText = F<TextBlock>("WarnText"); folderText = F<TextBlock>("FolderText");
            saveNext = F<RadioButton>("SaveNext"); saveFolder = F<RadioButton>("SaveFolder");
            gopFast = F<RadioButton>("GopFast"); gopBal = F<RadioButton>("GopBal"); gopSmall = F<RadioButton>("GopSmall");
            jobList = F<ItemsControl>("JobList");

            jobList.ItemsSource = jobs;
            jobList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnCardClick));

            LoadSettings();
            RefreshFolderRow();

            browseBtn.Click += delegate { AddViaDialog(); };
            addBtn.Click += delegate { AddViaDialog(); };
            clearBtn.Click += delegate { ClearFinished(); };
            tipClose.Click += delegate { tipDismissed = true; SaveSettings(); UpdateView(); };
            goBtn.Click += delegate { if (running) Stop(); else Start(); };
            folderChange.Click += delegate { ChooseFolder(); };
            saveNext.Checked += delegate { RefreshFolderRow(); SaveSettings(); UpdateView(); };
            saveFolder.Checked += delegate
            {
                if (Directory.Exists(folder)) { RefreshFolderRow(); SaveSettings(); UpdateView(); }
                else ChooseFolder();
            };
            RoutedEventHandler gopChanged = delegate { RefreshReadyTexts(); SaveSettings(); UpdateView(); };
            gopFast.Checked += gopChanged; gopBal.Checked += gopChanged; gopSmall.Checked += gopChanged;

            DragEventHandler over = (s, e) =>
            {
                bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
                e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
                if (files) { ShowDrag(true); dragTimer.Stop(); dragTimer.Start(); }
            };
            Win.PreviewDragEnter += over;
            Win.PreviewDragOver += over;
            Win.PreviewDrop += (s, e) =>
            {
                ShowDrag(false);
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) AddPaths(files);
                e.Handled = true;
            };
            dragTimer.Tick += delegate { dragTimer.Stop(); ShowDrag(false); };

            Win.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) { AddViaDialog(); e.Handled = true; }
            };

            Win.SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(Win).Handle;
                int on = 1, caption = 0x00161312; // COLORREF (BGR) of #121316
                try
                {
                    Native.DwmSetWindowAttribute(hwnd, 20, ref on, 4);      // dark title bar
                    Native.DwmSetWindowAttribute(hwnd, 35, ref caption, 4); // caption color = window color (Windows 11)
                }
                catch { }
            };
            Win.TaskbarItemInfo = new TaskbarItemInfo();
            Win.ContentRendered += delegate { Startup(); };
            Win.Closing += OnClosing;
            Win.Closed += delegate { probeQueue.CompleteAdding(); };

            var probeThread = new Thread(ProbeLoop) { IsBackground = true };
            probeThread.Start();

            var files0 = new List<string>();
            foreach (string a in args)
            {
                if (a.Equals("--cpu", StringComparison.OrdinalIgnoreCase)) forceCpu = true;
                else files0.Add(a);
            }
            if (files0.Count > 0) { AddPaths(files0); autoStart = true; }
            UpdateView();
        }

        // ---------- startup: find ffmpeg, check the GPU encoder ----------

        void Startup()
        {
            ffmpeg = FindFfmpeg();
            if (ffmpeg == null)
            {
                MessageBox.Show(Win, "EditReady uses ffmpeg to convert videos, and couldn't find it on this PC.\n\nClick OK and point to your ffmpeg.exe.\nDon't have it? Install it with:  winget install Gyan.FFmpeg", "Find ffmpeg", MessageBoxButton.OK, MessageBoxImage.Information);
                var d = new Microsoft.Win32.OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe", Title = "Find ffmpeg.exe" };
                if (d.ShowDialog(Win) == true)
                {
                    ffmpeg = d.FileName;
                    try { Directory.CreateDirectory(SettingsDir); File.WriteAllText(FfmpegPathFile, ffmpeg); } catch { }
                }
            }
            if (ffmpeg == null)
            {
                SetEngine("Danger", "ffmpeg not found", "EditReady needs ffmpeg to convert videos.");
                idleMessage = "EditReady needs ffmpeg. Install it (winget install Gyan.FFmpeg), then reopen EditReady.";
                toolsKnown.Set();
                UpdateView();
                return;
            }
            string probe = Path.Combine(Path.GetDirectoryName(ffmpeg), "ffprobe.exe");
            ffprobe = File.Exists(probe) ? probe : null;
            toolsKnown.Set();

            SetEngine("Text3", "Checking graphics card\u2026", null);
            var t = new Thread(() =>
            {
                bool ok = !forceCpu && TestNvenc();
                UI(delegate
                {
                    gpu = ok;
                    ready = true;
                    if (gpu) SetEngine("Success", "NVIDIA GPU \u00b7 fast", "Videos are converted on your graphics card's video encoder, which is the fastest way.");
                    else SetEngine("Warn", "CPU mode \u00b7 slower", forceCpu ? "Started with --cpu." : "No NVIDIA video encoder was found, so videos are converted on the processor. It works, just slower.");
                    UpdateView();
                    if (autoStart) { autoStart = false; Start(); }
                });
            }) { IsBackground = true };
            t.Start();
        }

        void SetEngine(string brushKey, string text, string tooltip)
        {
            engineDot.Fill = (Brush)Win.FindResource(brushKey);
            engineText.Text = text;
            engineBadge.ToolTip = tooltip;
        }

        static string FindFfmpeg()
        {
            var c = new List<string>();
            try { if (File.Exists(FfmpegPathFile)) c.Add(File.ReadAllText(FfmpegPathFile).Trim()); } catch { }
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            c.Add(Path.Combine(exeDir, "ffmpeg.exe"));
            c.Add(Path.Combine(exeDir, "bin", "ffmpeg.exe"));
            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                string dd = d.Trim().Trim('"');
                if (dd.Length == 0) continue;
                try { c.Add(Path.Combine(dd, "ffmpeg.exe")); } catch { }
            }
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            try
            {
                string pk = Path.Combine(la, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(pk))
                    foreach (string d in Directory.GetDirectories(pk, "*FFmpeg*"))
                        foreach (string f in Directory.GetFiles(d, "ffmpeg.exe", SearchOption.AllDirectories))
                            c.Add(f);
            }
            catch { }
            c.Add(Path.Combine(la, "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
            c.Add(@"C:\ffmpeg\bin\ffmpeg.exe");
            c.Add(@"C:\Program Files\ffmpeg\bin\ffmpeg.exe");
            foreach (string f in c)
                if (!string.IsNullOrEmpty(f) && File.Exists(f)) return f;
            return null;
        }

        bool TestNvenc()
        {
            string o;
            return Run(ffmpeg, "-hide_banner -nostdin -v error -f lavfi -i color=c=black:s=256x256:r=30:d=0.2 -c:v h264_nvenc -f null -", out o) == 0;
        }

        // ---------- adding / removing videos ----------

        void AddViaDialog()
        {
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "Choose videos",
                Filter = "Videos|*" + string.Join(";*", VideoExts) + "|All files|*.*"
            };
            if (d.ShowDialog(Win) == true) AddPaths(d.FileNames);
        }

        void AddPaths(IEnumerable<string> paths)
        {
            foreach (string p in paths)
            {
                if (Directory.Exists(p))
                {
                    string[] files;
                    try { files = Directory.GetFiles(p); } catch { continue; }
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (string f in files)
                    {
                        string name = Path.GetFileNameWithoutExtension(f);
                        if (Array.IndexOf(VideoExts, Path.GetExtension(f).ToLowerInvariant()) >= 0
                            && !name.EndsWith("_edit", StringComparison.OrdinalIgnoreCase)
                            && !name.EndsWith(".converting", StringComparison.OrdinalIgnoreCase))
                            AddFile(f);
                    }
                }
                else if (File.Exists(p)) AddFile(p);
            }
            idleMessage = null;
            UpdateView();
        }

        void AddFile(string path)
        {
            path = Path.GetFullPath(path);
            foreach (JobVM j in jobs)
                if (string.Equals(j.FullPath, path, StringComparison.OrdinalIgnoreCase) && (j.State == "Waiting" || j.State == "Working"))
                    return;
            var job = new JobVM { FileName = Path.GetFileName(path), FullPath = path, StatusText = "Reading video info\u2026" };
            try { job.Meta = FmtSize(new FileInfo(path).Length); } catch { }
            jobs.Add(job);
            try { probeQueue.Add(job); } catch (InvalidOperationException) { }
        }

        void ClearFinished()
        {
            foreach (JobVM j in jobs.ToArray())
                if (j.State == "Done" || j.State == "Failed" || j.State == "Invalid") jobs.Remove(j);
            idleMessage = null;
            UpdateView();
        }

        void OnCardClick(object sender, RoutedEventArgs e)
        {
            var b = e.OriginalSource as Button;
            if (b == null) return;
            var job = b.DataContext as JobVM;
            string tag = b.Tag as string;
            if (job == null || tag == null) return;
            if (tag == "remove")
            {
                if (job.State != "Working") jobs.Remove(job);
                idleMessage = null;
                UpdateView();
            }
            else if (tag == "show")
            {
                if (job.Output != null && File.Exists(job.Output)) Process.Start("explorer.exe", "/select,\"" + job.Output + "\"");
                else MessageBox.Show(Win, "The converted file isn't there anymore. It may have been moved or deleted.", "EditReady", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (tag == "details")
            {
                MessageBox.Show(Win, job.Error.Length > 0 ? job.Error : "(ffmpeg didn't say why)", "Why " + job.FileName + " didn't convert", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (tag == "retry")
            {
                job.State = "Waiting";
                job.Error = "";
                job.Progress = 0;
                job.StatusText = ReadyText(job);
                idleMessage = null;
                UpdateView();
                if (ready && !running) Start();
            }
        }

        // ---------- probing (runs in the background as soon as videos are added) ----------

        void ProbeLoop()
        {
            toolsKnown.WaitOne();
            foreach (JobVM job in probeQueue.GetConsumingEnumerable())
            {
                MediaInfo mi = null;
                try { mi = Probe(job.FullPath); } catch { }
                JobVM j = job;
                UI(delegate
                {
                    j.Info = mi;
                    if (mi == null || !mi.HasVideo)
                    {
                        j.Meta = "Not a video EditReady can read";
                        if (j.State == "Waiting")
                        {
                            j.State = "Invalid";
                            j.StatusText = "This file doesn't contain video, so there's nothing to convert.";
                        }
                    }
                    else
                    {
                        j.Meta = MetaText(mi);
                        if (j.State == "Waiting") j.StatusText = ReadyText(j);
                    }
                    UpdateView();
                });
            }
        }

        MediaInfo Probe(string input)
        {
            var mi = new MediaInfo();
            try { mi.FileBytes = new FileInfo(input).Length; } catch { }
            if (ffprobe == null) { mi.HasVideo = true; mi.AudioCopyOk = false; return mi; }
            string o;

            Run(ffprobe, "-v error -show_entries format=duration -of default=nw=1:nk=1 " + Q(input), out o);
            double d;
            if (double.TryParse(o.Trim(), NumberStyles.Float, Inv, out d)) mi.Duration = d;

            Run(ffprobe, "-v error -select_streams V:0 -show_entries stream=width,height,avg_frame_rate,r_frame_rate,duration -of default=nw=1 " + Q(input), out o);
            double avg = 0, rf = 0, sd = 0;
            foreach (string raw in o.Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                int iv;
                if (k == "width" && int.TryParse(v, out iv)) { mi.Width = iv; mi.HasVideo = true; }
                else if (k == "height" && int.TryParse(v, out iv)) mi.Height = iv;
                else if (k == "avg_frame_rate") avg = Rate(v);
                else if (k == "r_frame_rate") rf = Rate(v);
                else if (k == "duration") double.TryParse(v, NumberStyles.Float, Inv, out sd);
            }
            if (avg > 0 && avg < 1000) mi.Fps = avg; else if (rf > 0 && rf < 1000) mi.Fps = rf;
            if (mi.Duration <= 0 && sd > 0) mi.Duration = sd;

            // Premiere is happy with AAC/MP3 in MP4; anything else (FLAC, Opus, PCM...) becomes AAC 320k.
            Run(ffprobe, "-v error -select_streams a -show_entries stream=codec_name -of csv=p=0 " + Q(input), out o);
            foreach (string raw in o.Split('\n'))
            {
                string c = raw.Trim().Split(',')[0].Trim();
                if (c.Length > 0 && c != "aac" && c != "mp3") mi.AudioCopyOk = false;
            }
            return mi;
        }

        static double Rate(string v)
        {
            string[] p = v.Split('/');
            double a, b = 1;
            if (!double.TryParse(p[0], NumberStyles.Float, Inv, out a)) return 0;
            if (p.Length > 1 && (!double.TryParse(p[1], NumberStyles.Float, Inv, out b) || b == 0)) return 0;
            return a / b;
        }

        // ---------- converting ----------

        int CurrentGop()
        {
            if (gopFast.IsChecked == true) return 5;
            if (gopSmall.IsChecked == true) return 30;
            return 10;
        }

        void Start()
        {
            if (!ready || running) return;
            if (saveFolder.IsChecked == true && !Directory.Exists(folder))
            {
                ChooseFolder();
                if (!Directory.Exists(folder)) return;
            }
            if (!jobs.Any(j => j.State == "Waiting")) return;

            runFolder = saveFolder.IsChecked == true ? folder : null;
            runGop = CurrentGop();
            foreach (JobVM j in jobs) j.InRun = false;
            cancelRequested = false;
            running = true;
            runDone = runFailed = 0;
            lastSpeed = 0;
            idleMessage = null;
            Win.TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
            Win.TaskbarItemInfo.ProgressValue = 0;
            statusText.Text = "Starting\u2026";
            UpdateView();

            worker = new Thread(WorkerLoop) { IsBackground = true };
            worker.Start();
        }

        void Stop()
        {
            cancelRequested = true;
            KillCurrent();
            goBtn.IsEnabled = false;
            goBtn.Content = "Stopping\u2026";
        }

        void KillCurrent()
        {
            lock (procLock)
            {
                if (current != null) { try { current.Kill(); } catch { } }
            }
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (!running) return;
            if (MessageBox.Show(Win, "A video is still converting. Stop it and close EditReady?\n\nThe unfinished copy will be deleted; your original is untouched.", "EditReady", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            cancelRequested = true;
            KillCurrent();
            if (worker != null) worker.Join(5000);
        }

        JobVM PickNext()
        {
            foreach (JobVM j in jobs)
                if (j.State == "Waiting") { j.State = "Working"; j.InRun = true; return j; }
            return null;
        }

        void WorkerLoop()
        {
            Native.SetThreadExecutionState(0x80000001); // keep the PC awake while converting
            try
            {
                while (!cancelRequested)
                {
                    JobVM job = Win.Dispatcher.Invoke(new Func<JobVM>(PickNext));
                    if (job == null) break;
                    ProcessJob(job);
                }
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                UI(delegate { MessageBox.Show(Win, msg, "EditReady hit a problem", MessageBoxButton.OK, MessageBoxImage.Error); });
            }
            finally
            {
                Native.SetThreadExecutionState(0x80000000);
            }
            UI(FinishRun);
        }

        void ProcessJob(JobVM job)
        {
            UI(delegate { job.Progress = 0; job.PercentText = "0%"; job.StatusText = "Starting\u2026"; UpdateOverall(0); });

            MediaInfo mi = job.Info ?? Probe(job.FullPath);
            if (!mi.HasVideo) { Fail(job, "ffprobe couldn't find a video stream in this file."); return; }

            string dir = runFolder ?? Path.GetDirectoryName(job.FullPath);
            string baseName = Path.GetFileNameWithoutExtension(job.FullPath) + "_edit";
            string outPath = Path.Combine(dir, baseName + ".mp4");
            for (int n = 2; File.Exists(outPath); n++)
                outPath = Path.Combine(dir, baseName + " (" + n + ").mp4");
            string tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(outPath) + ".converting.mp4");
            TryDelete(tmp);

            var sw = Stopwatch.StartNew();
            bool useGpu = gpu;
            string err;
            int code = Encode(job, mi, tmp, useGpu, out err);
            if (code != 0 && useGpu && !cancelRequested)
            {
                TryDelete(tmp);
                UI(delegate { job.StatusText = "The GPU couldn't handle this one, trying the CPU\u2026"; });
                useGpu = false;
                code = Encode(job, mi, tmp, false, out err);
            }

            if (cancelRequested)
            {
                TryDelete(tmp);
                UI(delegate { job.State = "Waiting"; job.Progress = 0; job.StatusText = "Stopped \u00b7 will start over next time"; });
                return;
            }
            if (code != 0) { TryDelete(tmp); Fail(job, err); return; }
            try { File.Move(tmp, outPath); }
            catch (Exception ex) { Fail(job, "Converted, but couldn't rename the finished file: " + ex.Message); return; }

            long size = 0;
            try { size = new FileInfo(outPath).Length; } catch { }
            string took = Fmt(sw.Elapsed.TotalSeconds);
            bool cpu = !useGpu;
            UI(delegate
            {
                job.Output = outPath;
                job.State = "Done";
                job.Progress = 100;
                job.StatusText = "Saved as " + Path.GetFileName(outPath) + "  \u00b7  " + FmtSize(size) + "  \u00b7  took " + took + (cpu ? " on the CPU" : "");
                runDone++;
                UpdateView();
            });
        }

        void Fail(JobVM job, string err)
        {
            UI(delegate
            {
                job.Error = err ?? "";
                job.State = "Failed";
                job.StatusText = "Couldn't convert this video. Click Details to see why.";
                runFailed++;
                UpdateView();
            });
        }

        void FinishRun()
        {
            running = false;
            bool stopped = cancelRequested;
            Win.TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
            if (stopped) idleMessage = "Stopped. Press Convert to carry on.";
            else if (runFailed == 0 && runDone == 1) idleMessage = "Done! Your video is ready for Premiere.";
            else if (runFailed == 0) idleMessage = "All done! " + runDone + " videos are ready for Premiere.";
            else idleMessage = runDone + " ready, " + runFailed + " couldn't be converted (see Details).";
            UpdateView();
            if (!stopped && runDone + runFailed > 0)
            {
                SystemSounds.Asterisk.Play();
                if (!Win.IsActive) Flash();
            }
        }

        string BuildArgs(string input, string output, MediaInfo mi, bool useGpu)
        {
            var sb = new StringBuilder("-hide_banner -nostdin -y ");
            if (useGpu) sb.Append("-hwaccel cuda ");
            sb.Append("-i ").Append(Q(input)).Append(" -map 0:V:0 -map 0:a? ");
            if (mi.Width % 2 != 0 || mi.Height % 2 != 0) sb.Append("-vf scale=trunc(iw/2)*2:trunc(ih/2)*2 ");
            if (useGpu)
            {
                // Bitrate scaled from 60/80 Mbps at 1080p60: well above a YouTube export, so no visible loss.
                double ratio = PixelRatio(mi);
                int target = Clamp((int)Math.Round(60 * ratio), 15, 200);
                int max = Clamp((int)Math.Round(80 * ratio), 20, 240);
                sb.AppendFormat(Inv, "-c:v h264_nvenc -preset p5 -rc vbr -cq 16 -b:v {0}M -maxrate {1}M -bufsize {2}M -profile:v high ", target, max, max * 2);
            }
            else sb.Append("-c:v libx264 -preset veryfast -crf 16 ");
            sb.AppendFormat(Inv, "-g {0} -keyint_min {0} -bf 0 -pix_fmt yuv420p ", runGop);
            sb.Append(mi.AudioCopyOk ? "-c:a copy " : "-c:a aac -b:a 320k ");
            sb.Append("-map_metadata 0 -progress pipe:1 -nostats -f mp4 ").Append(Q(output));
            return sb.ToString();
        }

        int Encode(JobVM job, MediaInfo mi, string outFile, bool useGpu, out string errTail)
        {
            var psi = new ProcessStartInfo(ffmpeg, BuildArgs(job.FullPath, outFile, mi, useGpu))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            var tail = new LinkedList<string>();
            using (var p = new Process { StartInfo = psi })
            {
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (tail) { tail.AddLast(e.Data); if (tail.Count > 25) tail.RemoveFirst(); }
                };
                try { p.Start(); }
                catch (Exception ex) { errTail = "Couldn't start ffmpeg: " + ex.Message; return -1; }
                lock (procLock) current = p;
                if (cancelRequested) { try { p.Kill(); } catch { } }
                p.BeginErrorReadLine();

                double t = 0, speed = 0;
                long lastUi = -1000;
                var sw = Stopwatch.StartNew();
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    if (line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms="))
                    {
                        long v;
                        if (long.TryParse(line.Substring(12), out v) && v >= 0) t = v / 1e6;
                    }
                    else if (line.StartsWith("speed="))
                    {
                        double sp;
                        if (double.TryParse(line.Substring(6).Trim().TrimEnd('x'), NumberStyles.Float, Inv, out sp)) speed = sp;
                    }
                    else if (line.StartsWith("progress=") && sw.ElapsedMilliseconds - lastUi >= 250)
                    {
                        lastUi = sw.ElapsedMilliseconds;
                        ReportProgress(job, t, mi.Duration, speed, useGpu);
                    }
                }
                p.WaitForExit();
                lock (procLock) current = null;
                lock (tail) errTail = string.Join("\n", tail);
                return p.ExitCode;
            }
        }

        void ReportProgress(JobVM job, double t, double dur, double speed, bool useGpu)
        {
            UI(delegate
            {
                if (speed > 0) lastSpeed = speed;
                double frac = dur > 0 ? Math.Max(0, Math.Min(1, t / dur)) : 0;
                job.Progress = frac * 100;
                job.PercentText = dur > 0 ? (int)(frac * 100) + "%" : "\u2026";
                string s;
                if (dur > 0 && speed > 0)
                    s = "Converting  \u00b7  " + speed.ToString("0.0", Inv) + "\u00d7 real-time  \u00b7  " + Fmt((dur - t) / speed) + " left";
                else s = "Converting\u2026  " + Fmt(t) + " done";
                if (!useGpu) s += "  \u00b7  on the CPU";
                job.StatusText = s;
                UpdateOverall(t);
            });
        }

        // Footer text + taskbar progress for the whole batch.
        void UpdateOverall(double currentT)
        {
            double total = 0, done = 0, remaining = 0;
            int count = 0, finished = 0;
            foreach (JobVM j in jobs)
            {
                if (!j.InRun && j.State != "Waiting") continue;
                double d = j.Info != null ? j.Info.Duration : 0;
                count++;
                total += d;
                if (j.State == "Working") { done += Math.Min(currentT, d); remaining += Math.Max(0, d - currentT); }
                else if (j.State == "Waiting") remaining += d;
                else { done += d; finished++; }
            }
            string eta = lastSpeed > 0 && remaining > 0 ? "  \u00b7  about " + FmtLong(remaining / lastSpeed) + " left" : "";
            statusText.Text = count > 1 ? "Converting " + Math.Min(finished + 1, count) + " of " + count + eta : "Converting" + eta;
            Win.TaskbarItemInfo.ProgressValue = total > 0 ? done / total : 0;
        }

        // ---------- view ----------

        void UpdateView()
        {
            bool any = jobs.Count > 0;
            emptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            listHeader.Visibility = listScroll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

            int waiting = jobs.Count(j => j.State == "Waiting");
            int finished = jobs.Count(j => j.State == "Done" || j.State == "Failed" || j.State == "Invalid");
            double footage = jobs.Where(j => j.Info != null).Sum(j => j.Info.Duration);
            countText.Text = jobs.Count + (jobs.Count == 1 ? " video" : " videos") + (footage > 0 ? "  \u00b7  " + FmtLong(footage) + " of footage" : "");
            clearBtn.Visibility = finished > 0 ? Visibility.Visible : Visibility.Collapsed;
            tip.Visibility = !tipDismissed && jobs.Any(j => j.State == "Done") ? Visibility.Visible : Visibility.Collapsed;
            settingsPanel.IsEnabled = !running;
            warnText.Visibility = Visibility.Collapsed;

            if (running)
            {
                goBtn.Style = (Style)Win.FindResource("Secondary");
                goBtn.Content = "Stop";
                goBtn.IsEnabled = !cancelRequested;
                if (cancelRequested) goBtn.Content = "Stopping\u2026";
                return;
            }

            goBtn.Style = (Style)Win.FindResource("Primary");
            if (!ready)
            {
                goBtn.Content = ffmpeg == null && toolsKnown.WaitOne(0) ? "Convert" : "Getting ready\u2026";
                goBtn.IsEnabled = false;
            }
            else
            {
                goBtn.Content = waiting == 0 ? "Convert" : (waiting == 1 ? "Convert 1 video" : "Convert " + waiting + " videos");
                goBtn.IsEnabled = waiting > 0;
            }

            double est = jobs.Where(j => j.State == "Waiting" && j.Info != null).Sum(j => Estimate(j));
            if (idleMessage != null) statusText.Text = idleMessage;
            else if (waiting > 0) statusText.Text = (waiting == 1 ? "1 video ready to convert" : waiting + " videos ready to convert") + (est > 0 ? "  \u00b7  the copies take about " + FmtSize((long)est) : "");
            else statusText.Text = any ? "Nothing left to convert. Add more videos anytime." : "Add videos to get started.";

            CheckDiskSpace();
        }

        void CheckDiskSpace()
        {
            var need = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (JobVM j in jobs)
            {
                if (j.State != "Waiting" || j.Info == null) continue;
                string dir = saveFolder.IsChecked == true && Directory.Exists(folder) ? folder : Path.GetDirectoryName(j.FullPath);
                string root;
                try { root = Path.GetPathRoot(dir); } catch { continue; }
                if (string.IsNullOrEmpty(root)) continue;
                double cur;
                need.TryGetValue(root, out cur);
                need[root] = cur + Estimate(j);
            }
            foreach (var kv in need)
            {
                try
                {
                    var di = new DriveInfo(kv.Key);
                    if (kv.Value > di.AvailableFreeSpace * 0.97)
                    {
                        warnText.Text = "The copies need about " + FmtSize((long)kv.Value) + " on " + kv.Key.TrimEnd('\\') + ", but only " + FmtSize(di.AvailableFreeSpace) + " is free. Pick \u201cAnother folder\u2026\u201d on a drive with more space.";
                        warnText.Visibility = Visibility.Visible;
                        return;
                    }
                }
                catch { }
            }
        }

        void ShowDrag(bool on)
        {
            if (jobs.Count == 0)
            {
                dragOverlay.Visibility = Visibility.Collapsed;
                dropRect.Stroke = on ? (Brush)Win.FindResource("Accent") : new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x48));
                dropRect.Fill = on ? (Brush)Win.FindResource("AccentSoft") : new SolidColorBrush(Color.FromRgb(0x16, 0x17, 0x1B));
            }
            else dragOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        void RefreshReadyTexts()
        {
            foreach (JobVM j in jobs)
                if (j.State == "Waiting" && j.Info != null) j.StatusText = ReadyText(j);
        }

        string ReadyText(JobVM j)
        {
            if (j.Info == null) return "Reading video info\u2026";
            return "Ready  \u00b7  copy will be about " + FmtSize((long)Estimate(j));
        }

        // Rough size of the copy: ~40 Mbps at 1080p60 for Balanced, measured on real OBS recordings.
        double Estimate(JobVM j)
        {
            if (j.Info == null) return 0;
            int g = CurrentGop();
            double gopFactor = g <= 5 ? 1.25 : (g >= 30 ? 0.8 : 1.0);
            double mbps = Math.Max(8, Math.Min(200, 40 * PixelRatio(j.Info))) * gopFactor + 0.4;
            return mbps * 1e6 / 8 * j.Info.Duration;
        }

        static double PixelRatio(MediaInfo mi)
        {
            double fps = mi.Fps > 0 ? Math.Min(mi.Fps, 240) : 60;
            return (double)mi.Width * mi.Height * fps / (1920.0 * 1080 * 60);
        }

        static string MetaText(MediaInfo mi)
        {
            string res;
            if (mi.Height >= 2100) res = "4K";
            else if (mi.Height >= 1400) res = "1440p";
            else if (mi.Height >= 1060) res = "1080p";
            else if (mi.Height >= 700) res = "720p";
            else res = mi.Width + "\u00d7" + mi.Height;
            string fps = Math.Abs(mi.Fps - Math.Round(mi.Fps)) < 0.05 ? Math.Round(mi.Fps).ToString(Inv) : mi.Fps.ToString("0.##", Inv);
            var parts = new List<string> { res, fps + " fps" };
            if (mi.Duration > 0) parts.Add(Fmt(mi.Duration));
            if (mi.FileBytes > 0) parts.Add(FmtSize(mi.FileBytes));
            return string.Join("  \u00b7  ", parts);
        }

        // ---------- save folder ----------

        void ChooseFolder()
        {
            string p = FolderPicker.Pick(hwnd, "Where should EditReady save the copies?", folder);
            if (p != null) folder = p;
            if (!Directory.Exists(folder)) saveNext.IsChecked = true;
            else saveFolder.IsChecked = true;
            RefreshFolderRow();
            SaveSettings();
            UpdateView();
        }

        void RefreshFolderRow()
        {
            bool show = saveFolder.IsChecked == true && Directory.Exists(folder);
            folderRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            folderText.Text = folder;
            folderText.ToolTip = folder;
        }

        // ---------- settings ----------

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                foreach (string line in File.ReadAllLines(SettingsFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    if (k == "folder") folder = v;
                    else if (k == "save" && v == "folder" && Directory.Exists(folder)) saveFolder.IsChecked = true;
                    else if (k == "gop") { if (v == "5") gopFast.IsChecked = true; else if (v == "30") gopSmall.IsChecked = true; }
                    else if (k == "tipDismissed") tipDismissed = v == "1";
                }
                if (saveFolder.IsChecked == true && !Directory.Exists(folder)) saveNext.IsChecked = true;
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllLines(SettingsFile, new[]
                {
                    "folder=" + folder,
                    "save=" + (saveFolder.IsChecked == true ? "folder" : "next"),
                    "gop=" + CurrentGop(),
                    "tipDismissed=" + (tipDismissed ? "1" : "0")
                });
            }
            catch { }
        }

        // ---------- helpers ----------

        void UI(Action a)
        {
            if (Win.Dispatcher.HasShutdownStarted) return;
            Win.Dispatcher.BeginInvoke(a);
        }

        void Flash()
        {
            var fi = new Native.FLASHWINFO();
            fi.cbSize = (uint)Marshal.SizeOf(fi);
            fi.hwnd = hwnd;
            fi.dwFlags = 15; // flash caption + taskbar until the window comes to the front
            fi.uCount = 3;
            try { Native.FlashWindowEx(ref fi); } catch { }
        }

        static int Run(string exe, string args, out string stdout)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    stdout = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    err.Wait(2000);
                    return p.ExitCode;
                }
            }
            catch { stdout = ""; return -1; }
        }

        static string Q(string s) { return "\"" + s + "\""; }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        static string Fmt(double sec)
        {
            if (double.IsNaN(sec) || double.IsInfinity(sec) || sec < 0) return "?";
            var ts = TimeSpan.FromSeconds(Math.Round(sec));
            return ts.TotalHours >= 1 ? string.Format(Inv, "{0}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds)
                                      : string.Format(Inv, "{0}:{1:00}", ts.Minutes, ts.Seconds);
        }

        static string FmtLong(double sec)
        {
            if (double.IsNaN(sec) || double.IsInfinity(sec) || sec < 0) return "?";
            if (sec < 60) return Math.Max(1, (int)Math.Round(sec)) + " sec";
            int min = (int)Math.Round(sec / 60);
            if (min < 60) return min + " min";
            return (min / 60) + " h " + (min % 60) + " min";
        }

        static string FmtSize(long b)
        {
            return b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0", Inv) + " GB" : Math.Max(1, (long)Math.Round(b / (double)(1L << 20))).ToString(Inv) + " MB";
        }
    }

    static class Native
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        [DllImport("user32.dll")]
        public static extern bool FlashWindowEx(ref FLASHWINFO fi);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);
    }

    // The modern Windows folder picker (the old FolderBrowserDialog is a tiny tree view).
    static class FolderPicker
    {
        public static string Pick(IntPtr owner, string title, string start)
        {
            var dlg = (IFileDialog)new FileOpenDialogCom();
            try
            {
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | 0x20 | 0x40); // FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM
                dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
                {
                    try
                    {
                        IShellItem si;
                        Native.SHCreateItemFromParsingName(start, IntPtr.Zero, typeof(IShellItem).GUID, out si);
                        dlg.SetFolder(si);
                    }
                    catch { }
                }
                if (dlg.Show(owner) != 0) return null;
                IShellItem res;
                dlg.GetResult(out res);
                string path;
                res.GetDisplayName(0x80058000, out path); // SIGDN_FILESYSPATH
                return path;
            }
            catch { return null; }
            finally { Marshal.ReleaseComObject(dlg); }
        }
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    class FileOpenDialogCom { }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint fos);
        void GetOptions(out uint fos);
        void SetDefaultFolder(IShellItem si);
        void SetFolder(IShellItem si);
        void GetFolder(out IShellItem si);
        void GetCurrentSelection(out IShellItem si);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem si);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    }
}
