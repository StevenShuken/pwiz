﻿/*
 * Original author: Viktoria Dorfer <viktoria.dorfer .at. fh-hagenberg.at>,
 *                  Bioinformatics Research Group, University of Applied Sciences Upper Austria
 *
 * Copyright 2020 University of Applied Sciences Upper Austria
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using MSAmanda.Core;
using MSAmanda.Utils;
using MSAmanda.InOutput;
using MSAmanda.InOutput.Output;
using MSAmandaSettings = MSAmanda.InOutput.Settings;
using pwiz.Common.Chemistry;
using pwiz.Common.SystemUtil;
using pwiz.Skyline.Model.DocSettings;
using MSAmandaEnzyme = MSAmanda.Utils.Enzyme;
using OperationCanceledException = System.OperationCanceledException;
using Thread = System.Threading.Thread;
using pwiz.Skyline.Properties;

namespace pwiz.Skyline.Model.DdaSearch
{
    public class MSAmandaSearchWrapper : AbstractDdaSearchEngine
    {
        internal MSAmandaSettings Settings { get; private set; } = new MSAmandaSettings();
        private static MSHelper helper = new MSHelper();
        private SettingsFile AvailableSettings;
        private OutputMzid mzID;
        private MSAmandaSearch SearchEngine;
        private OutputParameters _outputParameters;
        private MSAmandaSpectrumParser amandaInputParser;

        public override event NotificationEventHandler SearchProgressChanged;

        private const string UNIMOD_FILENAME = "Unimod.xml";
        private const string ENZYME_FILENAME = "enzymes.xml";
        private const string INSTRUMENTS_FILENAME = "Instruments.xml";
        #region todo add as additional settings
        private const string AMANDA_DB_DIRECTORY = "C:\\ProgramData\\MSAmanda2.0\\DB";
        private const string AMANDA_SCRATCH_DIRECTORY = "C:\\ProgramData\\MSAmanda2.0\\Scratch";
        private const double CORE_USE_PERCENTAGE = 100;
        private const int MAX_NUMBER_PROTEINS = 10000;
        private const int MAX_NUMBER_SPECTRA = 1000;
        private const string AmandaResults = "AmandaResults";
        private const string AmandaDB = "AmandaDB";
        private const string AmandaMap = "AmandaMap";
        private readonly string _baseDir = "C:\\ProgramData\\MSAmanda2.0";
        #endregion

        public MSAmandaSearchWrapper()
        {
            if (!helper.IsInitialized())
            {
                helper.InitLogWriter(_baseDir);
                
            }
            helper.SearchProgressChanged += Helper_SearchProgressChanged;
            var folderForMappings = Path.Combine(_baseDir, AmandaMap);
            // creates dir if not existing
            Directory.CreateDirectory(folderForMappings);
            mzID = new OutputMzid(folderForMappings);
            AvailableSettings = new SettingsFile(helper, Settings, mzID);
            AvailableSettings.AllEnzymes = new List<MSAmandaEnzyme>();
            AvailableSettings.AllModifications = new List<Modification>();

            using (var d = new CurrentDirectorySetter(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)))
            {
                if (!AvailableSettings.ParseEnzymeFile(ENZYME_FILENAME, "", AvailableSettings.AllEnzymes))
                    throw new Exception(string.Format(Resources.DdaSearch_MSAmandaSearchWrapper_enzymes_file__0__not_found, ENZYME_FILENAME));
                if (!AvailableSettings.ParseUnimodFile(UNIMOD_FILENAME, AvailableSettings.AllModifications))
                    throw new Exception(string.Format(Resources.DdaSearch_MSAmandaSearchWrapper_unimod_file__0__not_found, UNIMOD_FILENAME));
                if (!AvailableSettings.ParseOboFiles())
                    throw new Exception(Resources.DdaSearch_MSAmandaSearchWrapper_Obo_files_not_found);
                if (!AvailableSettings.ReadInstrumentsFile(INSTRUMENTS_FILENAME))
                    throw new Exception(string.Format(Resources.DdaSearch_MSAmandaSearchWrapper_Instruments_file_not_found, INSTRUMENTS_FILENAME));
            }
        }

        private void Helper_SearchProgressChanged(string message)
        {
            SearchProgressChanged?.Invoke(this, new MessageEventArgs(){Message = message});
        }

        public override void SetEnzyme(pwiz.Skyline.Model.DocSettings.Enzyme enzyme, int maxMissedCleavages)
        {
            MSAmandaEnzyme e = AvailableSettings.AllEnzymes.Find(enz => enz.Name.ToUpper() == enzyme.Name.ToUpper());
            if (e != null)
            {
                Settings.MyEnzyme = e;
                Settings.MissedCleavages = maxMissedCleavages;
            }
            else
            {
                MSAmandaEnzyme enz = new MSAmandaEnzyme() 
                {
                  Name = enzyme.Name,
                  CleavageSites = enzyme.IsNTerm ? enzyme.CleavageN : enzyme.CleavageC,
                  CleavageInhibitors = enzyme.IsNTerm ? enzyme.RestrictN : enzyme.RestrictC,
                  Offset= enzyme.IsNTerm? 0 : 1,
                  Specificity = enzyme.IsSemiCleaving ? MSAmandaEnzyme.CLEAVAGE_SPECIFICITY.SEMI : MSAmandaEnzyme.CLEAVAGE_SPECIFICITY.FULL
                };
                Settings.MyEnzyme = enz;
                Settings.MissedCleavages = maxMissedCleavages;
            }
        }

        public override string[] FragmentIons
        {
            get { return Settings.ChemicalData.Instruments.Keys.ToArray(); }
        }
        public override string EngineName { get { return @"MS Amanda"; } }
        public override Bitmap SearchEngineLogo
        {
            get { return Properties.Resources.MSAmandaLogo; }
        }

        public override void SetPrecursorMassTolerance(MzTolerance tol)
        {
            Settings.Ms1Tolerance = new Tolerance(tol.Value, (MassUnit) tol.Unit);
        }

        public override void SetFragmentIonMassTolerance(MzTolerance tol)
        {
            Settings.Ms2Tolerance = new Tolerance(tol.Value, (MassUnit)tol.Unit);
        }

        public override void SetFragmentIons(string ions)
        {
            if (Settings.ChemicalData.Instruments.ContainsKey(ions))
            {
                Settings.ChemicalData.CurrentInstrumentSetting = Settings.ChemicalData.Instruments[ions];
            }
        }

        private List<FastaDBFile> GetFastaFileList()
        {
            List<FastaDBFile> files = new List<FastaDBFile>();
            foreach (string f in FastaFileNames)
            {
                AFastaFile file = new AFastaFile();
                file.FullPath = f;
                file.NeatName = Path.GetFileNameWithoutExtension(f);
                files.Add(new FastaDBFile() { fastaTarged = file});
            }
            return files;
        }

        private void InitializeEngine(CancellationTokenSource token, string spectrumFileName)
        {
            _outputParameters = new OutputParameters();
            _outputParameters.FastaFiles = FastaFileNames.ToList();
            _outputParameters.DBFile = FastaFileNames[0];
            //2 == mzid
            _outputParameters.SetOutputFileFormat(2);
            _outputParameters.IsPercolatorOutput = true;
            _outputParameters.SpectraFiles = new List<string>() { spectrumFileName};
            Settings.GenerateDecoyDb = true;
            Settings.ConsideredCharges.Clear();
            Settings.ConsideredCharges.Add(2);
            Settings.ConsideredCharges.Add(3);
            Settings.ChemicalData.UseMonoisotopicMass = true;
            Settings.ReportBothBestHitsForTD = false;
            mzID.Settings = Settings;
            SearchEngine = new MSAmandaSearch(helper, _baseDir, _outputParameters, Settings, token);
            SearchEngine.InitializeOutputMZ(mzID);
        }
    
        public override bool Run(CancellationTokenSource tokenSource)
        {
            bool success = true;
            try
            {
                using (var c = new CurrentCultureSetter(CultureInfo.InvariantCulture))
                {
                    foreach (var rawFileName in SpectrumFileNames)
                    {
                        tokenSource.Token.ThrowIfCancellationRequested();
                        try
                        {
                            InitializeEngine(tokenSource, rawFileName.GetSampleLocator());
                            amandaInputParser = new MSAmandaSpectrumParser(rawFileName.GetSampleLocator(), Settings.ConsideredCharges, true);
                            SearchEngine.SetInputParser(amandaInputParser);
                            SearchEngine.PerformSearch(_outputParameters.DBFile);
                        }
                        finally
                        {
                            SearchEngine.Dispose();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                helper.WriteMessage(Resources.DdaSearch_Search_is_canceled, true);
                success = false;
            }
            catch (Exception ex)
            {
                helper.WriteMessage(string.Format(Resources.DdaSearch_Search_failed__0, ex.Message), true);
                success = false;
            }

            if (tokenSource.IsCancellationRequested)
                success = false;
            
            return success;
        }

        public override void SetModifications(IEnumerable<StaticMod> modifications, int maxVariableMods)
        {
            Settings.SelectedModifications.Clear();
            foreach (var item in modifications)
            {
                string name = item.Name.Split(' ')[0];
                var elemsFromUnimod = AvailableSettings.AllModifications.FindAll(m => m.Title == name);
                if (elemsFromUnimod.Count> 0)
                {
                    foreach (char aa in item.AminoAcids)
                    {
                        var elem = elemsFromUnimod.Find(m => m.AA == aa);
                        if (elem != null)
                        {
                            Modification modClone = new Modification(elem);
                            modClone.Fixed = !item.IsVariable;
                            Settings.SelectedModifications.Add(modClone);
                        }
                        else
                        {
                            Settings.SelectedModifications.Add(GenerateNewModification(item, aa));
                        }
                    }
                }
                else
                {
                    Settings.SelectedModifications.AddRange(GenerateNewModificationsForEveryAA(item));
                }
            }
        }

        private List<Modification> GenerateNewModificationsForEveryAA(StaticMod mod)
        {
            List<Modification> mods = new List<Modification>();
            foreach (var a in mod.AAs) {
                mods.Add(GenerateNewModification(mod, a));
       
            }
            return mods;
        }

        private Modification GenerateNewModification(StaticMod mod, char a)
        {
            return new Modification(mod.ShortName, mod.Name, mod.MonoisotopicMass.HasValue ? mod.MonoisotopicMass.Value : 0.0,
                mod.AverageMass.HasValue ? mod.AverageMass.Value : 0.0, a, !mod.IsVariable, mod.Losses.Select(l => l.MonoisotopicMass).ToArray(),
                mod.Terminus.HasValue && mod.Terminus.Value == ModTerminus.N,
                mod.Terminus.HasValue && mod.Terminus.Value == ModTerminus.C,
                mod.UnimodId.HasValue ? mod.UnimodId.Value : 0, false);
        }
  }
}
