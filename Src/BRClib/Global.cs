﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using NLog;

namespace BRClib
{
    using Env = Environment;

    public static class Global
    {
        public static void Init(bool portableMode)
        {
            _baseDir = portableMode ? AppDomain.CurrentDomain.BaseDirectory :
                Path.Combine(Env.GetFolderPath(Env.SpecialFolder.ApplicationData),
                             "BlenderRenderController");

            _scriptsDir = Path.Combine(_baseDir, "scripts");
            _configFilePath = Path.Combine(_baseDir, SETTINGS_FILE);

            Directory.CreateDirectory(_scriptsDir);

            GetProjInfoScript = Path.Combine(_scriptsDir, PyGetProjInfo);
            MixdownScript = Path.Combine(_scriptsDir, PyMixdownAudio);

            var fw = ScriptsToDisk();
            Trace.WriteLine(fw + " scripts written to disk");

            Settings = Load(_configFilePath);
            NlogSetup(portableMode);
        }

        public static ConfigModel Settings { get; private set; }

        public static string GetProjInfoScript { get; private set; }
        public static string MixdownScript { get; private set; }

        public static void SaveSettings()
        {
            SaveInternal(Settings, _configFilePath);
        }

        public static bool CheckProgramPaths()
        {
            bool blenderFound = true, ffmpegFound = true;
            string ePath;

            if (!File.Exists(Settings.BlenderProgram))
            {
                ePath = FindProgram("blender");
                blenderFound = ePath != null;

                if (blenderFound) Settings.BlenderProgram = ePath;
            }

            if (!File.Exists(Settings.FFmpegProgram))
            {
                ePath = FindProgram("ffmpeg");
                ffmpegFound = ePath != null;

                if (ffmpegFound) Settings.FFmpegProgram = ePath;
            }

            return blenderFound && ffmpegFound;
        }

        // workaround Process.Start not working on .NET core
        public static void ShellOpen(string file_uri)
        {
            var stInfo = new ProcessStartInfo(file_uri) {
                UseShellExecute = true
            };
            Process.Start(stInfo);
        }


        static string _baseDir, _scriptsDir, _configFilePath;

        const string SETTINGS_FILE = "brc_settings.json";
        const string PyGetProjInfo = "get_project_info.py";
        const string PyMixdownAudio = "mixdown_audio.py";

        static ConfigModel GetDefaults()
        {
            string blender = "blender", ffmpeg = "ffmpeg";
            var defBlenderDir = string.Empty;
            var defFFmpegDir = string.Empty;

            switch (Env.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    // GetFolderPath only returns the 32bit ProgramFiles, cause we're a 32bit program
                    var vName = Env.Is64BitOperatingSystem ? "ProgramW6432" : "ProgramFiles";
                    var pf = Env.GetEnvironmentVariable(vName);

                    blender += ".exe";
                    ffmpeg += ".exe";

                    defBlenderDir = Path.Combine(pf, "Blender Foundation", "Blender");
                    defFFmpegDir = AppDomain.CurrentDomain.BaseDirectory;
                    break;
                case PlatformID.Unix:
                case PlatformID.MacOSX:
                // TODO: Mac specific guess?
                // remember: Platform == Unix on both Linux and MacOSX
                default:
                    defBlenderDir = defFFmpegDir = "/usr/bin";
                    break;
            }

            return new ConfigModel
            {
                BlenderProgram = Path.Combine(defBlenderDir, blender),
                FFmpegProgram = Path.Combine(defFFmpegDir, ffmpeg),
                LoggingLevel = 0,
                DisplayToolTips = true,
                AfterRender = AfterRenderAction.MIX_JOIN,
                Renderer = Renderer.BLENDER_RENDER,
                RecentProjects = new List<string>(),
                DeleteChunksFolder = false
            };
        }

        static string FindProgram(string name)
        {
            if (Env.OSVersion.Platform == PlatformID.Win32NT)
            {
                name += ".exe";
            }

            var PATH = Env.GetEnvironmentVariable("PATH").Split(Path.PathSeparator);
            var found = PATH.Select(x => Path.Combine(x, name)).FirstOrDefault(File.Exists);

            return found;
        }

        static int ScriptsToDisk()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var assembName = assembly.GetName().Name;
            var resourcesStuff = new Dictionary<string, string>
            {
                [GetProjInfoScript] = assembName + ".Scripts." + PyGetProjInfo,
                [MixdownScript] = assembName + ".Scripts." + PyMixdownAudio
            };

            // Write files to disk if they don't exist, or are diferent
            int filesWritten = 0;
            var md5 = System.Security.Cryptography.MD5.Create();
            byte[] header = Encoding.UTF8.GetBytes("# Generated by BRC, do not modify!\n");

            foreach (var pair in resourcesStuff)
            {
                var filePath = pair.Key;
                var resPath = pair.Value;

                using (Stream assmbStream = assembly.GetManifestResourceStream(resPath), memStream = new MemoryStream())
                using (var fs = File.Open(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                {
                    // write header to embedded stream
                    memStream.Write(header, 0, header.Length);
                    assmbStream.CopyTo(memStream);
                    memStream.Seek(0, SeekOrigin.Begin);

                    bool eq = md5.ComputeHash(fs).SequenceEqual(md5.ComputeHash(memStream));
                    if (!eq)
                    {
                        // Seek both streams to begining
                        memStream.Seek(0, SeekOrigin.Begin);
                        fs.Seek(0, SeekOrigin.Begin);

                        memStream.CopyTo(fs);
                        filesWritten++;
                    }
                }
            }

            return filesWritten;
        }

        static ConfigModel Load(string configFile)
        {
            if (File.Exists(configFile))
            {
                return JsonConvert.DeserializeObject<ConfigModel>(File.ReadAllText(configFile));
            }

            // create file w/ default settings
            var def = GetDefaults();
            SaveInternal(def, configFile);

            return def;
        }

        static void SaveInternal(ConfigModel def, string filepath)
        {
            var json = JsonConvert.SerializeObject(def, Formatting.Indented);
            File.WriteAllText(filepath, json);
        }

        static void NlogSetup(bool portableMode)
        {
            LogLevel lLvl;

            switch (Settings.LoggingLevel)
            {
                case 1: lLvl = LogLevel.Info; break;
                case 2: lLvl = LogLevel.Trace; break;
                default: return;
            }

            string fileTgt = "brclogfile";
            if (portableMode) fileTgt += "_p";

            var target = LogManager.Configuration.FindTargetByName(fileTgt);
            LogManager.Configuration.AddRule(lLvl, LogLevel.Fatal, target, "*");

            LogManager.ReconfigExistingLoggers();
        }

    }
}
