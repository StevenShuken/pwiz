﻿using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using pwiz.Common.Collections;
using pwiz.SkylineTestUtil;
using pwiz.Skyline.Controls.Graphs;
using pwiz.Skyline.Model;
using pwiz.Skyline.Properties;
using pwiz.Skyline.Util.Extensions;
using ZedGraph;

namespace pwiz.SkylineTestFunctional
{
    [TestClass]
    public class DetectionsPlotTest : AbstractFunctionalTestEx
    {
        private static readonly int[][] REF_DATA =
        {
            new[] { 114, 113, 113, 112, 112, 113},       //q = 0.003, Peptides
            new[] { 123, 122, 122, 121, 121, 121},      //q = 0.003, Precursors
            new[] { 111, 109, 110, 110, 109, 110 },      //q = 0.001, Peptides
            new[] { 120, 118, 119, 119, 117, 117 },      //q = 0.001, Precursors
            new[] { 110, 108, 109, 109, 108, 109 },      //q = 0.001, Peptides, after update
            new[] { 119, 117, 118, 118, 116, 116 }      //q = 0.001, Precursors, after update
        };

        [TestMethod]
        public void TestDetectionsPlot()
        {
            TestFilesZip = @"TestFunctional/DetectionsPlotTest.zip";
            RunFunctionalTest();
        }

        protected override void DoTest()
        {
            OpenDocument(TestFilesDir.GetTestPath(@"DIA-TTOF-tutorial.sky"));

            Trace.WriteLine(this.GetType().Name + ": Test started.");

            RunUI(() => { SkylineWindow.ShowDetectionsReplicateComparisonGraph(); });
            WaitForGraphs();

            GraphSummary graph = SkylineWindow.DetectionsPlot;
            var toolbar = graph.Toolbar as DetectionsToolbar;
            Assert.IsNotNull(toolbar);
            RunUI(() => { toolbar.CbLevel.SelectedItem = DetectionsGraphController.TargetType.PRECURSOR; });
            WaitForGraphs();

            DetectionsPlotPane pane;
            Assert.IsTrue(graph.TryGetGraphPane(out pane));
            Assert.IsTrue(pane.HasToolbar);

            //use properties dialog to update the q-value
            var propDialog = ShowDialog<DetectionToolbarProperties>(() =>
            {
                toolbar.pbProperties_Click(graph.GraphControl, new EventArgs());
            });

            //verify data correct for 2 q-values
            RunUI(() =>
            {
                propDialog.SetQValueTo(0.003f);
                Trace.WriteLine(this.GetType().Name + ": set Q-Value to 0.003");
            });
            OkDialog(propDialog, propDialog.OkDialog);
            Trace.WriteLine(this.GetType().Name + ": properties dialog for Q-Value to 0.003 has been processed.");
            WaitForCondition(() => (DetectionsGraphController.Settings.QValueCutoff == 0.003f));
            AssertDataCorrect(pane, 0, 0.003f);

            //use properties dialog to update the q-value
            propDialog = ShowDialog<DetectionToolbarProperties>(() =>
            {
                toolbar.pbProperties_Click(graph.GraphControl, new EventArgs());
            });
            RunUI(() =>
            {
                propDialog.SetQValueTo(0.001f);
                Trace.WriteLine(this.GetType().Name + ": set Q-Value to 0.001");
            });
            OkDialog(propDialog, propDialog.OkDialog);
            WaitForCondition(() => (DetectionsGraphController.Settings.QValueCutoff == 0.001f));
            AssertDataCorrect(pane, 2, 0.001f);

            //verify the number of the bars on the plot
            RunUI(() =>
            {
            Assert.IsTrue(
                pane.CurveList[0].IsBar && pane.CurveList[0].Points.Count == REF_DATA[0].Length);
            });

            Trace.WriteLine(this.GetType().Name + ": Display and hide tooltip");
            string[] tipText =
            {
                Resources.DetectionPlotPane_Tooltip_Replicate + TextUtil.SEPARATOR_TSV_STR + @"2_SW-B",
                string.Format(Resources.DetectionPlotPane_Tooltip_Count, DetectionsGraphController.TargetType.PRECURSOR) +
                TextUtil.SEPARATOR_TSV_STR + 118.ToString( CultureInfo.CurrentCulture),
                Resources.DetectionPlotPane_Tooltip_CumulativeCount + TextUtil.SEPARATOR_TSV_STR +
                123.ToString( CultureInfo.CurrentCulture),
                Resources.DetectionPlotPane_Tooltip_AllCount + TextUtil.SEPARATOR_TSV_STR +
                115.ToString( CultureInfo.CurrentCulture),
                Resources.DetectionPlotPane_Tooltip_QMedian + TextUtil.SEPARATOR_TSV_STR +
                (6.0d).ToString(@"F1",CultureInfo.CurrentCulture)
            };
            RunUI(() =>
            {
                Assert.IsNotNull(pane.ToolTip);
                pane.PopulateTooltip(1);
                //verify the tooltip text
                CollectionAssert.AreEqual(tipText, pane.ToolTip.TipLines);
            });

            Trace.WriteLine(this.GetType().Name + ": deleting a peptide");
            //test the data correct after a doc change (delete peptide)
            RunUI(() =>
            {
                SkylineWindow.SelectedPath = SkylineWindow.Document.GetPathTo((int)SrmDocument.Level.Molecules, 12);
                SkylineWindow.EditDelete();
            });
            WaitForGraphs();
            WaitForConditionUI(() => DetectionPlotData.GetDataCache().Datas.Any((dat) =>
                    ReferenceEquals(SkylineWindow.DocumentUI, dat.Document) &&
                    DetectionsGraphController.Settings.QValueCutoff == dat.QValueCutoff),
                "Cache is not updated on document change.");

            //verify that the cache is purged after the document update
            RunUI(() =>
            {
                Assert.IsTrue(DetectionPlotData.GetDataCache().Datas.All((dat) =>
                    ReferenceEquals(SkylineWindow.DocumentUI, dat.Document)));
            });
            AssertDataCorrect(pane, 4, 0.001f);

            Trace.WriteLine(this.GetType().Name + ": showing a histogram pane.");
            RunUI(() => { SkylineWindow.ShowDetectionsHistogramGraph(); });
            WaitForGraphs();
            DetectionsHistogramPane paneHistogram;
            var graphHistogram = SkylineWindow.DetectionsPlot;
            Assert.IsTrue(graphHistogram.TryGetGraphPane(out paneHistogram), "Cannot get histogram pane.");
            //display and hide tooltip
            Trace.WriteLine(this.GetType().Name + ": showing histogram tooltip.");
            string[] histogramTipText =
            {
                Resources.DetectionHistogramPane_Tooltip_ReplicateCount + TextUtil.SEPARATOR_TSV_STR +
                5.ToString( CultureInfo.CurrentCulture),
                String.Format(Resources.DetectionHistogramPane_Tooltip_Count, DetectionsGraphController.TargetType.PRECURSOR) +
                TextUtil.SEPARATOR_TSV_STR + 102.ToString( CultureInfo.CurrentCulture),
            };
            RunUI(() =>
            {
                Assert.IsNotNull(paneHistogram.ToolTip, "No tooltip found.");
                paneHistogram.PopulateTooltip(5);
                //verify the tooltip text
                CollectionAssert.AreEqual(histogramTipText, paneHistogram.ToolTip.TipLines);
            });
            RunUI(() =>
            {
                graph.Close();
                graphHistogram.Close();
            });
            WaitForGraphs();
            Trace.WriteLine(this.GetType().Name + ": Test complete.");
        }

        private void AssertDataCorrect(DetectionsPlotPane pane, int refIndex, float qValue, bool record = false)
        {
            DetectionPlotData data = null;
            Trace.WriteLine(this.GetType().Name + $": Waiting for data for qValue {qValue} .");
            WaitForConditionUI(() => (data = pane.CurrentData) != null 
                                           && pane.CurrentData.QValueCutoff == qValue
                                           && DetectionPlotData.GetDataCache().Status == DetectionPlotData.DetectionDataCache.CacheStatus.idle,
                () => $"Retrieving data for qValue {qValue}, refIndex {refIndex} took too long.");
            WaitForGraphs();
            Assert.IsTrue(data.IsValid);

            if (record)
            {
                Console.WriteLine(@"Peptides");
                pane.CurrentData.GetTargetData(DetectionsGraphController.TargetType.PEPTIDE).TargetsCount
                    .ForEach((cnt) => { Debug.Write($"{cnt}, "); });
                Console.WriteLine(@"\nPrecursors");
                pane.CurrentData.GetTargetData(DetectionsGraphController.TargetType.PRECURSOR).TargetsCount
                    .ForEach((cnt) => { Debug.Write($"{cnt}, "); });
            }

            Assert.IsTrue(
                REF_DATA[refIndex].SequenceEqual(
                    pane.CurrentData.GetTargetData(DetectionsGraphController.TargetType.PEPTIDE).TargetsCount));
            Assert.IsTrue(
                REF_DATA[refIndex + 1].SequenceEqual(
                    pane.CurrentData.GetTargetData(DetectionsGraphController.TargetType.PRECURSOR).TargetsCount));
        }
    }
}