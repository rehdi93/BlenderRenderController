﻿// Part of the Blender Render Controller project
// https://github.com/RedRaptor93/BlenderRenderController
// Copyright 2017-present Pedro Oliva Rodrigues
// This code is released under the MIT licence

using NLog;
using System;
using System.Windows.Forms;
using System.Xml;
using System.Collections.Generic;


namespace BlenderRenderController
{
    using static BRClib.Global;

    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            string cmdFile = null;
            if (args.Length > 0) cmdFile = args[0];

            string portableStr = System.Configuration.ConfigurationManager.AppSettings["portable"];
            bool portable = bool.TryParse(portableStr, out bool ptb) ? ptb : false;

            Init(portable);
            NlogSetup(portable);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            BrcForm form;

            if (cmdFile != null) {
                form = new BrcForm(cmdFile);
            } else {
                form = new BrcForm();
            }

            Application.Run(form);

            SaveSettings();
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
