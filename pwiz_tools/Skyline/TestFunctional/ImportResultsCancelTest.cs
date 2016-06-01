﻿/*
 * Original author: Brian Pratt <bspratt .at. proteinms.net>,
 *                  MacCoss Lab, Department of Genome Sciences, UW
 *
 * Copyright 2016 University of Washington - Seattle, WA
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

using Microsoft.VisualStudio.TestTools.UnitTesting;
using pwiz.Skyline.Controls.Graphs;
using pwiz.Skyline.Model;
using pwiz.Skyline.Model.Results;
using pwiz.Skyline.Properties;
using pwiz.SkylineTestUtil;

namespace pwiz.SkylineTestFunctional
{
    [TestClass]
    public class ImportResultsCancelTest : AbstractFunctionalTestEx
    {
        /// <summary>
        /// Verify that the import results cancel button works
        /// </summary>
        //[TestMethod]  // TODO uncomment this when this test actually passes.  It works fine on its own in Test Explorer, but when run in a loop in SkylineTester it tends to fail, and/or issue warnings like "*** Attempt to complete document with non-final status ***"
        public void TestImportResultsCancel()
        {
            Run(@"TestFunctional\RetentionTimeFilterTest.zip");
        }

        protected override void DoTest()
        {
            TestCancellation(true);  // Start with progress window visible
            TestCancellation(false);  // Start with progress window invisible
        }

        private const int maxTries = 10;

        private void TestCancellation(bool initiallyVisible)
        {
            var files = new[] {"8fmol.mz5", "20fmol.mz5", "40fmol.mz5", "200fmol.mz5"};
            OpenDocument("RetentionTimeFilterTest.sky");
            var skyfile = initiallyVisible ? "TestImportResultsCancelA.sky" : "TestImportResultsCancelB.sky";
            RunUI(() => { SkylineWindow.SaveDocument(TestFilesDir.GetTestPath(skyfile)); });  // Make a clean copy

            // Try individual cancellation - can be timing dependent (do we get to the cancel button quickly enough?) so allow some retry
            for (int retry = 0; retry < maxTries; retry++)
            {
                OpenDocument(skyfile);
                Assert.IsFalse(SkylineWindow.Document.Settings.HasResults);
                Settings.Default.AutoShowAllChromatogramsGraph = initiallyVisible; // Start with progress window hidden?
                Settings.Default.ImportResultsSimultaneousFiles =
                    (int) MultiFileLoader.ImportResultsSimultaneousFileOptions.many; // Ensure all buttons are enabled
                ImportResultsAsync(files);
                WaitForConditionUI(
                    () =>
                        SkylineWindow.ImportingResultsWindow != null &&
                        SkylineWindow.ImportingResultsWindow.ProgressTotalPercent >= 1); // Get at least partway in
                if (!initiallyVisible)
                {
                    RunUI(() => SkylineWindow.ShowAllChromatogramsGraph()); // Turn it on
                }
                var dlg2 = TryWaitForOpenForm<AllChromatogramsGraph>(30000);
                if (dlg2 == null)
                {
                    if (SkylineWindow.Document.IsLoaded)
                    {
                        continue; // Loaded faster than we could react
                    }
                    else
                    {
                        dlg2 = WaitForOpenForm<AllChromatogramsGraph>();
                    }
                }
                WaitForCondition(30*1000, () => dlg2.ProgressTotalPercent >= 1); // Get a least a little way in
                int cancelIndex = retry%4;
                var cancelTarget = files[cancelIndex].Replace(".mz5", "");
                RunUI(() => dlg2.FileButtonClick(cancelTarget));
                WaitForDocumentLoaded();
                WaitForClosedAllChromatogramsGraph();
                foreach (var file in files)
                {
                    int index;
                    ChromatogramSet chromatogramSet;
                    var chromatogramSetName = file.Replace(".mz5", "");
                    // Can we find a loaded chromatogram set by this name?
                    SkylineWindow.Document.Settings.MeasuredResults.TryGetChromatogramSet(chromatogramSetName,
                        out chromatogramSet, out index);
                    if (!chromatogramSetName.Equals(cancelTarget))
                    {
                        // Should always find it since we didn't try to cancel this one
                        Assert.AreNotEqual(-1, index, string.Format("Missing chromatogram set {0} after cancelling {1}", chromatogramSetName, cancelTarget));
                    }
                    else if (index == -1)
                    {
                        retry = maxTries; // Success, no more retry needed
                    }
                    else if (retry == maxTries - 1)
                    {
                        Assert.Fail("Failed to cancel individual file import");
                    }
                }
            }

            // Cancelled load should revert to initial document
            CancelAll(files, false);
            CancelAll(files, true);

            // Now try a proper import
            Settings.Default.AutoShowAllChromatogramsGraph = initiallyVisible;
            ImportResultsAsync(files);
            WaitForConditionUI(
                () =>
                    SkylineWindow.ImportingResultsWindow != null &&
                    SkylineWindow.ImportingResultsWindow.ProgressTotalPercent > 2); // Get at least partway in
            if (!initiallyVisible)
            {
                RunUI(() => SkylineWindow.ShowAllChromatogramsGraph()); // Turn it on
            }
            var dlgACG = WaitForOpenForm<AllChromatogramsGraph>();
            Assert.IsTrue(dlgACG.ChromatogramManager.SupportAllGraphs);
            Assert.IsNotNull(dlgACG.SelectedControl, "unable to select a loader control in chromatogram progress window");
            Assert.IsTrue(dlgACG.Width > 500, "Initially hidden chromatogram progress window did not size properly when enabled by user"); // Did it resize properly?
            WaitForDocumentLoaded();
            WaitForClosedAllChromatogramsGraph();
            AssertResult.IsDocumentResultsState(SkylineWindow.Document, "8fmol", 4, 4, 0, 13, 0);
            AssertResult.IsDocumentResultsState(SkylineWindow.Document, "20fmol", 5, 5, 0, 13, 0);
            AssertResult.IsDocumentResultsState(SkylineWindow.Document, "40fmol", 3, 3, 0, 11, 0);
            AssertResult.IsDocumentResultsState(SkylineWindow.Document, "200fmol", 4, 4, 0, 12, 0);
        }

        private void CancelAll(string[] files, bool closeOnFinish)
        {
            Settings.Default.ImportResultsAutoCloseWindow = closeOnFinish;

            for (int retry = 0; retry < maxTries; retry++)
            {
                OpenDocument("RetentionTimeFilterTest.sky");
                Assert.IsFalse(SkylineWindow.Document.Settings.HasResults);
                var docUnloaded = SkylineWindow.Document;
                ImportResultsAsync(files);
                var dlg = WaitForOpenForm<AllChromatogramsGraph>();
                RunUI(dlg.ClickCancel);
                if (closeOnFinish)
                    WaitForClosedAllChromatogramsGraph();
                WaitForDocumentLoaded();
                if (SkylineWindow.Document.Settings.HasResults)
                {
                    if (!closeOnFinish)
                        OkDialog(dlg, dlg.ClickClose);
                }
                else
                {
                    Assert.AreEqual(docUnloaded, SkylineWindow.Document);
                    if (!closeOnFinish)
                    {
                        WaitForConditionUI(() =>
                        {
                            for (int i = 0; i < files.Length; i++)
                            {
                                if (!dlg.IsItemCanceled(i))
                                    return false;
                            }
                            return true;
                        });
                        RunUI(() =>
                        {
                            Assert.IsTrue(dlg.IsUserCanceled);
                            dlg.ClickClose();
                        });
                    }
                    break;
                }
            }
        }
    }
}