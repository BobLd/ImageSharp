// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Linq;

namespace SixLabors.ImageSharp.Formats.WebP.Lossless
{
    internal class HistogramEncoder
    {
        /// <summary>
        /// Number of partitions for the three dominant (literal, red and blue) symbol costs.
        /// </summary>
        private const int NumPartitions = 4;

        /// <summary>
        /// The size of the bin-hash corresponding to the three dominant costs.
        /// </summary>
        private const int BinSize = NumPartitions * NumPartitions * NumPartitions;

        /// <summary>
        /// Maximum number of histograms allowed in greedy combining algorithm.
        /// </summary>
        private const int MaxHistoGreedy = 100;

        private const uint NonTrivialSym = 0xffffffff;

        public static void GetHistoImageSymbols(int xSize, int ySize, Vp8LBackwardRefs refs, int quality, int histoBits, int cacheBits, List<Vp8LHistogram> imageHisto, Vp8LHistogram tmpHisto, short[] histogramSymbols)
        {
            int histoXSize = histoBits > 0 ? LosslessUtils.SubSampleSize(xSize, histoBits) : 1;
            int histoYSize = histoBits > 0 ? LosslessUtils.SubSampleSize(ySize, histoBits) : 1;
            int imageHistoRawSize = histoXSize * histoYSize;
            int entropyCombineNumBins = BinSize;
            short[] mapTmp = new short[imageHistoRawSize];
            short[] clusterMappings = new short[imageHistoRawSize];
            int numUsed = imageHistoRawSize;
            var origHisto = new List<Vp8LHistogram>(imageHistoRawSize);
            for (int i = 0; i < imageHistoRawSize; i++)
            {
                origHisto.Add(new Vp8LHistogram(cacheBits));
            }

            // Construct the histograms from the backward references.
            HistogramBuild(xSize, histoBits, refs, origHisto);

            // Copies the histograms and computes its bitCost. histogramSymbols is optimized.
            HistogramCopyAndAnalyze(origHisto, imageHisto, ref numUsed, histogramSymbols);

            var entropyCombine = (numUsed > entropyCombineNumBins * 2) && (quality < 100);
            if (entropyCombine)
            {
                var binMap = mapTmp;
                var numClusters = numUsed;
                double combineCostFactor = GetCombineCostFactor(imageHistoRawSize, quality);
                HistogramAnalyzeEntropyBin(imageHisto, binMap);

                // Collapse histograms with similar entropy.
                HistogramCombineEntropyBin(imageHisto, ref numUsed, histogramSymbols, clusterMappings, tmpHisto, binMap, entropyCombineNumBins, combineCostFactor);

                OptimizeHistogramSymbols(imageHisto, clusterMappings, numClusters, mapTmp, histogramSymbols);
            }

            float x = quality / 100.0f;

            // Cubic ramp between 1 and MaxHistoGreedy:
            int thresholdSize = (int)(1 + (x * x * x * (MaxHistoGreedy - 1)));
            bool doGreedy = HistogramCombineStochastic(imageHisto, ref numUsed, thresholdSize);
            if (doGreedy)
            {
                HistogramCombineGreedy(imageHisto);
            }

            // Find the optimal map from original histograms to the final ones.
            RemoveEmptyHistograms(imageHisto);
            HistogramRemap(origHisto, imageHisto, histogramSymbols);
        }

        private static void RemoveEmptyHistograms(List<Vp8LHistogram> histograms)
        {
            int size = 0;
            var indicesToRemove = new List<int>();
            for (int i = 0; i < histograms.Count; i++)
            {
                if (histograms[i] == null)
                {
                    indicesToRemove.Add(i);
                    continue;
                }

                histograms[size++] = histograms[i];
            }

            foreach (int index in indicesToRemove.OrderByDescending(i => i))
            {
                histograms.RemoveAt(index);
            }
        }

        /// <summary>
        /// Construct the histograms from the backward references.
        /// </summary>
        private static void HistogramBuild(int xSize, int histoBits, Vp8LBackwardRefs backwardRefs, List<Vp8LHistogram> histograms)
        {
            int x = 0, y = 0;
            int histoXSize = LosslessUtils.SubSampleSize(xSize, histoBits);
            using List<PixOrCopy>.Enumerator backwardRefsEnumerator = backwardRefs.Refs.GetEnumerator();
            while (backwardRefsEnumerator.MoveNext())
            {
                PixOrCopy v = backwardRefsEnumerator.Current;
                int ix = ((y >> histoBits) * histoXSize) + (x >> histoBits);
                histograms[ix].AddSinglePixOrCopy(v, false);
                x += v.Len;
                while (x >= xSize)
                {
                    x -= xSize;
                    y++;
                }
            }
        }

        /// <summary>
        /// Partition histograms to different entropy bins for three dominant (literal,
        /// red and blue) symbol costs and compute the histogram aggregate bitCost.
        /// </summary>
        private static void HistogramAnalyzeEntropyBin(List<Vp8LHistogram> histograms, short[] binMap)
        {
            int histoSize = histograms.Count;
            var costRange = new DominantCostRange();

            // Analyze the dominant (literal, red and blue) entropy costs.
            for (int i = 0; i < histoSize; i++)
            {
                costRange.UpdateDominantCostRange(histograms[i]);
            }

            // bin-hash histograms on three of the dominant (literal, red and blue)
            // symbol costs and store the resulting bin_id for each histogram.
            for (int i = 0; i < histoSize; i++)
            {
                binMap[i] = (short)costRange.GetHistoBinIndex(histograms[i], NumPartitions);
            }
        }

        private static void HistogramCopyAndAnalyze(List<Vp8LHistogram> origHistograms, List<Vp8LHistogram> histograms, ref int numUsed, short[] histogramSymbols)
        {
            int numUsedOrig = numUsed;
            var indicesToRemove = new List<int>();
            for (int clusterId = 0, i = 0; i < origHistograms.Count; i++)
            {
                Vp8LHistogram histo = origHistograms[i];
                histo.UpdateHistogramCost();

                // Skip the histogram if it is completely empty, which can happen for tiles
                // with no information (when they are skipped because of LZ77).
                if (!histo.IsUsed[0] && !histo.IsUsed[1] && !histo.IsUsed[2] && !histo.IsUsed[3] && !histo.IsUsed[4])
                {
                    indicesToRemove.Add(i);
                }
                else
                {
                    histograms[i] = (Vp8LHistogram)histo.DeepClone();
                    histogramSymbols[i] = (short)clusterId++;
                }
            }

            foreach (int index in indicesToRemove.OrderByDescending(v => v))
            {
                origHistograms.RemoveAt(index);
                histograms.RemoveAt(index);
            }
        }

        private static void HistogramCombineEntropyBin(List<Vp8LHistogram> histograms, ref int numUsed, short[] clusters, short[] clusterMappings, Vp8LHistogram curCombo, short[] binMap, int numBins, double combineCostFactor)
        {
            var binInfo = new HistogramBinInfo[BinSize];
            for (int idx = 0; idx < numBins; idx++)
            {
                binInfo[idx].First = -1;
                binInfo[idx].NumCombineFailures = 0;
            }

            // By default, a cluster matches itself.
            for (int idx = 0; idx < histograms.Count; idx++)
            {
                clusterMappings[idx] = (short)idx;
            }

            var indicesToRemove = new List<int>();
            for (int idx = 0; idx < histograms.Count; idx++)
            {
                if (histograms[idx] == null)
                {
                    continue;
                }

                int binId = binMap[idx];
                int first = binInfo[binId].First;
                if (first == -1)
                {
                    binInfo[binId].First = (short)idx;
                }
                else
                {
                    // Try to merge #idx into #first (both share the same binId)
                    double bitCost = histograms[idx].BitCost;
                    double bitCostThresh = -bitCost * combineCostFactor;
                    double currCostDiff = histograms[first].AddEval(histograms[idx], bitCostThresh, curCombo);

                    if (currCostDiff < bitCostThresh)
                    {
                        // Try to merge two histograms only if the combo is a trivial one or
                        // the two candidate histograms are already non-trivial.
                        // For some images, 'tryCombine' turns out to be false for a lot of
                        // histogram pairs. In that case, we fallback to combining
                        // histograms as usual to avoid increasing the header size.
                        bool tryCombine = (curCombo.TrivialSymbol != NonTrivialSym) || ((histograms[idx].TrivialSymbol == NonTrivialSym) && (histograms[first].TrivialSymbol == NonTrivialSym));
                        int maxCombineFailures = 32;
                        if (tryCombine || binInfo[binId].NumCombineFailures >= maxCombineFailures)
                        {
                            // Move the (better) merged histogram to its final slot.
                            Vp8LHistogram tmp = curCombo;
                            curCombo = histograms[first];
                            histograms[first] = tmp;

                            histograms[idx] = null;
                            indicesToRemove.Add(idx);
                            clusterMappings[clusters[idx]] = clusters[first];
                        }
                        else
                        {
                            binInfo[binId].NumCombineFailures++;
                        }
                    }
                }
            }

            foreach (int index in indicesToRemove.OrderByDescending(i => i))
            {
                histograms.RemoveAt(index);
            }
        }

        /// <summary>
        /// Given a Histogram set, the mapping of clusters 'clusterMapping' and the
        /// current assignment of the cells in 'symbols', merge the clusters and assign the smallest possible clusters values.
        /// </summary>
        private static void OptimizeHistogramSymbols(List<Vp8LHistogram> histograms, short[] clusterMappings, int numClusters, short[] clusterMappingsTmp, short[] symbols)
        {
            int clusterMax;
            bool doContinue = true;

            // First, assign the lowest cluster to each pixel.
            while (doContinue)
            {
                doContinue = false;
                for (int i = 0; i < numClusters; i++)
                {
                    int k;
                    k = clusterMappings[i];
                    while (k != clusterMappings[k])
                    {
                        clusterMappings[k] = clusterMappings[clusterMappings[k]];
                        k = clusterMappings[k];
                    }

                    if (k != clusterMappings[i])
                    {
                        doContinue = true;
                        clusterMappings[i] = (short)k;
                    }
                }
            }

            // Create a mapping from a cluster id to its minimal version.
            clusterMax = 0;
            clusterMappingsTmp.AsSpan().Fill(0);

            // Re-map the ids.
            for (int i = 0; i < symbols.Length; i++)
            {
                int cluster = clusterMappings[symbols[i]];
                if (cluster > 0 && clusterMappingsTmp[cluster] == 0)
                {
                    clusterMax++;
                    clusterMappingsTmp[cluster] = (short)clusterMax;
                }

                symbols[i] = clusterMappingsTmp[cluster];
            }
        }

        /// <summary>
        /// Perform histogram aggregation using a stochastic approach.
        /// </summary>
        /// <returns>true if a greedy approach needs to be performed afterwards, false otherwise.</returns>
        private static bool HistogramCombineStochastic(List<Vp8LHistogram> histograms, ref int numUsed, int minClusterSize)
        {
            var rand = new Random();
            int triesWithNoSuccess = 0;
            int outerIters = numUsed;
            int numTriesNoSuccess = outerIters / 2;

            if (histograms.Count < minClusterSize)
            {
                return true;
            }

            // Priority queue of histogram pairs. Its size impacts the quality of the compression and the speed:
            // the smaller the faster but the worse for the compression.
            var histoPriorityList = new List<HistogramPair>();
            int histoQueueMaxSize = histograms.Count * histograms.Count;

            // Fill the initial mapping.
            int[] mappings = new int[histograms.Count];
            for (int j = 0, iter = 0; iter < histograms.Count; iter++)
            {
                mappings[j++] = iter;
            }

            // Collapse similar histograms
            for (int iter = 0; iter < outerIters && numUsed >= minClusterSize && ++triesWithNoSuccess < numTriesNoSuccess; iter++)
            {
                double bestCost = (histoPriorityList.Count == 0) ? 0.0d : histoPriorityList[0].CostDiff;
                int bestIdx1 = -1;
                int bestIdx2 = 1;
                int numTries = numUsed / 2; // TODO: should that be histogram.Count/2?
                uint randRange = (uint)((numUsed - 1) * numUsed);

                // Pick random samples.
                for (int j = 0; numUsed >= 2 && j < numTries; j++)
                {
                    // Choose two different histograms at random and try to combine them.
                    uint tmp = (uint)(rand.Next() % randRange);
                    double currCost;
                    int idx1 = (int)(tmp / (numUsed - 1));
                    int idx2 = (int)(tmp % (numUsed - 1));
                    if (idx2 >= idx1)
                    {
                        idx2++;
                    }

                    idx1 = mappings[idx1];
                    idx2 = mappings[idx2];

                    // Calculate cost reduction on combination.
                    currCost = HistoPriorityListPush(histoPriorityList, histoQueueMaxSize, histograms, idx1, idx2, bestCost);

                    // Found a better pair?
                    if (currCost < 0)
                    {
                        bestCost = currCost;

                        // Empty the queue if we reached full capacity.
                        if (histoPriorityList.Count == histoQueueMaxSize)
                        {
                            break;
                        }
                    }
                }

                if (histoPriorityList.Count == 0)
                {
                    continue;
                }

                // Get the best histograms.
                bestIdx1 = histoPriorityList[0].Idx1;
                bestIdx2 = histoPriorityList[0].Idx2;

                // Pop bestIdx2 from mappings.
                var mappingIndex = Array.BinarySearch(mappings, bestIdx2);
                // TODO: memmove(mapping_index, mapping_index + 1, sizeof(*mapping_index) *((*num_used) - (mapping_index - mappings) - 1));

                // Merge the histograms and remove bestIdx2 from the queue.
                HistogramAdd(histograms[bestIdx2], histograms[bestIdx1], histograms[bestIdx1]);
                histograms.ElementAt(bestIdx1).BitCost = histoPriorityList[0].CostCombo;
                histograms.RemoveAt(bestIdx2);
                numUsed--;

                var indicesToRemove = new List<int>();
                int lastIndex = histoPriorityList.Count - 1;
                for (int j = 0; j < histoPriorityList.Count;)
                {
                    HistogramPair p = histoPriorityList.ElementAt(j);
                    bool isIdx1Best = p.Idx1 == bestIdx1 || p.Idx1 == bestIdx2;
                    bool isIdx2Best = p.Idx2 == bestIdx1 || p.Idx2 == bestIdx2;
                    bool doEval = false;

                    // The front pair could have been duplicated by a random pick so
                    // check for it all the time nevertheless.
                    if (isIdx1Best && isIdx2Best)
                    {
                        indicesToRemove.Add(lastIndex);
                        numUsed--;
                        lastIndex--;
                        continue;
                    }

                    // Any pair containing one of the two best indices should only refer to
                    // best_idx1. Its cost should also be updated.
                    if (isIdx1Best)
                    {
                        p.Idx1 = bestIdx1;
                        doEval = true;
                    }
                    else if (isIdx2Best)
                    {
                        p.Idx2 = bestIdx1;
                        doEval = true;
                    }

                    // Make sure the index order is respected.
                    if (p.Idx1 > p.Idx2)
                    {
                        int tmp = p.Idx2;
                        p.Idx2 = p.Idx1;
                        p.Idx1 = tmp;
                    }

                    if (doEval)
                    {
                        // Re-evaluate the cost of an updated pair.
                        HistoListUpdatePair(histograms[p.Idx1], histograms[p.Idx2], 0.0d, p);
                        if (p.CostDiff >= 0.0d)
                        {
                            indicesToRemove.Add(lastIndex);
                            lastIndex--;
                            numUsed--;
                            continue;
                        }
                    }

                    HistoListUpdateHead(histoPriorityList, p);
                    j++;
                }

                triesWithNoSuccess = 0;
            }

            bool doGreedy = numUsed <= minClusterSize;

            return doGreedy;
        }

        private static void HistogramCombineGreedy(List<Vp8LHistogram> histograms)
        {
            int histoSize = histograms.Count;

            // Priority list of histogram pairs.
            var histoPriorityList = new List<HistogramPair>();
            int maxSize = histoSize * histoSize;

            for (int i = 0; i < histograms.Count; i++)
            {
                for (int j = i + 1; j < histograms.Count; j++)
                {
                    HistoPriorityListPush(histoPriorityList, maxSize, histograms, i, j, 0.0d);
                }
            }

            while (histoPriorityList.Count > 0)
            {
                int idx1 = histoPriorityList[0].Idx1;
                int idx2 = histoPriorityList[0].Idx2;
                HistogramAdd(histograms[idx2], histograms[idx1], histograms[idx1]);
                histograms[idx1].BitCost = histoPriorityList[0].CostCombo;

                // Remove merged histogram.
                // TODO: can the element be removed instead? histograms.RemoveAt(idx2);
                histograms[idx2] = null;

                // Remove pairs intersecting the just combined best pair.
                for (int i = 0; i < histoPriorityList.Count;)
                {
                    HistogramPair p = histoPriorityList.ElementAt(i);
                    if (p.Idx1 == idx1 || p.Idx2 == idx1 || p.Idx1 == idx2 || p.Idx2 == idx2)
                    {
                        // Replace item at pos i with the last one and shrinking the list.
                        histoPriorityList[i] = histoPriorityList[histoPriorityList.Count - 1];
                        histoPriorityList.RemoveAt(histoPriorityList.Count - 1);
                    }
                    else
                    {
                        HistoListUpdateHead(histoPriorityList, p);
                        i++;
                    }
                }

                // Push new pairs formed with combined histogram to the list.
                for (int i = 0; i < histograms.Count; i++)
                {
                    if (i == idx1 || histograms[i] == null)
                    {
                        continue;
                    }

                    HistoPriorityListPush(histoPriorityList, maxSize, histograms, idx1, i, 0.0d);
                }
            }
        }

        private static void HistogramRemap(List<Vp8LHistogram> input, List<Vp8LHistogram> output, short[] symbols)
        {
            int inSize = symbols.Length;
            int outSize = output.Count;
            if (outSize > 1)
            {
                for (int i = 0; i < inSize; i++)
                {
                    int bestOut = 0;
                    double bestBits = double.MaxValue;
                    for (int k = 0; k < outSize; k++)
                    {
                        double curBits = output[k].AddThresh(input[i], bestBits);
                        if (k == 0 || curBits < bestBits)
                        {
                            bestBits = curBits;
                            bestOut = k;
                        }
                    }

                    symbols[i] = (short)bestOut;
                }
            }
            else
            {
                for (int i = 0; i < inSize; i++)
                {
                    symbols[i] = 0;
                }
            }

            for (int i = 0; i < inSize; i++)
            {
                if (input[i] == null)
                {
                    continue;
                }

                int idx = symbols[i];
                input[i].Add(output[idx], output[idx]);
            }
        }

        /// <summary>
        /// Create a pair from indices "idx1" and "idx2" provided its cost
        /// is inferior to "threshold", a negative entropy.
        /// </summary>
        /// <returns>The cost of the pair, or 0. if it superior to threshold.</returns>
        private static double HistoPriorityListPush(List<HistogramPair> histoList, int maxSize, List<Vp8LHistogram> histograms, int idx1, int idx2, double threshold)
        {
            var pair = new HistogramPair();

            if (histoList.Count == maxSize)
            {
                return 0.0d;
            }

            if (idx1 > idx2)
            {
                int tmp = idx2;
                idx2 = idx1;
                idx1 = tmp;
            }

            pair.Idx1 = idx1;
            pair.Idx2 = idx2;
            Vp8LHistogram h1 = histograms[idx1];
            Vp8LHistogram h2 = histograms[idx2];

            HistoListUpdatePair(h1, h2, threshold, pair);

            // Do not even consider the pair if it does not improve the entropy.
            if (pair.CostDiff >= threshold)
            {
                return 0.0d;
            }

            histoList.Add(pair);

            HistoListUpdateHead(histoList, pair);

            return pair.CostDiff;
        }

        /// <summary>
        /// Update the cost diff and combo of a pair of histograms. This needs to be called when the the histograms have been merged with a third one.
        /// </summary>
        private static void HistoListUpdatePair(Vp8LHistogram h1, Vp8LHistogram h2, double threshold, HistogramPair pair)
        {
            double sumCost = h1.BitCost + h2.BitCost;
            h1.GetCombinedHistogramEntropy(h2, sumCost + threshold, costInitial: pair.CostCombo, out var cost);
            pair.CostCombo = cost;
            pair.CostDiff = pair.CostCombo - sumCost;
        }

        /// <summary>
        /// Check whether a pair in the list should be updated as head or not.
        /// </summary>
        private static void HistoListUpdateHead(List<HistogramPair> histoList, HistogramPair pair)
        {
            if (pair.CostDiff < histoList[0].CostDiff)
            {
                // Replace the best pair.
                var oldIdx = histoList.IndexOf(pair);
                histoList[oldIdx] = histoList[0];
                histoList[0] = pair;
            }
        }

        private static void HistogramAdd(Vp8LHistogram a, Vp8LHistogram b, Vp8LHistogram output)
        {
            a.Add(b, output);
            output.TrivialSymbol = (a.TrivialSymbol == b.TrivialSymbol) ? a.TrivialSymbol : NonTrivialSym;
        }

        private static double GetCombineCostFactor(int histoSize, int quality)
        {
            double combineCostFactor = 0.16d;
            if (quality < 90)
            {
                if (histoSize > 256)
                {
                    combineCostFactor /= 2.0d;
                }

                if (histoSize > 512)
                {
                    combineCostFactor /= 2.0d;
                }

                if (histoSize > 1024)
                {
                    combineCostFactor /= 2.0d;
                }

                if (quality <= 50)
                {
                    combineCostFactor /= 2.0d;
                }
            }

            return combineCostFactor;
        }
    }
}
