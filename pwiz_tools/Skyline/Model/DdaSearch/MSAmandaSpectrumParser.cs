﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MSAmanda.InOutput.Input;
using MSAmanda.Utils;
using pwiz.ProteowizardWrapper;

namespace pwiz.Skyline.Model.DdaSearch
{
    public class MSAmandaSpectrumParser : IParserInput
    {
        public class MSDataRunPath
        {
            public MSDataRunPath(string filepathPossiblyWithRunIndexSuffix)
            {
                var match = Regex.Match(filepathPossiblyWithRunIndexSuffix, @"(.+):(\d+)");
                if (match.Success)
                {
                    Filepath = match.Groups[1].Value;
                    RunIndex = int.Parse(match.Groups[2].Value);
                }
                else
                {
                    Filepath = filepathPossiblyWithRunIndexSuffix;
                    RunIndex = 0;
                }
            }

            public MSDataRunPath(string filepath, int runIndex)
            {
                Filepath = filepath;
                RunIndex = runIndex;
            }

            public string Filepath { get; }
            public int RunIndex { get; }

            public static bool operator ==(MSDataRunPath lhs, MSDataRunPath rhs)
            {
                return lhs.Filepath == rhs.Filepath && lhs.RunIndex == rhs.RunIndex;
            }

            public static bool operator !=(MSDataRunPath lhs, MSDataRunPath rhs)
            {
                return !(lhs == rhs);
            }

            public override int GetHashCode()
            {
                return Filepath.GetHashCode() ^ RunIndex.GetHashCode();
            }

            public override string ToString()
            {
                return $"{Filepath}:{RunIndex}";
            }
        }

        private MsDataFileImpl spectrumFileReader;
        private int specId = 0;
        private int amandaId = 0;
        private List<int> consideredCharges;
        private bool useMonoIsotopicMass;
        private MSDataRunPath msdataRunPath;
        public Dictionary<int, string> SpectTitleMap { get; }

        public MSAmandaSpectrumParser(string file, List<int> charges, bool mono)
        {
            consideredCharges = charges;
            spectrumFileReader = new MsDataFileImpl(file,
                requireVendorCentroidedMS2: MsDataFileImpl.SupportsVendorPeakPicking(file),
                ignoreZeroIntensityPoints: true, trimNativeId: false);
            useMonoIsotopicMass = mono;

            msdataRunPath = new MSDataRunPath(file);
            SpectTitleMap = new Dictionary<int, string>();
        }
        public void Dispose()
        {
            spectrumFileReader.Dispose();
        }

        public bool ReaderIsActiveAndNotEOF()
        {
            return specId < spectrumFileReader.SpectrumCount;
        }

        public List<Spectrum> ParseNextSpectra(int numberOfSpectraToRead, out int nrOfParsed, CancellationToken cancellationToken = new CancellationToken())
        {
            List<Spectrum> spectra = new List<Spectrum>();
            nrOfParsed = 0;
            while (nrOfParsed < numberOfSpectraToRead && specId < spectrumFileReader.SpectrumCount) { 
                MsDataSpectrum spectrum = spectrumFileReader.GetSpectrum(specId);
                cancellationToken.ThrowIfCancellationRequested();
                ++specId;
                if (spectrum.Level != 2)
                    continue;
                Spectrum amandaSpectrum = GenerateMSAmandaSpectrum(spectrum, amandaId);
                if (amandaSpectrum.Precursor.Charge == 0)
                {
                    foreach (int charge in consideredCharges)
                    {
                        Spectrum newSpect = GenerateSpectrum(amandaSpectrum, amandaId,
                            spectrum.Precursors[0].PrecursorMz.Value, charge);
                        SpectTitleMap.Add(amandaId, spectrum.Id);
                        ++amandaId;
                        spectra.Add(newSpect);
                    }
                }
                else
                {
                    SpectTitleMap.Add(amandaId, spectrum.Id);
                    ++amandaId;
                    spectra.Add(amandaSpectrum);
                }

                ++nrOfParsed;
                
            }

            return spectra;
        }

        private Spectrum GenerateSpectrum(Spectrum spec, int id, double mOverZ, int charge)
        {
            Spectrum s = new Spectrum
            {
                //clone peaks
                FragmentsPeaks = new List<AMassCentroid>(spec.FragmentsPeaks.ToArray()),
                SpectrumId = id,
                RT = spec.RT,
                immuneMasses = new SortedSet<double>(),
                immunePeaks = new Dictionary<int, double>()
            };
            s.Precursor.SetMassCharge(mOverZ, charge, useMonoIsotopicMass);
            return s;
        }

        private Spectrum GenerateMSAmandaSpectrum(MsDataSpectrum spectrum, int index)
        {
            Spectrum amandaSpectrum = new Spectrum() { RT = spectrum.RetentionTime.Value, SpectrumId = index };
            amandaSpectrum.FragmentsPeaks = GetFragmentPeaks(spectrum.Mzs, spectrum.Intensities);
            amandaSpectrum.ScanNumber = spectrum.Index;
            if (spectrum.Precursors[0].ChargeState.HasValue && spectrum.Precursors[0].PrecursorMz.HasValue)
                amandaSpectrum.Precursor.SetMassCharge(spectrum.Precursors[0].PrecursorMz.Value, spectrum.Precursors[0].ChargeState.Value, true);
            //SpectTitleMap.Add(index, spectrum.Id);
            return amandaSpectrum;
        }

        private List<AMassCentroid> GetFragmentPeaks(double[] spectrumMzs, double[] spectrumIntensities)
        {
            List<AMassCentroid> peaks = new List<AMassCentroid>();
            for (int i = 0; i < spectrumMzs.Length; ++i)
            {
                peaks.Add(new AMassCentroid() { Charge = 1, Intensity = spectrumIntensities[i], Position = spectrumMzs[i] });
            }

            return peaks;
        }

        public int GetTotalNumberOfSpectra(string spectraFile)
        {
            if (new MSDataRunPath(spectraFile) != msdataRunPath)
                return 0;
            MsDataFileImpl filereader = new MsDataFileImpl(msdataRunPath.Filepath, msdataRunPath.RunIndex, preferOnlyMsLevel: 2);
            return filereader.SpectrumCount;
        }
    }
}
