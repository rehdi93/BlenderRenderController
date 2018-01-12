﻿using BRClib.Commands;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;

using Timer = System.Timers.Timer;
using ScriptShelf = BRClib.Scripts.Shelf;
using BRClib.Extentions;

namespace BRClib
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
    public class RenderManager
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();

        // State trackers
        private ConcurrentDictionary<Chunk, Process> _workingRenderData;
        IReadOnlyList<Chunk> _chunkList;
        private ConcurrentHashSet<int> _framesRendered;
        private int _chunksToDo, _chunksInProgress,
                    _initalChunkCount, _currentIndex,
                    _maxConcurrency;

        private Timer timer;

        // Progress stuff
        int _reportCount;
        const int PROG_STACK_SIZE = 3;

        Project _proj;

        // after render
        Dictionary<string, ProcessResult> _afterRenderReport;
        List<Process> _afterRenderProcList;
        Task<bool> _arState;
        const string MIX_KEY = "mixdown";
        const string CONCAT_KEY = "concat";
        CancellationTokenSource _arCts;

        const string BAD_RESULT_FMT = "Exit code: {0}\n\n" +
                                        "Std Error:\n{1}\n\n\n" +
                                        "Std Output:\n{2}";

        const string CHUNK_TXT = "chunklist.txt",
                     CHUNK_DIR = "chunks";



        public int NumberOfFramesRendered => _framesRendered.Count;

        public bool InProgress { get => timer.Enabled; }

        public bool WasAborted { get; private set; }

        public string BlenderProgram { get; set; }

        public string FFmpegProgram { get; set; }

        public Renderer Renderer { get; set; }

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

        public AfterRenderAction Action { get; set; } = AfterRenderAction.NOTHING;

        object _syncLock = new object();



        /// <summary>
        /// Raised when AfterRender actions finish
        /// </summary>
        public event EventHandler Finished;

        public event EventHandler<RenderProgressInfo> ProgressChanged;

        public event EventHandler<AfterRenderAction> AfterRenderStarted;

        
        public RenderManager()
        {
            timer = new Timer
            {
                Interval = 100,
                AutoReset = true,
            };

            timer.Elapsed += delegate 
            {
                lock (_syncLock)
                {
                    TryQueueRenderProcess();
                }
            };
        }

        public RenderManager(Project project) : this()
        {
            Setup(project);
        }


        /// <summary>
        /// Setup <see cref="RenderManager"/> using a <see cref="ProjectSettings"/> object
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
            timer.Start();
        }


        /// <summary>
        /// Aborts the render process
        /// </summary>
        public void Abort()
        {
            if (InProgress)
            {
                timer.Stop();
                WasAborted = true;
                _arCts.Cancel();
                DisposeProcesses();
                logger.Warn("RENDER ABORTED");
            }
        }

        public bool GetAfterRenderResult()
        {
            return _arState.Result;
        }

        private void CheckForValidProperties()
        {
            string[] mustHaveValues = { BlenderProgram, FFmpegProgram };

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
            var dictionary = _proj.ChunkList.ToDictionary(k => k, CreateRenderProcess);
            _workingRenderData = new ConcurrentDictionary<Chunk, Process>(dictionary);
            _chunkList = dictionary.Keys.ToList();
            _afterRenderProcList = new List<Process>();
            _framesRendered = new ConcurrentHashSet<int>();
            _currentIndex = 0;
            _chunksInProgress = 0;
            _chunksToDo = dictionary.Count;
            _initalChunkCount = dictionary.Count;
            _afterRenderReport = new Dictionary<string, ProcessResult>();
            _arCts = new CancellationTokenSource();
            WasAborted = false;
            _reportCount = 0;
            _maxConcurrency = _proj.MaxConcurrency;
        }

        Process CreateRenderProcess(Chunk chunk)
        {
            var renderCom = new Process();
            var info = new ProcessStartInfo
            {
                FileName = BlenderProgram,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = string.Format("-b \"{0}\" -o \"{1}\" -E {2} -s {3} -e {4} -a",
                                            _proj.BlendFilePath,
                                            Path.Combine(ChunksFolderPath, _proj.ProjectName + "-#"),
                                            Renderer,
                                            chunk.Start,
                                            chunk.End),
            };
            renderCom.StartInfo = info;
            renderCom.EnableRaisingEvents = true;
            renderCom.OutputDataReceived += RenderCom_OutputDataReceived;
            renderCom.Exited += RenderCom_Exited;

            return renderCom;
        }

        bool CreateChunksTxtFile(string chunksFolder)
        {
            // TODO: Find a way to get the videos file ext
            // before rendering ends
            var fileListSorted = Utilities.GetChunkFiles(chunksFolder);

            if (fileListSorted.Count == 0)
            {
                return false;
            }

            string chunksTxtFile = Path.Combine(chunksFolder, CHUNK_TXT);

            //write txt for FFmpeg concatenation
            using (StreamWriter partListWriter = new StreamWriter(chunksTxtFile))
            {
                foreach (var filePath in fileListSorted)
                {
                    partListWriter.WriteLine("file '{0}'", filePath);
                }
            }

            return true;
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

        // decrement counts when a process exits, stops the timer when the count 
        // reaches 0
        private void RenderCom_Exited(object sender, EventArgs e)
        {
            --_chunksInProgress;
         
            logger.Trace("Render proc exited with code {0}", (sender as Process).ExitCode);

            // check if the overall render is done
            if (Interlocked.Decrement(ref _chunksToDo) == 0)
            {
                timer.Stop();

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
                var proc = _workingRenderData[currentChunk];
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
            DisposeProcesses();
            logger.Info("RENDER FINISHED");

            // Send a '100%' ProgressReport
            ReportProgress(NumberOfFramesRendered, _initalChunkCount);

            _arState = Task.Factory.StartNew(AfterRenderProc, this.Action, _arCts.Token);

            _arState.ContinueWith(t =>
            {
                Finished?.Raise(this, EventArgs.Empty);
            },
            TaskContinuationOptions.ExecuteSynchronously);
        }

        void ReportProgress(int framesRendered, int chunksCompleted)
        {
            // Stagger report sending to save some heap allocation
            if (_reportCount++ % PROG_STACK_SIZE == 0)
            {
                ProgressChanged?.Raise(this, new RenderProgressInfo(framesRendered, chunksCompleted));
            }
        }

        private void DisposeProcesses()
        {
            var procList = _workingRenderData.Values.ToList();
            procList.AddRange(_afterRenderProcList);

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
                    Debug.WriteLine(ex.ToString(), "RenderManager Proc dispose");
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

            AfterRenderStarted?.Raise(this, action);

            if (action == AfterRenderAction.NOTHING)
            {
                return true;
            }

            logger.Info("AfterRender started. Action: {0}", action);

            if ((action & AfterRenderAction.JOIN) != 0)
            {
                // create chunklist.txt
                if (!CreateChunksTxtFile(ChunksFolderPath))
                {
                    // did not create txtFile
                    throw new Exception("Failed to create chunklist.txt");
                }
            }

            // full range of frames
            var fullc = new Chunk(_chunkList.First().Start, _chunkList.Last().End);

            var videoExt = Path.GetExtension(Utilities.GetChunkFiles(ChunksFolderPath).First());
            var projFinalPath = Path.Combine(_proj.OutputPath, _proj.ProjectName + videoExt);
            var chunksTxt = Path.Combine(ChunksFolderPath, CHUNK_TXT);
            var mixdownPath = Path.Combine(_proj.OutputPath, MixdownFile);
            var mixdownTmpScript = ScriptShelf.MixdownAudio;


            MixdownCmd mixdown = new MixdownCmd(BlenderProgram)
            {
                BlendFile = _proj.BlendFilePath,
                MixdownScript = mixdownTmpScript,
                Range = fullc,
                OutputFolder = _proj.OutputPath
            };

            ConcatCmd concat = new ConcatCmd(FFmpegProgram)
            {
                ConcatTextFile = chunksTxt,
                OutputFile = projFinalPath,
                Duration = _proj.Duration,
                MixdownFile = mixdownPath
            };

            Process mixdownProc = null, concatProc = null;

            _afterRenderReport.Add(MIX_KEY, new ProcessResult());
            _afterRenderReport.Add(CONCAT_KEY, new ProcessResult());


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
            var badProcResults = _afterRenderProcList.Where(p => p != null && p.ExitCode != 0).ToArray();

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
                            WriteReport(sw, "Mixdown ", _afterRenderReport[MIX_KEY]);
                        }

                        if (concatProc?.ExitCode != 0)
                        {
                            WriteReport(sw, "FFMpeg concat ", _afterRenderReport[CONCAT_KEY]);
                        } 
                    }

                }

                return false;
            }
            else
            {
                return !_arCts.IsCancellationRequested;
            }


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
                proc.Start();

                _afterRenderProcList.Add(proc);

                var readOutput = proc.StandardOutput.ReadToEndAsync();
                var readError = proc.StandardError.ReadToEndAsync();

                readOutput.ContinueWith(t => _afterRenderReport[key].StdOutput = t.Result);
                readError.ContinueWith(t => _afterRenderReport[key].StdError = t.Result);

                proc.WaitForExit();

                _afterRenderReport[key].ExitCode = proc.ExitCode;
            }
        }


    }

    public interface IRenderSettings
    {
        string Blender { get; set; }
        string FFmpeg { get; set; }
        AfterRenderAction AfterRender { get; set; }

    }
}
