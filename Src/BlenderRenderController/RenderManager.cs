﻿// Part of the Blender Render Controller project
// https://github.com/RedRaptor93/BlenderRenderController
// Copyright 2017-present Pedro Oliva Rodrigues
// This code is released under the MIT licence

using BRClib;
using BRClib.Commands;
using BRClib.Extentions;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Windows.Forms.Timer;

namespace BlenderRenderController.Render
{
    /// <summary>
    /// Manages the render process of a list of <see cref="Chunk"/>s.
    /// </summary>
    /// <remarks>
    /// Properties values must remain the same once <see cref="Start"/> is called,
    /// attempting to call <see cref="Setup(Project)"/> while 
    /// <see cref="InProgress"/> is true will throw an Exception, 
    /// you must <seealso cref="Abort"/> or wait for the process to finish before 
    /// changing any values
    /// </remarks>
    public class RenderManager : IBrcRenderManager
    {

        #region Fields

        private static Logger logger = LogManager.GetCurrentClassLogger();

        // State trackers
        List<Process> _processes;
        IReadOnlyList<Chunk> _chunkList;
        private ConcurrentHashSet<int> _framesRendered;
        private int _chunksToDo, _chunksInProgress,
                    _initalChunkCount, _currentIndex,
                    _maxConcurrency;

        private Timer _timer;

        // Progress stuff
        int _reportCount;
        const int PROG_STACK_SIZE = 3;

        Project _proj;
        AfterRenderAction _action;
        Renderer _renderer;

        BrcSettings _setts = Services.Settings.Current;

        // after render
        List<Process> _arProcesses;
        const string MIX_KEY = "mixdown";
        const string CONCAT_KEY = "concat";
        CancellationTokenSource _arCts;

        const string BAD_RESULT_FMT = "Exit code: {0}\n\n" +
                                        "Std Error:\n{1}\n\n\n" +
                                        "Std Output:\n{2}";

        const string CHUNK_TXT = "chunklist.txt",
                     CHUNK_DIR = "chunks";

        //object _syncLock = new object();
        
        #endregion


        public int NumberOfFramesRendered => _framesRendered.Count;

        public bool InProgress { get => _timer.Enabled; }

        public bool WasAborted { get; private set; }


        string ChunksFolderPath
        {
            get
            {
                if (_proj == null || string.IsNullOrWhiteSpace(_proj.OutputPath))
                    return null;

                return Path.Combine(_proj.OutputPath, CHUNK_DIR);
            }
        }

        string MixdownFile
        {
            get
            {
                if (_proj == null) return null;

                var mixdownFmt = _proj.FFmpegAudioCodec;
                var projName = _proj.ProjectName;

                if (projName == null) return null;

                switch (mixdownFmt)
                {
                    case "PCM":
                        return Path.ChangeExtension(projName, "wav");
                    case "VORBIS":
                        return Path.ChangeExtension(projName, "ogg");
                    case null:
                    case "NONE":
                        return Path.ChangeExtension(projName, "ac3");
                    default:
                        return Path.ChangeExtension(projName, mixdownFmt.ToLower());
                }
            }
        }

        public AfterRenderAction Action => _action;

        public Renderer Renderer => _renderer;




        /// <summary>
        /// Raised when AfterRender actions finish
        /// </summary>
        public event EventHandler<BrcRenderResult> Finished;

        public event EventHandler<RenderProgressInfo> ProgressChanged;

        public event EventHandler<AfterRenderAction> AfterRenderStarted;

        
        public RenderManager()
        {
            _timer = new Timer
            {
                Interval = 100,
            };

            _timer.Tick += delegate 
            {
                TryQueueRenderProcess();
            };
        }

        public RenderManager(Project project) : this()
        {
            Setup(project, _setts.AfterRender, _setts.Renderer);
        }


        /// <summary>
        /// Setup <see cref="RenderManager"/> to render the Chunks in the <see cref="Project"/> 
        /// </summary>
        /// <param name="project"></param>
        public void Setup(Project project)
        {
            if (InProgress)
            {
                Abort();
                throw new InvalidOperationException("Cannot change settings while a render is in progress!");
            }

            _proj = project;

            _action = _setts.AfterRender;
            _renderer = _setts.Renderer;
        }
        /// <summary>
        /// Setup <see cref="RenderManager"/> to render the Chunks in the <see cref="Project"/> and
        /// override the default acton and renderer
        /// </summary>
        /// <param name="project"></param>
        /// <param name="action"></param>
        /// <param name="renderer"></param>
        public void Setup(Project project, AfterRenderAction action, Renderer renderer)
        {
            Setup(project);

            _action = action;
            _renderer = renderer;
        }


        /// <summary>
        /// Starts rendering 
        /// </summary>
        public void StartAsync()
        {
            // do not start if its already in progress
            if (InProgress)
            {
                Abort();
                throw new InvalidOperationException("A render is already in progress");
            }

            CheckForValidProperties();

            ResetFields();

            logger.Info("RENDER STARTING");
            _timer.Start();
        }


        /// <summary>
        /// Aborts the render process
        /// </summary>
        public void Abort()
        {
            if (InProgress)
            {
                _timer.Stop();
                WasAborted = true;
                _arCts.Cancel();
                DisposeProcesses();
                logger.Warn("RENDER ABORTED");

                Finished?.Invoke(this, BrcRenderResult.Aborted);
            }
        }

        /// <summary>
        /// Analizes the files in the specified folder and returns
        /// a list of valid chunks, ordered by frame-range
        /// </summary>
        /// <param name="chunkFolderPath"></param>
        /// <returns></returns>
        public static List<string> GetChunkFiles(string chunkFolderPath)
        {
            var dirFiles = Directory.EnumerateFiles(chunkFolderPath, "*.*",
                                            SearchOption.TopDirectoryOnly);

            string[] validExts = RenderFormats.VideoFileExts;

            var orderedChunks = dirFiles
                .Where(f =>
                {
                    var ext = Path.GetExtension(f);
                    var split = f.Split('-');

                    // format: FILENAME-fStart-fEnd.ext
                    if (split.Length > 2 && validExts.Contains(ext))
                    {
                        // only add files with frame range
                        var nstr = split[split.Length - 2];
                        return int.TryParse(nstr, out int x);
                    }

                    return false;
                })
                .OrderBy(f =>
                {
                    //sort files in list by starting frame
                    var split = f.Split('-');
                    var nstr = split[split.Length - 2];
                    return int.Parse(nstr);
                });

            return orderedChunks.ToList();
        }


        private void CheckForValidProperties()
        {
            string[] mustHaveValues = { _setts.BlenderProgram, _setts.FFmpegProgram };

            if (mustHaveValues.Any(x => string.IsNullOrWhiteSpace(x)))
            {
                throw new Exception("Required info missing");
            }

            if (_proj == null)
            {
                throw new Exception("Invalid settings");
            }

            if (_proj.ChunkList.Count == 0)
            {
                throw new Exception("Chunk list is empty");
            }

            if (!File.Exists(_proj.BlendFilePath))
            {
                throw new FileNotFoundException("Could not find 'blend' file", _proj.BlendFilePath);
            }

            if (!Directory.Exists(ChunksFolderPath))
            {
                try
                {
                    Directory.CreateDirectory(ChunksFolderPath);
                }
                catch (Exception inner)
                {
                    throw new Exception("Could not create 'chunks' folder", inner);
                }
            }
        }

        void ResetFields()
        {
            _chunkList = _proj.ChunkList.ToList();
            _processes = _chunkList.Select(CreateRenderProcess).ToList();
            _arProcesses = new List<Process>();

            _framesRendered = new ConcurrentHashSet<int>();
            _currentIndex = 0;
            _chunksInProgress = 0;
            _chunksToDo = _chunkList.Count;
            _initalChunkCount = _chunkList.Count;
            _arCts = new CancellationTokenSource();
            WasAborted = false;
            _reportCount = 0;
            _maxConcurrency = _proj.MaxConcurrency;
        }

        Process CreateRenderProcess(Chunk chunk)
        {
            var renderCom = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _setts.BlenderProgram,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = string.Format("-b \"{0}\" -o \"{1}\" -E {2} -s {3} -e {4} -a",
                                            _proj.BlendFilePath,
                                            Path.Combine(ChunksFolderPath, _proj.ProjectName + "-#"),
                                            _renderer,
                                            chunk.Start,
                                            chunk.End),
                },
                EnableRaisingEvents = true
            };

            renderCom.OutputDataReceived += RenderCom_OutputDataReceived;
            renderCom.Exited += RenderCom_Exited;

            return renderCom;
        }

        string CreateConcatFile(List<string> chunkFilePaths, string concatDir)
        {
            var concatFile = Path.Combine(concatDir, CHUNK_TXT);
            var sb = new StringBuilder();

            foreach (var filePath in chunkFilePaths)
            {
                sb.AppendFormat("file '{0}'", filePath).AppendLine();
            }

            File.WriteAllText(concatFile, sb.ToString());

            return concatFile;
        }

        string CreateConcatFile(List<string> chunkFilePaths)
        {
            var bDir = Path.GetDirectoryName(chunkFilePaths[0]);
            return CreateConcatFile(chunkFilePaths, bDir);
        }

        // read blender's output to see what frames are beeing rendered
        private void RenderCom_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                if (e.Data.IndexOf("Fra:", StringComparison.InvariantCulture) == 0)
                {
                    var line = e.Data.Split(' ')[0].Replace("Fra:", "");
                    _framesRendered.Add(int.Parse(line));
                }
            }
        }

        // decrement counts when a process exits, stops the timer when the 
        // 'ToDo' count reaches 0
        private void RenderCom_Exited(object sender, EventArgs e)
        {
            --_chunksInProgress;
         
            logger.Trace("Render proc exited with code {0}", (sender as Process).ExitCode);

            // check if the overall render is done
            if (Interlocked.Decrement(ref _chunksToDo) == 0)
            {
                _timer.Stop();

                // all render processes are done at this point
                Debug.Assert(_framesRendered.ToList().Count == _chunkList.TotalLength(),
                            "Frames counted don't match the ChunkList TotalLenght");

                OnChunksFinished();
            }
        }

        private void TryQueueRenderProcess()
        {
            // start new render procs only within the concurrency limit and until the
            // end of ChunkList
            if (_currentIndex < _initalChunkCount && _chunksInProgress < _maxConcurrency)
            {
                var currentChunk = _chunkList[_currentIndex];
                var proc = _processes[_currentIndex];
                proc.Start();
                proc.BeginOutputReadLine();

                _chunksInProgress++;
                _currentIndex++;

                logger.Trace("Started render n. {0}, frames: {1}", _currentIndex, currentChunk);
            }

            ReportProgress(NumberOfFramesRendered, _initalChunkCount - _chunksToDo);
        }

        private void OnChunksFinished()
        {
            bool renderOk = _processes.TrueForAll(p => p.ExitCode == 0);

            if (!renderOk)
            {
                Finished?.Raise(this, BrcRenderResult.ChunkRenderFailed);
                DisposeProcesses();
                logger.Error("One or more render processes did not complete sucessfully");
                return;
            }

            DisposeProcesses();
            logger.Info("RENDER FINISHED");

            // Send a '100%' ProgressReport
            ReportProgress(NumberOfFramesRendered, _initalChunkCount);

            AfterRenderStarted?.Raise(this, Action);

            var arTask = Task.Factory.StartNew(AfterRenderProc, _action, _arCts.Token)
            .ContinueWith(t =>
            {
                BrcRenderResult result;
                if (!t.Result && WasAborted)
                {
                    result = BrcRenderResult.Aborted;
                }
                else
                {
                    result = BrcRenderResult.AllOk;
                }

                return result;
            })
            .ContinueWith(t => Finished?.Raise(this, t.Result));


        }

        void ReportProgress(int framesRendered, int chunksCompleted)
        {
            // Stagger report sending to save some heap allocation
            if (_reportCount++ % PROG_STACK_SIZE == 0)
            {
                ProgressChanged?.Invoke(this, new RenderProgressInfo(framesRendered, chunksCompleted));
            }
        }

        private void DisposeProcesses()
        {
            var procList = _processes.ToList();
            procList.AddRange(_arProcesses);

            foreach (var p in procList)
            {
                try
                {
                    p.Exited -= RenderCom_Exited;
                    p.OutputDataReceived -= RenderCom_OutputDataReceived;

                    if (!p.HasExited)
                    {
                        p.Kill();
                    }
                }
                catch (Exception ex)
                {
                    // Processes may be in an invalid state, just swallow the errors 
                    // since we're diposing them anyway
                    Debug.WriteLine(ex.Message, "RenderManager Proc dispose");
                }
                finally
                {
                    p.Dispose();
                }
            }

        }

        bool AfterRenderProc(object state)
        {
            var action = (AfterRenderAction)state;

            //AfterRenderStarted?.Invoke(this, action);

            if (action == AfterRenderAction.NOTHING)
            {
                return true;
            }

            logger.Info("AfterRender started. Action: {0}", action);

            var chunkFiles = GetChunkFiles(ChunksFolderPath);
            string concatFile = null;

            if (action.HasFlag(AfterRenderAction.JOIN))
            {
                if (chunkFiles.Count == 0)
                {
                    throw new Exception("Failed to query chunk files");
                }

                concatFile = CreateConcatFile(chunkFiles);

                Debug.Assert(File.Exists(concatFile), 
                    "concatFile was not created, but chunkFiles is not empty");
            }

            // full range of frames
            var fullc = new Chunk(_chunkList.First().Start, _chunkList.Last().End);

            var videoExt = Path.GetExtension(chunkFiles.First());
            var projFinalPath = Path.Combine(_proj.OutputPath, _proj.ProjectName + videoExt);
            var mixdownPath = Path.Combine(_proj.OutputPath, MixdownFile);
            var mixdownTmpScript = Services.Scripts.MixdownAudio;


            MixdownCmd mixdown = new MixdownCmd(_setts.BlenderProgram)
            {
                BlendFile = _proj.BlendFilePath,
                MixdownScript = mixdownTmpScript,
                Range = fullc,
                OutputFolder = _proj.OutputPath
            };

            ConcatCmd concat = new ConcatCmd(_setts.FFmpegProgram)
            {
                ConcatTextFile = concatFile,
                OutputFile = projFinalPath,
                Duration = _proj.Duration,
                MixdownFile = mixdownPath
            };

            Process mixdownProc = null, concatProc = null;

            var arReports = new Dictionary<string, ProcessResult>(2);


            if (_arCts.IsCancellationRequested) return false;

            switch (action)
            {
                case AfterRenderAction.JOIN | AfterRenderAction.MIXDOWN:

                    mixdownProc = mixdown.GetProcess();
                    RunProc(ref mixdownProc, MIX_KEY);

                    if (_arCts.IsCancellationRequested) return false;

                    concatProc = concat.GetProcess();
                    RunProc(ref concatProc, CONCAT_KEY);

                    break;
                case AfterRenderAction.JOIN:

                    // null out MixdownFile so it generates the proper Args
                    concat.MixdownFile = null;

                    concatProc = concat.GetProcess();
                    RunProc(ref concatProc, CONCAT_KEY);

                    break;
                case AfterRenderAction.MIXDOWN:

                    mixdownProc = mixdown.GetProcess();
                    RunProc(ref mixdownProc, MIX_KEY);

                    break;
                default:
                    break;
            }

            if (_arCts.IsCancellationRequested) return false;

            // check for bad exit codes
            var badProcResults = _arProcesses.Where(p => p != null && p.ExitCode != 0).ToArray();

            if (badProcResults.Length > 0)
            {
                // create a file report file
                string arReportFile = Path.Combine(_proj.OutputPath, GetRandSulfix("AfterRenderReport_"));

                using (var sw = File.AppendText(arReportFile))
                {

                    // do not write reports if exit code was caused by cancellation
                    if (!_arCts.IsCancellationRequested)
                    {
                        if (mixdownProc?.ExitCode != 0)
                        {
                            WriteReport(sw, "Mixdown ", arReports[MIX_KEY]);
                        }

                        if (concatProc?.ExitCode != 0)
                        {
                            WriteReport(sw, "FFMpeg concat ", arReports[CONCAT_KEY]);
                        } 
                    }

                }

                return false;
            }
            else
            {
                return !_arCts.IsCancellationRequested;
            }
            // -----

            void WriteReport(StreamWriter writer, string title, ProcessResult result)
            {
                writer.Write("\n\n");
                writer.Write(title);
                writer.WriteLine(string.Format(BAD_RESULT_FMT, 
                                    result.ExitCode,
                                    result.StdError,
                                    result.StdOutput));
            }

            string GetRandSulfix(string baseName)
            {
                var tmp = Path.GetRandomFileName();
                return baseName + Path.ChangeExtension(tmp, "txt");
            }

            void RunProc(ref Process proc, string key)
            {
                string stdOut = string.Empty;
                string stdErr = string.Empty;

                // its important to read the streams asynchronously, to avoid deadlocks
                proc.OutputDataReceived += (s, e) => ReadStreamToString(e.Data, ref stdOut);
                proc.ErrorDataReceived += (s, e) => ReadStreamToString(e.Data, ref stdErr);

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                _arProcesses.Add(proc);

                proc.WaitForExit();

                arReports.Add(key, new ProcessResult(proc.ExitCode, stdOut, stdErr));

                // ----
                void ReadStreamToString(string data, ref string target)
                {
                    if (data != null)
                    {
                        target += data + Environment.NewLine;
                    }
                }
            }
        }

    }


}
