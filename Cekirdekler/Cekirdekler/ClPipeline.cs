﻿//    Cekirdekler API: a C# explicit multi-device load-balancer opencl wrapper
//    Copyright(C) 2017 Hüseyin Tuğrul BÜYÜKIŞIK

//   This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.

//    This program is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
//    GNU General Public License for more details.

//    You should have received a copy of the GNU General Public License
//    along with this program.If not, see<http://www.gnu.org/licenses/>.



using Cekirdekler.ClArrays;
using Cekirdekler.Hardware;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cekirdekler
{
    namespace Pipeline
    {
        /// <summary>
        /// <para>Explicit pipelining where each device works on a different stage of pipeline, concurrently, after each push()</para>
        /// <para>To be able to push at each iteration, double-buffering is used for inputs</para>
        /// <para>1 push      (1 thread) = (switch buffer):false</para>
        /// <para>1 more push (1 thread) = (switch buffer)(read+compute+write)[stage-0]:false</para>
        /// <para>1 more push (2 threads)= (switch buffer)(read+compute+write)[stage-0] ++ (switch buffer):false</para>
        /// <para>1 more push (2 threads)= (switch buffer)(read+compute+write)[stage-0] ++ (switch buffer)(read+compute+write)[stage-1]:true</para>
        /// </summary>
        public class ClPipeline
        {
            internal static object syncObj = new object();
            internal int counter { get; set; }
            /// <summary>
            /// pushes data(arrays) from entrance stage, returns true if exit stage has result data on its output(and its target arrays)
            /// </summary>
            /// <returns></returns>
            public bool pushData(object[] data = null, object[] popResultsHere = null)
            {
                if (debug)
                {
                    Console.WriteLine("Pipeline running.");
                    if ((stages == null) || (stages.Length == 0))
                    {
                        Console.WriteLine("Zero pipeline stages.");
                        return false;
                    }
                }

                Parallel.For(0, stages.Length * 2, i =>
                {
                    if (i < stages.Length)
                    {
                        for (int j = 0; j < stages[i].Length; j++)
                        {
                            stages[i][j].run(); // input(stage i) --> output(stage i)
                            if (debug)
                            {
                                lock (syncObj)
                                {
                                    Console.WriteLine("stage-" + i + "-" + j + " compute time: " + stages[i][j].elapsedTime);
                                }
                            }
                        }
                    }
                    else
                    {
                        int k = i - stages.Length;
                        for (int j = 0; j < stages[k].Length; j++)
                        {
                            stages[k][j].forwardResults(k, stages.Length - 1, data, popResultsHere); // duplicate output(stage i) --> duplicate input(stage i+1)
                        }
                    }
                });

                Parallel.For(0, stages.Length, i =>
                {
                    for (int j = 0; j < stages[i].Length; j++)
                    {
                        if (data == null)
                        {
                            if (i != 0)
                                stages[i][j].switchInputBuffers(); // switch all duplicates with real buffers
                        }
                        else
                        {
                            stages[i][j].switchInputBuffers(); // switch all duplicates with real buffers
                        }

                        if (popResultsHere == null)
                        {
                            if (i != (stages.Length - 1))
                                stages[i][j].switchOutputBuffers(); // switch all duplicates with real buffers
                        }
                        else
                        {
                            stages[i][j].switchOutputBuffers(); // switch all duplicates with real buffers
                        }
                    }
                });


                counter++;
                if ((counter > (stages.Length * 2 - 2)) && (data == null) && (popResultsHere == null))
                    return true;
                else if ((counter > (stages.Length * 2 - 1)) && (data != null) && (popResultsHere == null))
                    return true;
                else if ((counter > (stages.Length * 2 - 1)) && (data == null) && (popResultsHere != null))
                    return true;
                else if ((counter > (stages.Length * 2)) && (data != null) && (popResultsHere != null))
                    return true;

                return false;
            }

            // multiple layers per stage, each stage push data to next stage
            internal ClPipelineStage[][] stages;
            internal bool debug { get; set; }
            /// <summary>
            /// only created by one of the stages that are bound together
            /// </summary>
            internal ClPipeline(bool debugLog = false)
            {
                debug = debugLog;
                counter = 0;
            }

        }

        internal class KernelParameters
        {
            public int globalRange { get; set; }
            public int localRange { get; set; }
        }

        /// <summary>
        /// <para>used to build stages of a pipeline</para>
        /// <para>inputs are interpreted as read-only(partial if multi device stage), outputs are interpreted as write-only</para>
        /// <para>hidden buffers are used for sequential logic (keeping xyz coordinates of an nbody algorithm - for example) </para>
        /// <para>addition order of inputs,hiddens,outputs must be same as kernel arguments </para>
        /// <para>multiple inputs can be bound to a single output(copies same data to all inputs), opposite can't </para>
        /// </summary>
        public class ClPipelineStage
        {
            internal ClNumberCruncher numberCruncher;

            // if this is true, it will compute after the input switch
            internal bool inputHasData = false;

            // just to inform push() of whole pipeline
            internal bool outputHasData = false;

            internal ClPipelineStageBuffer[] inputBuffers;
            internal List<ClPipelineStageBuffer> inputBuffersList;

            internal ClPipelineStageBuffer[] outputBuffers;
            internal List<ClPipelineStageBuffer> outputBuffersList;

            internal ClPipelineStageBuffer[] hiddenBuffers;
            internal List<ClPipelineStageBuffer> hiddenBuffersList;

            internal Dictionary<string, KernelParameters> kernelNameToParameters;
            internal Dictionary<string, KernelParameters> initKernelNameToParameters;
            internal ClDevices devices;

            internal string[] kernelNamesToRun;
            internal string kernelsToCompile;

            internal ClPipelineStage previousStage;
            internal ClPipelineStage[] nextStages;
            internal List<ClPipelineStage> nextStagesList;
            internal static object syncObj = new object();
            internal bool initComplete;

            private bool debug { get; set; }

            /// <summary>
            /// creates a stage to form a pipeline with other stages
            /// </summary>
            public ClPipelineStage(bool debugLog = false)
            {
                enqueueMode = false;
                initializerKernelNames = null;
                debug = debugLog;
                initComplete = false;
                nextStagesList = new List<ClPipelineStage>();
                inputBuffersList = new List<ClPipelineStageBuffer>();
                outputBuffersList = new List<ClPipelineStageBuffer>();
                hiddenBuffersList = new List<ClPipelineStageBuffer>();
                kernelNameToParameters = new Dictionary<string, KernelParameters>();
                initKernelNameToParameters = new Dictionary<string, KernelParameters>();
                stageOrder = -1;
            }

            internal Stopwatch timer { get; set; }
            internal double elapsedTime { get; set; }

            /// <summary>
            /// enables enqueued execution of all kernels given as a list, false by default(not enabled)
            /// </summary>
            public bool enqueueMode { get; set; }
            // switch inputs(concurrently all stages) then compute(concurrently all stages, if they received input)
            /// <summary>
            /// runs kernels attached to this stage consecutively (one after another)
            /// </summary>
            /// <param name="initializerKernels">runs only the initializer kernels given by initializerKernel() method</param>
            internal void run(bool initializerKernels = false)
            {
                if (timer == null)
                    timer = new Stopwatch();
                if (debug)
                    Console.WriteLine("pipeline stage running.");
                // initialize buffers and number cruncher
                if (!initComplete)
                {
                    lock (syncObj)
                    {
                        if (numberCruncher == null)
                        {
                            numberCruncher = new ClNumberCruncher(devices, kernelsToCompile, true/* can't enable driver-pipelining but can have more device-pipeline-stages */);
                            if (debug)
                            {
                                numberCruncher.performanceFeed = true;
                                Console.WriteLine("number cruncher setup complete");
                            }
                        }

                        if (inputBuffers != null)
                        {
                            for (int i = 0; i < inputBuffers.Length; i++)
                            {
                                inputBuffers[i].write = false;
                                inputBuffers[i].partialRead = false;
                                inputBuffers[i].read = true;
                            }

                            if (debug)
                                Console.WriteLine("input buffer write flag is unset, read is set, partialread is unset");
                        }

                        if (outputBuffers != null)
                        {
                            for (int i = 0; i < outputBuffers.Length; i++)
                            {
                                outputBuffers[i].write = true;
                                outputBuffers[i].read = false;
                                outputBuffers[i].partialRead = false;
                            }

                            if (debug)
                                Console.WriteLine("output buffer read-partialread flag is unset, write is set");
                        }

                        if (hiddenBuffers != null)
                        {
                            // hidden buffers don't write/read unless its multi gpu
                            // todo: multi-gpu stage buffers will sync
                            for (int i = 0; i < hiddenBuffers.Length; i++)
                            {
                                hiddenBuffers[i].read = false;
                                hiddenBuffers[i].partialRead = false;
                                hiddenBuffers[i].write = false;
                            }

                            if (debug)
                                Console.WriteLine("hidden buffer read-partialread-write flag is unset");
                        }

                        initComplete = true;

                        if (debug)
                            Console.WriteLine("pipeline initialization complete.");
                    }
                }

                timer.Start();

                // to do: move parameter building to initializing
                ClParameterGroup bufferParameters = null;
                ClPipelineStageBuffer ib = null;
                int inputStart = 0, hiddenStart = 0, outputStart = 0;
                if ((inputBuffers != null) && (inputBuffers.Length > 0))
                {
                    ib = inputBuffers[0];
                    inputStart = 1;
                }
                else if ((hiddenBuffers != null) && (hiddenBuffers.Length > 0))
                {
                    ib = hiddenBuffers[0];
                    hiddenStart = 1;
                }
                else if ((outputBuffers != null) && (outputBuffers.Length > 0))
                {
                    ib = outputBuffers[0];
                    outputStart = 1;
                }
                else
                {
                    Console.WriteLine("no buffer found.");
                }

                if (debug)
                {
                    Console.WriteLine("input start = " + inputStart);
                    Console.WriteLine("hidden start = " + hiddenStart);
                    Console.WriteLine("output start = " + outputStart);
                }

                bool parameterStarted = false;
                bool moreThanOneParameter = false;

                if (inputBuffers != null)
                {
                    for (int i = inputStart; i < inputBuffers.Length; i++)
                    {
                        if (!parameterStarted)
                        {
                            bufferParameters = ib.nextParam(inputBuffers[i].buf);

                            parameterStarted = true;
                        }
                        else
                        {
                            bufferParameters = bufferParameters.nextParam(inputBuffers[i].buf);
                        }
                        moreThanOneParameter = true;
                    }
                }

                if (hiddenBuffers != null)
                {
                    for (int i = hiddenStart; i < hiddenBuffers.Length; i++)
                    {
                        if (!parameterStarted)
                        {
                            bufferParameters = ib.nextParam(hiddenBuffers[i].buf);
                            parameterStarted = true;
                        }
                        else
                        {
                            bufferParameters = bufferParameters.nextParam(hiddenBuffers[i].buf);
                        }
                        moreThanOneParameter = true;
                    }
                }

                if (outputBuffers != null)
                {
                    for (int i = outputStart; i < outputBuffers.Length; i++)
                    {
                        if (!parameterStarted)
                        {
                            bufferParameters = ib.nextParam(outputBuffers[i].buf);
                            parameterStarted = true;
                        }
                        else
                        {
                            bufferParameters = bufferParameters.nextParam(outputBuffers[i].buf);
                        }
                        moreThanOneParameter = true;
                    }
                }

                if (debug)
                    Console.WriteLine("kernel parameters are set");

                // parameter building end

                // running kernel
                if (!initializerKernels)
                {
                    if (enqueueMode)
                        if (hiddenBuffers != null)
                        {
                            // hidden buffers don't write/read unless its multi gpu
                            // todo: multi-gpu stage buffers will sync
                            for (int i = 0; i < hiddenBuffers.Length; i++)
                            {
                                hiddenBuffers[i].read = false;
                                hiddenBuffers[i].partialRead = false;
                                hiddenBuffers[i].write = false;

                                var rd = bufferParameters.reads.First;
                                var wr = bufferParameters.writes.First;
                                var rp = bufferParameters.partialReads.First;
                                var arrs = bufferParameters.arrays.First;
                                for (int k = 0; k < bufferParameters.reads.Count; k++)
                                {
                                    if ((arrs.Value == hiddenBuffers[i].buf) || (arrs.Value == hiddenBuffers[i].bufDuplicate))
                                    {
                                        rd.Value = false; wr.Value = false; rp.Value = false;
                                    }
                                    rd = rd.Next; wr = wr.Next; arrs = arrs.Next; rp = rp.Next;
                                }
                            }

                        }

                    // normal kernel execution
                    if (kernelNamesToRun != null)
                    {
                        if (enqueueMode)
                            numberCruncher.enqueueMode = true;
                        for (int i = 0; i < kernelNamesToRun.Length; i++)
                        {
                            if (enqueueMode)
                            {
                                if (i == 0)
                                {
                                    if (inputBuffers != null)
                                    {
                                        for (int j = 0; j < inputBuffers.Length; j++)
                                        {
                                            inputBuffers[j].write = false;
                                            inputBuffers[j].partialRead = false;
                                            inputBuffers[j].read = true;

                                            var rd = bufferParameters.reads.First;
                                            var wr = bufferParameters.writes.First;
                                            var rp = bufferParameters.partialReads.First;
                                            var arrs = bufferParameters.arrays.First;
                                            for (int k = 0; k < bufferParameters.reads.Count; k++)
                                            {
                                                if ((arrs.Value == inputBuffers[j].buf) || (arrs.Value == inputBuffers[j].bufDuplicate))
                                                {
                                                    rd.Value = true; wr.Value = false; rp.Value = false;
                                                }
                                                rd = rd.Next; wr = wr.Next; arrs = arrs.Next; rp = rp.Next;
                                            }
                                        }
                                    }

                                    if (outputBuffers != null)
                                    {
                                        for (int j = 0; j < outputBuffers.Length; j++)
                                        {
                                            outputBuffers[j].write = false;
                                            outputBuffers[j].read = false;
                                            outputBuffers[j].partialRead = false;

                                            var rd = bufferParameters.reads.First;
                                            var wr = bufferParameters.writes.First;
                                            var rp = bufferParameters.partialReads.First;
                                            var arrs = bufferParameters.arrays.First;
                                            for (int k = 0; k < bufferParameters.reads.Count; k++)
                                            {
                                                if ((arrs.Value == outputBuffers[j].buf) || (arrs.Value == outputBuffers[j].bufDuplicate))
                                                {
                                                    rd.Value = false; wr.Value = false; rp.Value = false;
                                                }
                                                rd = rd.Next; wr = wr.Next; arrs = arrs.Next; rp = rp.Next;
                                            }
                                        }
                                    }
                                }
                                else if (i == 1)
                                {
                                    if (inputBuffers != null)
                                    {
                                        for (int j = 0; j < inputBuffers.Length; j++)
                                        {
                                            inputBuffers[j].write = false;
                                            inputBuffers[j].partialRead = false;
                                            inputBuffers[j].read = false;

                                            var rd = bufferParameters.reads.First;
                                            var wr = bufferParameters.writes.First;
                                            var rp = bufferParameters.partialReads.First;
                                            var arrs = bufferParameters.arrays.First;
                                            for (int k = 0; k < bufferParameters.reads.Count; k++)
                                            {
                                                if ((arrs.Value == inputBuffers[j].buf) || (arrs.Value == inputBuffers[j].bufDuplicate))
                                                {
                                                    rd.Value = false; wr.Value = false; rp.Value = false;
                                                }
                                                rd = rd.Next; wr = wr.Next; arrs = arrs.Next; rp = rp.Next;
                                            }
                                        }
                                    }
                                }

                                if (i == (kernelNamesToRun.Length - 1))
                                {
                                    if (outputBuffers != null)
                                    {
                                        for (int j = 0; j < outputBuffers.Length; j++)
                                        {
                                            outputBuffers[j].write = true;
                                            outputBuffers[j].read = false;
                                            outputBuffers[j].partialRead = false;

                                            var rd = bufferParameters.reads.First;
                                            var wr = bufferParameters.writes.First;
                                            var rp = bufferParameters.partialReads.First;
                                            var arrs = bufferParameters.arrays.First;
                                            for (int k = 0; k < bufferParameters.reads.Count; k++)
                                            {
                                                if ((arrs.Value == outputBuffers[j].buf) || (arrs.Value == outputBuffers[j].bufDuplicate))
                                                {
                                                    rd.Value = true; wr.Value = true; rp.Value = false;
                                                }
                                                rd = rd.Next; wr = wr.Next; arrs = arrs.Next; rp = rp.Next;
                                            }
                                        }

                                    }
                                }
                            }

                            if (debug)
                            {
                                Console.WriteLine("running kernel: " + i);
                                Console.WriteLine("more than one parameter: " + moreThanOneParameter);
                            }



                            // normal run
                            if (moreThanOneParameter)
                            {
                                bufferParameters.compute(numberCruncher, i + 1,
                                    kernelNamesToRun[i],
                                    kernelNameToParameters[kernelNamesToRun[i]].globalRange,
                                    kernelNameToParameters[kernelNamesToRun[i]].localRange);
                            }
                            else
                            {
                                (ib.buf as ICanCompute).compute(numberCruncher, i * 123456 + 1,
                                    kernelNamesToRun[i],
                                    kernelNameToParameters[kernelNamesToRun[i]].globalRange,
                                    kernelNameToParameters[kernelNamesToRun[i]].localRange);
                            }


                            if (debug)
                                Console.WriteLine("kernel complete: " + i);
                        }
                        if (enqueueMode)
                            numberCruncher.enqueueMode = false;
                    }
                    else
                    {
                        if (debug)
                            Console.WriteLine("no kernel names to run");
                    }
                }
                else
                {
                    // initializing stage
                    if (initializerKernelNames != null)
                    {
                        for (int i = 0; i < initializerKernelNames.Length; i++)
                        {
                            if (debug)
                            {
                                Console.WriteLine("running kernel: " + i);
                                Console.WriteLine("more than one parameter: " + moreThanOneParameter);
                            }


                            // normal run
                            if (moreThanOneParameter)
                            {

                                bufferParameters.compute(numberCruncher, i * 500 + 1,
                                    initializerKernelNames[i],
                                    initializerKernelGlobalRanges[i],
                                    initializerKernelLocalRanges[i]);
                            }
                            else
                            {
                                (ib.buf as ICanCompute).compute(numberCruncher, i * 12345678 + 1,
                                    initializerKernelNames[i],
                                    initializerKernelGlobalRanges[i],
                                    initializerKernelLocalRanges[i]);
                            }


                            if (debug)
                                Console.WriteLine("kernel complete: " + i);
                        }
                    }
                    else
                    {
                        if (debug)
                            Console.WriteLine("no kernel names to run");
                    }
                }
                timer.Stop();
                elapsedTime = timer.Elapsed.TotalMilliseconds;
                timer.Reset();
            }


            // double buffering for overlapped stages for multi device usage
            internal void switchInputBuffers()
            {
                for (int i = 0; i < inputBuffers.Length; i++)
                    inputBuffers[i].switchBuffers();
            }

            // double buffering for overlapped stages for multi device usage
            internal void switchOutputBuffers()
            {
                for (int i = 0; i < outputBuffers.Length; i++)
                    outputBuffers[i].switchBuffers();
            }

            // copy from output duplicates to input duplicates while real outputs and real inputs are computed concurrently
            // index is current index to check against zero or maxIndex
            // if it is zero and if data is given, gets data to its input (duplicate one)
            // if it is maxIndex and if result is given, gets output to result
            internal void forwardResults(int index, int maxIndex, object[] data, object[] result)
            {
                // has data to be pushed to duplicated input because real input is in use
                if ((index == 0) && (data != null))
                {
                    if (data.Length != inputBuffers.Length)
                    {
                        Console.WriteLine("error: inconsistent number of input arrays and data arrays.");
                        // to do: add error code whenever error happened. Then don't run pipeline if error code is not zero
                        return;
                    }

                    // to do: if there are enough threads, can make this a parallel.for loop
                    for (int i = 0; i < data.Length; i++)
                    {
                        var asArray = data[i] as Array;
                        if (asArray != null)
                        {
                            // given element is a C# array(of float,int,..byte,struct)
                            if (data[i].GetType() == typeof(float[]))
                            {
                                if (asArray.Length != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_FLOAT)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<float>;
                                destination.CopyFrom((float[])asArray, 0);
                            }
                            else if (data[i].GetType() == typeof(double[]))
                            {
                                if (asArray.Length != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_DOUBLE)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }
                                var destination = inputBuffers[i].switchedBuffer() as ClArray<double>;
                                destination.CopyFrom((double[])asArray, 0);
                            }
                            else if (data[i].GetType() == typeof(byte[]))
                            {
                                if (asArray.Length != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_BYTE)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }
                                var destination = inputBuffers[i].switchedBuffer() as ClArray<byte>;
                                destination.CopyFrom((byte[])asArray, 0);
                            }
                            else if (data[i].GetType() == typeof(char[]))
                            {
                                if (asArray.Length != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_CHAR)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }
                                var destination = inputBuffers[i].switchedBuffer() as ClArray<char>;
                                destination.CopyFrom((char[])asArray, 0);
                            }
                            else if (data[i].GetType() == typeof(int[]))
                            {
                                if (asArray.Length != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_INT)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }
                                var destination = inputBuffers[i].switchedBuffer() as ClArray<int>;
                                destination.CopyFrom((int[])asArray, 0);
                            }
                            else if (data[i].GetType() == typeof(uint[]))
                            {
                                if (asArray.Length != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_UINT)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }
                                var destination = inputBuffers[i].switchedBuffer() as ClArray<uint>;
                                destination.CopyFrom((uint[])asArray, 0);
                            }
                            else if (data[i].GetType() == typeof(long[]))
                            {
                                if (asArray.Length != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_LONG)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }
                                var destination = inputBuffers[i].switchedBuffer() as ClArray<long>;
                                destination.CopyFrom((long[])asArray, 0);
                            }
                            else
                            {
                                Console.WriteLine("error: array of structs for device-to-device pipeline not implemented yet.");
                                throw new NotImplementedException();
                            }




                        }

                        var asFastArray = data[i] as IMemoryHandle;
                        if (asFastArray != null)
                        {
                            if (data[i].GetType() == typeof(ClFloatArray))
                            {
                                if (asFastArray.Length != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_FLOAT)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<float>;
                                destination.CopyFrom((ClFloatArray)asFastArray, 0);
                            }
                            else if (data[i].GetType() == typeof(ClDoubleArray))
                            {
                                if (asFastArray.Length != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_DOUBLE)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<double>;
                                destination.CopyFrom((ClDoubleArray)asFastArray, 0);
                            }
                            else if (data[i].GetType() == typeof(ClByteArray))
                            {
                                if (asFastArray.Length != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_BYTE)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<byte>;
                                destination.CopyFrom((ClByteArray)asFastArray, 0);
                            }
                            else if (data[i].GetType() == typeof(ClCharArray))
                            {
                                if (asFastArray.Length != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_CHAR)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<char>;
                                destination.CopyFrom((ClCharArray)asFastArray, 0);
                            }
                            else if (data[i].GetType() == typeof(ClIntArray))
                            {
                                if (asFastArray.Length != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_INT)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<int>;
                                destination.CopyFrom((ClIntArray)asFastArray, 0);
                            }
                            else if (data[i].GetType() == typeof(ClUIntArray))
                            {
                                if (asFastArray.Length != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_UINT)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<uint>;
                                destination.CopyFrom((ClUIntArray)asFastArray, 0);
                            }
                            else if (data[i].GetType() == typeof(ClLongArray))
                            {
                                if (asFastArray.Length != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_LONG)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<long>;
                                destination.CopyFrom((ClLongArray)asFastArray, 0);
                            }

                        }

                        var asClArray = data[i] as IBufferOptimization;
                        if (asClArray != null)
                        {
                            if (data[i].GetType() == typeof(ClArray<float>))
                            {
                                if (asClArray.arrayLength != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_FLOAT)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<float>;
                                destination.CopyFrom((ClArray<float>)asClArray, 0);
                            }
                            else if (data[i].GetType() == typeof(ClArray<double>))
                            {
                                if (asClArray.arrayLength != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_DOUBLE)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<double>;
                                destination.CopyFrom((ClArray<double>)asClArray, 0);
                            }
                            else if (data[i].GetType() == typeof(ClArray<byte>))
                            {
                                if (asClArray.arrayLength != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_BYTE)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<byte>;
                                destination.CopyFrom((ClArray<byte>)asClArray, 0);
                            }
                            else if (data[i].GetType() == typeof(ClArray<char>))
                            {
                                if (asClArray.arrayLength != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_CHAR)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<char>;
                                destination.CopyFrom((ClArray<char>)asClArray, 0);
                            }
                            else if (data[i].GetType() == typeof(ClArray<int>))
                            {
                                if (asClArray.arrayLength != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_INT)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<int>;
                                destination.CopyFrom((ClArray<int>)asClArray, 0);
                            }
                            else if (data[i].GetType() == typeof(ClArray<uint>))
                            {
                                if (asClArray.arrayLength != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_UINT)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<uint>;
                                destination.CopyFrom((ClArray<uint>)asClArray, 0);
                            }
                            else if (data[i].GetType() == typeof(ClArray<long>))
                            {
                                if (asClArray.arrayLength != inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of input arrays and length of data arrays.");
                                    return;
                                }

                                if (inputBuffers[i].eType != ElementType.ELM_LONG)
                                {
                                    Console.WriteLine("error: inconsistent types of input and data arrays.");
                                    return;
                                }

                                var destination = inputBuffers[i].switchedBuffer() as ClArray<long>;
                                destination.CopyFrom((ClArray<long>)asClArray, 0);
                            }
                        }


                    }
                }

                // has result arrays to be received data from duplicated outputs because real output is in use
                if ((index == maxIndex) && (result != null))
                {
                    // to do: convert to output version from this input version
                    // *******************************************************************************************
                    // *******************************************************************************************
                    // *******************************************************************************************
                    if (result.Length != outputBuffers.Length)
                    {
                        Console.WriteLine("error: inconsistent number of output arrays and result arrays.");
                        // to do: add error code whenever error happened. Then don't run pipeline if error code is not zero
                        return;
                    }

                    // to do: if there are enough threads, can make this a parallel.for loop
                    for (int i = 0; i < result.Length; i++)
                    {
                        var asArray = result[i] as Array;
                        if (asArray != null)
                        {
                            // given element is a C# array(of float,int,..byte,struct)
                            if (result[i].GetType() == typeof(float[]))
                            {
                                if (asArray.Length != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_FLOAT)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<float>;
                                source.CopyTo((float[])asArray, 0);
                            }
                            else if (result[i].GetType() == typeof(double[]))
                            {
                                if (asArray.Length != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_DOUBLE)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }
                                var source = outputBuffers[i].switchedBuffer() as ClArray<double>;
                                source.CopyTo((double[])asArray, 0);
                            }
                            else if (result[i].GetType() == typeof(byte[]))
                            {
                                if (asArray.Length != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_BYTE)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }
                                var source = outputBuffers[i].switchedBuffer() as ClArray<byte>;
                                source.CopyTo((byte[])asArray, 0);
                            }
                            else if (result[i].GetType() == typeof(char[]))
                            {
                                if (asArray.Length != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_CHAR)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }
                                var source = outputBuffers[i].switchedBuffer() as ClArray<char>;
                                source.CopyTo((char[])asArray, 0);
                            }
                            else if (result[i].GetType() == typeof(int[]))
                            {
                                if (asArray.Length != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_INT)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }
                                var source = outputBuffers[i].switchedBuffer() as ClArray<int>;
                                source.CopyTo((int[])asArray, 0);
                            }
                            else if (result[i].GetType() == typeof(uint[]))
                            {
                                if (asArray.Length != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_UINT)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }
                                var source = outputBuffers[i].switchedBuffer() as ClArray<uint>;
                                source.CopyTo((uint[])asArray, 0);
                            }
                            else if (result[i].GetType() == typeof(long[]))
                            {
                                if (asArray.Length != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_LONG)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }
                                var source = outputBuffers[i].switchedBuffer() as ClArray<long>;
                                source.CopyTo((long[])asArray, 0);
                            }
                            else
                            {
                                Console.WriteLine("error: array of structs for device-to-device pipeline not implemented yet.");
                                throw new NotImplementedException();
                            }




                        }

                        var asFastArray = result[i] as IMemoryHandle;
                        if (asFastArray != null)
                        {
                            if (result[i].GetType() == typeof(ClFloatArray))
                            {
                                if (asFastArray.Length != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_FLOAT)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<float>;
                                source.CopyTo((ClFloatArray)asFastArray, 0);
                            }
                            else if (result[i].GetType() == typeof(ClDoubleArray))
                            {
                                if (asFastArray.Length != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_DOUBLE)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<double>;
                                source.CopyTo((ClDoubleArray)asFastArray, 0);
                            }
                            else if (result[i].GetType() == typeof(ClByteArray))
                            {
                                if (asFastArray.Length != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_BYTE)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<byte>;
                                source.CopyTo((ClByteArray)asFastArray, 0);
                            }
                            else if (result[i].GetType() == typeof(ClCharArray))
                            {
                                if (asFastArray.Length != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_CHAR)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<char>;
                                source.CopyTo((ClCharArray)asFastArray, 0);
                            }
                            else if (result[i].GetType() == typeof(ClIntArray))
                            {
                                if (asFastArray.Length != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_INT)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<int>;
                                source.CopyTo((ClIntArray)asFastArray, 0);
                            }
                            else if (result[i].GetType() == typeof(ClUIntArray))
                            {
                                if (asFastArray.Length != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_UINT)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<uint>;
                                source.CopyTo((ClUIntArray)asFastArray, 0);
                            }
                            else if (result[i].GetType() == typeof(ClLongArray))
                            {
                                if (asFastArray.Length != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_LONG)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<long>;
                                source.CopyTo((ClLongArray)asFastArray, 0);
                            }

                        }

                        var asClArray = result[i] as IBufferOptimization;
                        if (asClArray != null)
                        {
                            if (result[i].GetType() == typeof(ClArray<float>))
                            {
                                if (asClArray.arrayLength != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_FLOAT)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<float>;
                                source.CopyTo((ClArray<float>)asClArray, 0);
                            }
                            else if (result[i].GetType() == typeof(ClArray<double>))
                            {
                                if (asClArray.arrayLength != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_DOUBLE)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<double>;
                                source.CopyTo((ClArray<double>)asClArray, 0);
                            }
                            else if (result[i].GetType() == typeof(ClArray<byte>))
                            {
                                if (asClArray.arrayLength != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_BYTE)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<byte>;
                                source.CopyTo((ClArray<byte>)asClArray, 0);
                            }
                            else if (result[i].GetType() == typeof(ClArray<char>))
                            {
                                if (asClArray.arrayLength != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_CHAR)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<char>;
                                source.CopyTo((ClArray<char>)asClArray, 0);
                            }
                            else if (result[i].GetType() == typeof(ClArray<int>))
                            {
                                if (asClArray.arrayLength != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_INT)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<int>;
                                source.CopyTo((ClArray<int>)asClArray, 0);
                            }
                            else if (result[i].GetType() == typeof(ClArray<uint>))
                            {
                                if (asClArray.arrayLength != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_UINT)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<uint>;
                                source.CopyTo((ClArray<uint>)asClArray, 0);
                            }
                            else if (result[i].GetType() == typeof(ClArray<long>))
                            {
                                if (asClArray.arrayLength != outputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: inconsistent length of output arrays and length of result arrays.");
                                    return;
                                }

                                if (outputBuffers[i].eType != ElementType.ELM_LONG)
                                {
                                    Console.WriteLine("error: inconsistent types of output and result arrays.");
                                    return;
                                }

                                var source = outputBuffers[i].switchedBuffer() as ClArray<long>;
                                source.CopyTo((ClArray<long>)asClArray, 0);
                            }
                        }


                    }
                    // *********************************************************************************************
                    // *********************************************************************************************
                    // *********************************************************************************************
                }

                // to do: complete this method
                if ((nextStages != null) && (nextStages.Length > 0))
                {

                    for (int i = 0; i < outputBuffers.Length; i++)
                    {
                        if (outputBuffers[i].eType == ElementType.ELM_FLOAT)
                        {
                            var source = outputBuffers[i].bufDuplicate as ClArray<float>;
                            // to do: if number of free threads greater than nextStages length, use parallel.for loop
                            for (int j = 0; j < nextStages.Length; j++)
                            {
                                if (source.GetType() != nextStages[j].inputBuffers[i].switchedBuffer().GetType())
                                {
                                    Console.WriteLine("error: output - input buffer type mismatch");
                                    return;
                                }

                                if (source.Length != nextStages[j].inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: output - input buffer length mismatch");
                                    return;
                                }
                                source.CopyTo((ClArray<float>)nextStages[j].inputBuffers[i].bufDuplicate, 0);
                            }
                        }

                        if (outputBuffers[i].eType == ElementType.ELM_DOUBLE)
                        {
                            var source = outputBuffers[i].bufDuplicate as ClArray<double>;
                            // to do: if number of free threads greater than nextStages length, use parallel.for loop
                            for (int j = 0; j < nextStages.Length; j++)
                            {
                                if (source.GetType() != nextStages[j].inputBuffers[i].switchedBuffer().GetType())
                                {
                                    Console.WriteLine("error: output - input buffer type mismatch");
                                    return;
                                }

                                if (source.Length != nextStages[j].inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: output - input buffer length mismatch");
                                    return;
                                }
                                source.CopyTo((ClArray<double>)nextStages[j].inputBuffers[i].bufDuplicate, 0);
                            }
                        }

                        if (outputBuffers[i].eType == ElementType.ELM_BYTE)
                        {
                            var source = outputBuffers[i].bufDuplicate as ClArray<byte>;
                            // to do: if number of free threads greater than nextStages length, use parallel.for loop
                            for (int j = 0; j < nextStages.Length; j++)
                            {
                                if (source.GetType() != nextStages[j].inputBuffers[i].switchedBuffer().GetType())
                                {
                                    Console.WriteLine("error: output - input buffer type mismatch");
                                    return;
                                }

                                if (source.Length != nextStages[j].inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: output - input buffer length mismatch");
                                    return;
                                }
                                source.CopyTo((ClArray<byte>)nextStages[j].inputBuffers[i].bufDuplicate, 0);
                            }
                        }

                        if (outputBuffers[i].eType == ElementType.ELM_CHAR)
                        {
                            var source = outputBuffers[i].bufDuplicate as ClArray<char>;
                            // to do: if number of free threads greater than nextStages length, use parallel.for loop
                            for (int j = 0; j < nextStages.Length; j++)
                            {
                                if (source.GetType() != nextStages[j].inputBuffers[i].switchedBuffer().GetType())
                                {
                                    Console.WriteLine("error: output - input buffer type mismatch");
                                    return;
                                }

                                if (source.Length != nextStages[j].inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: output - input buffer length mismatch");
                                    return;
                                }
                                source.CopyTo((ClArray<char>)nextStages[j].inputBuffers[i].bufDuplicate, 0);
                            }
                        }

                        if (outputBuffers[i].eType == ElementType.ELM_INT)
                        {
                            var source = outputBuffers[i].bufDuplicate as ClArray<int>;
                            // to do: if number of free threads greater than nextStages length, use parallel.for loop
                            for (int j = 0; j < nextStages.Length; j++)
                            {
                                if (source.GetType() != nextStages[j].inputBuffers[i].switchedBuffer().GetType())
                                {
                                    Console.WriteLine("error: output - input buffer type mismatch");
                                    return;
                                }

                                if (source.Length != nextStages[j].inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: output - input buffer length mismatch");
                                    return;
                                }
                                source.CopyTo((ClArray<int>)nextStages[j].inputBuffers[i].bufDuplicate, 0);
                            }
                        }

                        if (outputBuffers[i].eType == ElementType.ELM_UINT)
                        {
                            var source = outputBuffers[i].bufDuplicate as ClArray<uint>;
                            // to do: if number of free threads greater than nextStages length, use parallel.for loop
                            for (int j = 0; j < nextStages.Length; j++)
                            {
                                if (source.GetType() != nextStages[j].inputBuffers[i].switchedBuffer().GetType())
                                {
                                    Console.WriteLine("error: output - input buffer type mismatch");
                                    return;
                                }

                                if (source.Length != nextStages[j].inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: output - input buffer length mismatch");
                                    return;
                                }
                                source.CopyTo((ClArray<uint>)nextStages[j].inputBuffers[i].bufDuplicate, 0);
                            }
                        }

                        if (outputBuffers[i].eType == ElementType.ELM_LONG)
                        {
                            var source = outputBuffers[i].bufDuplicate as ClArray<long>;
                            // to do: if number of free threads greater than nextStages length, use parallel.for loop
                            for (int j = 0; j < nextStages.Length; j++)
                            {
                                if (source.GetType() != nextStages[j].inputBuffers[i].switchedBuffer().GetType())
                                {
                                    Console.WriteLine("error: output - input buffer type mismatch");
                                    return;
                                }

                                if (source.Length != nextStages[j].inputBuffers[i].bufDuplicate.arrayLength)
                                {
                                    Console.WriteLine("error: output - input buffer length mismatch");
                                    return;
                                }
                                source.CopyTo((ClArray<long>)nextStages[j].inputBuffers[i].bufDuplicate, 0);
                            }
                        }
                    }
                }
            }

            /// <summary>
            /// <para>creates a pipeline out of all bound stages, ready to compute, currently only 1 stage per layer (as parallel)</para>
            /// <para>executes the initializer kernels of each stage once</para>
            /// </summary>
            /// <returns></returns>
            public ClPipeline makePipeline()
            {
                if (debug)
                    Console.WriteLine("Creating a pipeline from a group of stages");
                // find starting stages with tracking back
                ClPipelineStage currentStage = findInputStages();
                int numberOfLayers = currentStage.findOutputStagesCount(1);
                int currentOrder = 0;
                if (debug)
                    Console.WriteLine("Number of stages=" + numberOfLayers);

                ClPipelineStage[][] pipelineStages = new ClPipelineStage[numberOfLayers][];
                // enumerate orders and add all stages as array elements for pipeline

                for (int i = 0; i < numberOfLayers; i++)
                {
                    // currently supports only linear-horizontal bound stages, only 1 stage per layer, no parallel stages (yet)
                    currentStage.stageOrder = i;
                    pipelineStages[i] = new ClPipelineStage[1];
                    pipelineStages[i][0] = currentStage;
                    if (i < numberOfLayers - 1)
                        currentStage = currentStage.nextStages[0];
                }

                ClPipeline pipeline = new ClPipeline(debug);
                pipeline.stages = pipelineStages;
                // initialize stage buffers

                for (int i = 0; i < pipeline.stages.Length; i++)
                {
                    for (int j = 0; j < pipeline.stages[i].Length; j++)
                    {
                        pipeline.stages[i][j].run(true); // initialize normal buffers
                        pipeline.stages[i][j].switchInputBuffers();
                        pipeline.stages[i][j].switchOutputBuffers();
                        pipeline.stages[i][j].run(true); // initialize duplicate buffers
                        pipeline.stages[i][j].switchInputBuffers();
                        pipeline.stages[i][j].switchOutputBuffers();
                    }
                }
                return pipeline;
            }

            /// <summary>
            /// finds all stages and puts them in layers to be computed in parallel
            /// </summary>
            /// <param name="root"></param>
            /// <returns></returns>
            internal ClPipelineStage findInputStages()
            {
                if (previousStage != null)
                {
                    return previousStage.findInputStages();
                }
                else
                    return this;
            }

            /// <summary>
            /// finds total number of horizontally bound stages (also number of steps before output has data)
            /// </summary>
            /// <returns></returns>
            internal int findOutputStagesCount(int startValue)
            {
                ClPipelineStage currentStage = this;
                if (currentStage.nextStages != null)
                {
                    if (currentStage.nextStages.Length > 0)
                    {
                        int[] valuesToCompare = new int[currentStage.nextStages.Length];
                        for (int i = 0; i < currentStage.nextStages.Length; i++)
                        {
                            valuesToCompare[i] = currentStage.nextStages[i].findOutputStagesCount(startValue);
                        }
                        Array.Sort(valuesToCompare);
                        return startValue + valuesToCompare[0];
                    }
                    else
                        return startValue;
                }
                else
                    return startValue;
            }

            internal int stageOrder { get; set; }

            internal string[] initializerKernelNames { get; set; }
            internal int[] initializerKernelGlobalRanges { get; set; }
            internal int[] initializerKernelLocalRanges { get; set; }



            /// <summary>
            /// <para>kernel function name to run once before beginning, empty string = no initializing needed</para>
            /// <para></para>
            /// </summary>
            public void initializerKernel(string initKernelNames, int[] globalRanges, int[] localRanges)
            {
                if (initKernelNames != null)
                    initializerKernelNames = initKernelNames.Split(new string[] { " ", ",", ";", "-", Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                if ((initKernelNames == null) || (globalRanges == null) || (localRanges == null) ||
                    (initializerKernelNames.Length != globalRanges.Length) || (initializerKernelNames.Length != localRanges.Length) || (localRanges.Length != globalRanges.Length))
                {
                    initializerKernelNames = null;
                    Console.WriteLine("Warning: Number of initializer kernels and number of range values do not match or one of them is null. Initializer kernels will not run.");
                    return;
                }


                initializerKernelGlobalRanges = new int[globalRanges.Length];
                initializerKernelLocalRanges = new int[localRanges.Length];

                for (int i = 0; i < initializerKernelGlobalRanges.Length; i++)
                {
                    initializerKernelGlobalRanges[i] = globalRanges[i];
                    initializerKernelLocalRanges[i] = localRanges[i];
                }
            }

            /// <summary>
            /// binds this stage to target stage's entrance
            /// </summary>
            public void prependToStage(ClPipelineStage stage)
            {
                // this stage
                nextStagesList.Add(stage);
                nextStages = nextStagesList.ToArray();

                // next stage
                stage.previousStage = this;
            }

            /// <summary>
            /// binds this stage to target stage's exit
            /// </summary>
            public void appendToStage(ClPipelineStage stage)
            {
                // this stage
                previousStage = stage;

                // previous stage
                previousStage.nextStagesList.Add(this);
                previousStage.nextStages = previousStage.nextStagesList.ToArray();
            }

            /// <summary>
            /// <para>setup devices that will compute this stage(as parallel to speed-up this stage only, duplicated devices allowed too)</para>
            /// <para>copies device object</para>
            /// </summary>
            public void addDevices(ClDevices devicesParameter)
            {
                devices = devicesParameter.copyExact();
            }

            /// <summary>
            /// setup kernels to be used by this stage
            /// 
            /// </summary>
            /// <param name="kernels">string containing auxiliary functions, kernel functions and constants</param>
            /// <param name="kernelNames">names of kernels to be used(in the order they run)</param>
            /// <param name="globalRanges">total workitems per kernel name in kernelNames parameter</param>
            /// <param name="localRanges">workgroup workitems per kernel name in kernelNames parameter</param>
            public void addKernels(string kernels, string kernelNames, int[] globalRanges, int[] localRanges)
            {
                kernelsToCompile = new StringBuilder(kernels).ToString();
                kernelNamesToRun = kernelNames.Split(new string[] { " ", ",", ";", "-", Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                if (debug)
                {
                    Console.WriteLine(kernelNamesToRun != null ? (kernelNamesToRun.Length > 0 ? kernelNamesToRun[0] : ("kernel name error: " + kernelNames)) : ("kernel name error: " + kernelNames));
                    if (globalRanges.Length < kernelNamesToRun.Length)
                        Console.WriteLine("number of global ranges is not equal to kernel names listed in \"kernels\" parameter ");

                    if (localRanges.Length < kernelNamesToRun.Length)
                        Console.WriteLine("number of local ranges is not equal to kernel names listed in \"kernels\" parameter ");
                }

                for (int i = 0; i < kernelNamesToRun.Length; i++)
                {
                    if (kernelNameToParameters.ContainsKey(kernelNamesToRun[i]))
                    {

                    }
                    else
                    {
                        KernelParameters kernelParameters = new KernelParameters();
                        kernelParameters.globalRange = globalRanges[i];
                        kernelParameters.localRange = localRanges[i];
                        kernelNameToParameters.Add(kernelNamesToRun[i], kernelParameters);
                    }
                }
            }

            /// <summary>
            /// not used for input or output, just for keeping sequential logic states (such as coordinates of particles)
            /// </summary>
            public void addHiddenBuffers(params Array[] hiddensParameter)
            {
                for (int i = 0; i < hiddensParameter.Length; i++)
                    hiddenBuffersList.Add(new ClPipelineStageBuffer(hiddensParameter[i]));

                hiddenBuffers = hiddenBuffersList.ToArray();
            }

            /// <summary>
            /// not used for input or output, just for keeping sequential logic states (such as coordinates of particles)
            /// </summary>
            public void addHiddenBuffers(params IBufferOptimization[] hiddensParameter)
            {
                for (int i = 0; i < hiddensParameter.Length; i++)
                    hiddenBuffersList.Add(new ClPipelineStageBuffer(hiddensParameter[i]));

                hiddenBuffers = hiddenBuffersList.ToArray();
            }

            /// <summary>
            /// not used for input or output, just for keeping sequential logic states (such as coordinates of particles)
            /// </summary>
            public void addHiddenBuffers(params IMemoryHandle[] hiddensParameter)
            {
                for (int i = 0; i < hiddensParameter.Length; i++)
                    hiddenBuffersList.Add(new ClPipelineStageBuffer(hiddensParameter[i]));

                hiddenBuffers = hiddenBuffersList.ToArray();
            }

            /// <summary>
            /// input arrays (ClArray, ClByteArray, byte[], ... ) to be pushed by user or to be connect to another stage's output
            /// </summary>
            public void addInputBuffers(params Array[] inputsParameter)
            {
                for (int i = 0; i < inputsParameter.Length; i++)
                    inputBuffersList.Add(new ClPipelineStageBuffer(inputsParameter[i]));

                inputBuffers = inputBuffersList.ToArray();
            }

            /// <summary>
            /// input arrays (ClArray, ClByteArray, byte[], ... ) to be pushed by user or to be connect to another stage's output
            /// </summary>
            public void addInputBuffers(params IBufferOptimization[] inputsParameter)
            {
                for (int i = 0; i < inputsParameter.Length; i++)
                    inputBuffersList.Add(new ClPipelineStageBuffer(inputsParameter[i]));

                inputBuffers = inputBuffersList.ToArray();
            }

            /// <summary>
            /// input arrays (ClArray, ClByteArray, byte[], ... ) to be pushed by user or to be connect to another stage's output
            /// </summary>
            public void addInputBuffers(params IMemoryHandle[] inputsParameter)
            {
                for (int i = 0; i < inputsParameter.Length; i++)
                    inputBuffersList.Add(new ClPipelineStageBuffer(inputsParameter[i]));

                inputBuffers = inputBuffersList.ToArray();
            }



            /// <summary>
            /// output arrays (ClArray, ClByteArray, byte[], ... ) to be popped to user or to be connected another stage's input
            /// </summary>
            public void addOutputBuffers(params Array[] outputsParameter)
            {
                for (int i = 0; i < outputsParameter.Length; i++)
                    outputBuffersList.Add(new ClPipelineStageBuffer(outputsParameter[i]));

                outputBuffers = outputBuffersList.ToArray();
            }

            /// <summary>
            /// output arrays (ClArray, ClByteArray, byte[], ... ) to be popped to user or to be connected another stage's input
            /// </summary>
            public void addOutputBuffers(params IMemoryHandle[] outputsParameter)
            {
                for (int i = 0; i < outputsParameter.Length; i++)
                    outputBuffersList.Add(new ClPipelineStageBuffer(outputsParameter[i]));

                outputBuffers = outputBuffersList.ToArray();
            }

            /// <summary>
            /// output arrays (ClArray, ClByteArray, byte[], ... ) to be popped to user or to be connected another stage's input
            /// </summary>
            public void addOutputBuffers(params IBufferOptimization[] outputsParameter)
            {
                for (int i = 0; i < outputsParameter.Length; i++)
                    outputBuffersList.Add(new ClPipelineStageBuffer(outputsParameter[i]));

                outputBuffers = outputBuffersList.ToArray();
            }
        }



        internal enum ElementType : int
        {
            ELM_FLOAT = 0, ELM_DOUBLE = 1, ELM_BYTE = 2, ELM_CHAR = 3, ELM_INT = 4, ELM_LONG = 5, ELM_UINT = 6, ELM_STRUCT = 7,
        }

        /// <summary>
        /// Wraps Array, FastArr, ClArray types so that it can be switched by its duplicate, read, write, ...
        /// </summary>
        internal class ClPipelineStageBuffer
        {
            // buffers are always wrapped as ClArray
            internal ElementType eType;
            private ClArray<byte> bufByte;
            private ClArray<byte> bufByteDuplicate;
            private ClArray<char> bufChar;
            private ClArray<char> bufCharDuplicate;
            private ClArray<int> bufInt;
            private ClArray<int> bufIntDuplicate;
            private ClArray<long> bufLong;
            private ClArray<long> bufLongDuplicate;
            private ClArray<float> bufFloat;
            private ClArray<float> bufFloatDuplicate;
            private ClArray<double> bufDouble;
            private ClArray<double> bufDoubleDuplicate;
            private ClArray<uint> bufUInt;
            private ClArray<uint> bufUIntDuplicate;
            internal IBufferOptimization buf;
            internal IBufferOptimization buf0;
            internal IBufferOptimization bufDuplicate;
            /// <summary>
            /// p: buffer to duplicate and double buffered in pipeline stages
            /// </summary>
            /// <param name="p"></param>
            /// <param name="duplicate"></param>
            public ClPipelineStageBuffer(object p,bool duplicate=true)
            {
                var bufAsArray = p as Array;
                if (bufAsArray != null)
                {
                    if (p.GetType() == typeof(float[]))
                    {
                        eType = ElementType.ELM_FLOAT;
                        bufFloat = (float[])p;
                        bufFloatDuplicate = new ClArray<float>(bufFloat.Length, bufFloat.alignmentBytes);
                        buf = bufFloat as IBufferOptimization;
                        bufDuplicate = bufFloatDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(double[]))
                    {
                        eType = ElementType.ELM_DOUBLE;
                        bufDouble = (double[])p;
                        bufDoubleDuplicate = new ClArray<double>(bufDouble.Length, bufDouble.alignmentBytes);
                        buf = bufDouble as IBufferOptimization;
                        bufDuplicate = bufDoubleDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(byte[]))
                    {
                        eType = ElementType.ELM_BYTE;
                        bufByte = (byte[])p;
                        bufByteDuplicate = new ClArray<byte>(bufByte.Length, bufByte.alignmentBytes);
                        buf = bufByte as IBufferOptimization;
                        bufDuplicate = bufByteDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(char[]))
                    {
                        eType = ElementType.ELM_CHAR;
                        bufChar = (char[])p;
                        bufCharDuplicate = new ClArray<char>(bufChar.Length, bufChar.alignmentBytes);
                        buf = bufChar as IBufferOptimization;
                        bufDuplicate = bufCharDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(int[]))
                    {
                        eType = ElementType.ELM_INT;
                        bufInt = (int[])p;
                        bufIntDuplicate = new ClArray<int>(bufInt.Length, bufInt.alignmentBytes);
                        buf = bufInt as IBufferOptimization;
                        bufDuplicate = bufIntDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(uint[]))
                    {
                        eType = ElementType.ELM_UINT;
                        bufUInt = (uint[])p;
                        bufUIntDuplicate = new ClArray<uint>(bufUInt.Length, bufUInt.alignmentBytes);
                        buf = bufUInt as IBufferOptimization;
                        bufDuplicate = bufUIntDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(long[]))
                    {
                        eType = ElementType.ELM_LONG;
                        bufLong = (long[])p;
                        bufLongDuplicate = new ClArray<long>(bufLong.Length, bufLong.alignmentBytes);
                        buf = bufLong as IBufferOptimization;
                        bufDuplicate = bufLongDuplicate as IBufferOptimization;
                    }
                    else
                    {
                        // then it has to be a struct array
                        eType = ElementType.ELM_BYTE;
                        bufByte = ClArray<byte>.wrapArrayOfStructs(p);
                        bufByteDuplicate = new ClArray<byte>(bufByte.Length, bufByte.alignmentBytes);
                        bufByteDuplicate.numberOfElementsPerWorkItem = bufByte.numberOfElementsPerWorkItem;
                        buf = bufByte as IBufferOptimization;
                        bufDuplicate = bufByteDuplicate as IBufferOptimization;
                    }
                }
                var bufAsFastArr = p as IMemoryHandle;
                if (bufAsFastArr != null)
                {
                    if (p.GetType() == typeof(ClByteArray))
                    {
                        eType = ElementType.ELM_BYTE;
                        bufByte = (ClByteArray)p;
                        bufByteDuplicate = new ClArray<byte>(bufByte.Length, bufByte.alignmentBytes > 0 ? bufByte.alignmentBytes : 4096);
                        buf = bufByte as IBufferOptimization;
                        bufDuplicate = bufByteDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(ClCharArray))
                    {
                        eType = ElementType.ELM_CHAR;
                        bufChar = (ClCharArray)p;
                        bufCharDuplicate = new ClArray<char>(bufChar.Length, bufChar.alignmentBytes > 0 ? bufChar.alignmentBytes : 4096);
                        buf = bufChar as IBufferOptimization;
                        bufDuplicate = bufCharDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(ClIntArray))
                    {
                        eType = ElementType.ELM_INT;
                        bufInt = (ClIntArray)p;
                        bufIntDuplicate = new ClArray<int>(bufInt.Length, bufInt.alignmentBytes > 0 ? bufInt.alignmentBytes : 4096);
                        buf = bufInt as IBufferOptimization;
                        bufDuplicate = bufIntDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(ClUIntArray))
                    {
                        eType = ElementType.ELM_UINT;
                        bufUInt = (ClUIntArray)p;
                        bufUIntDuplicate = new ClArray<uint>(bufUInt.Length, bufUInt.alignmentBytes > 0 ? bufUInt.alignmentBytes : 4096);
                        buf = bufUInt as IBufferOptimization;
                        bufDuplicate = bufUIntDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(ClLongArray))
                    {
                        eType = ElementType.ELM_LONG;
                        bufLong = (ClLongArray)p;
                        bufLongDuplicate = new ClArray<long>(bufLong.Length, bufLong.alignmentBytes > 0 ? bufLong.alignmentBytes : 4096);
                        buf = bufLong as IBufferOptimization;
                        bufDuplicate = bufLongDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(ClFloatArray))
                    {
                        eType = ElementType.ELM_FLOAT;
                        bufFloat = (ClFloatArray)p;
                        bufFloatDuplicate = new ClArray<float>(bufFloat.Length, bufFloat.alignmentBytes > 0 ? bufFloat.alignmentBytes : 4096);
                        buf = bufFloat as IBufferOptimization;
                        bufDuplicate = bufFloatDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(ClDoubleArray))
                    {
                        eType = ElementType.ELM_DOUBLE;
                        bufDouble = (ClDoubleArray)p;
                        bufDoubleDuplicate = new ClArray<double>(bufDouble.Length, bufDouble.alignmentBytes > 0 ? bufDouble.alignmentBytes : 4096);
                        buf = bufDouble as IBufferOptimization;
                        bufDuplicate = bufDoubleDuplicate as IBufferOptimization;
                    }
                }
                var bufAsClArray = p as IBufferOptimization;
                if (bufAsClArray != null)
                {
                    if (p.GetType() == typeof(ClArray<byte>))
                    {
                        eType = ElementType.ELM_BYTE;
                        bufByte = (ClArray<byte>)p;
                        bufByteDuplicate = new ClArray<byte>(bufByte.Length, bufByte.alignmentBytes > 0 ? bufByte.alignmentBytes : 4096);
                        bufByteDuplicate.numberOfElementsPerWorkItem = bufByte.numberOfElementsPerWorkItem;
                        buf = bufByte as IBufferOptimization;
                        bufDuplicate = bufByteDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(ClArray<char>))
                    {
                        eType = ElementType.ELM_CHAR;
                        bufChar = (ClArray<char>)p;
                        bufCharDuplicate = new ClArray<char>(bufChar.Length, bufChar.alignmentBytes > 0 ? bufChar.alignmentBytes : 4096);
                        bufCharDuplicate.numberOfElementsPerWorkItem = bufChar.numberOfElementsPerWorkItem;
                        buf = bufChar as IBufferOptimization;
                        bufDuplicate = bufCharDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(ClArray<int>))
                    {
                        eType = ElementType.ELM_INT;
                        bufInt = (ClArray<int>)p;
                        bufIntDuplicate = new ClArray<int>(bufInt.Length, bufInt.alignmentBytes > 0 ? bufInt.alignmentBytes : 4096);
                        bufIntDuplicate.numberOfElementsPerWorkItem = bufInt.numberOfElementsPerWorkItem;
                        buf = bufInt as IBufferOptimization;
                        bufDuplicate = bufIntDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(ClArray<uint>))
                    {
                        eType = ElementType.ELM_UINT;
                        bufUInt = (ClArray<uint>)p;
                        bufUIntDuplicate = new ClArray<uint>(bufUInt.Length, bufUInt.alignmentBytes > 0 ? bufUInt.alignmentBytes : 4096);
                        bufUIntDuplicate.numberOfElementsPerWorkItem = bufUInt.numberOfElementsPerWorkItem;
                        buf = bufUInt as IBufferOptimization;
                        bufDuplicate = bufUIntDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(ClArray<long>))
                    {
                        eType = ElementType.ELM_LONG;
                        bufLong = (ClArray<long>)p;
                        bufLongDuplicate = new ClArray<long>(bufLong.Length, bufLong.alignmentBytes > 0 ? bufLong.alignmentBytes : 4096);
                        bufLongDuplicate.numberOfElementsPerWorkItem = bufLong.numberOfElementsPerWorkItem;
                        buf = bufLong as IBufferOptimization;
                        bufDuplicate = bufLongDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(ClArray<float>))
                    {
                        eType = ElementType.ELM_FLOAT;
                        bufFloat = (ClArray<float>)p;
                        bufFloatDuplicate = new ClArray<float>(bufFloat.Length, bufFloat.alignmentBytes > 0 ? bufFloat.alignmentBytes : 4096);
                        bufFloatDuplicate.numberOfElementsPerWorkItem = bufFloat.numberOfElementsPerWorkItem;
                        buf = bufFloat as IBufferOptimization;
                        bufDuplicate = bufFloatDuplicate as IBufferOptimization;
                    }
                    else if (p.GetType() == typeof(ClArray<double>))
                    {
                        eType = ElementType.ELM_DOUBLE;
                        bufDouble = (ClArray<double>)p;
                        bufDoubleDuplicate = new ClArray<double>(bufDouble.Length, bufDouble.alignmentBytes > 0 ? bufDouble.alignmentBytes : 4096);
                        bufDoubleDuplicate.numberOfElementsPerWorkItem = bufDouble.numberOfElementsPerWorkItem;
                        buf = bufDouble as IBufferOptimization;
                        bufDuplicate = bufDoubleDuplicate as IBufferOptimization;
                    }
                }

                // to do: optimize this to not create unused buffers in first place
                if(!duplicate)
                {
                    bufByteDuplicate = null;
                    bufCharDuplicate = null;
                    bufIntDuplicate = null;
                    bufUIntDuplicate = null;
                    bufLongDuplicate = null;
                    bufFloatDuplicate = null;
                    bufDoubleDuplicate = null;
                    bufDuplicate = null;
                }
                buf0 = buf;
            }

            internal void enableInput()
            {
                if(bufDuplicate!=null)
                {
                    bufDuplicate.read = true;
                    bufDuplicate.partialRead = false;
                }
            }

            internal void disableInput()
            {
                if (bufDuplicate != null)
                {
                    bufDuplicate.read = false;
                    bufDuplicate.partialRead = false;
                }
            }

            internal void enableOutput()
            {
                if (bufDuplicate != null)
                {
                    bufDuplicate.writeAll = true;
                    bufDuplicate.write = true;
                }
            }

            internal void disableOutput()
            {
                if (bufDuplicate != null)
                {
                    bufDuplicate.writeAll = false;
                    bufDuplicate.write = false;
                }
            }

            private int switchCounter = 0;

            internal int debugSwitchCount()
            {
                if (buf == buf0)
                    return 0;
                else
                    return 1;
                return switchCounter;
            }

            /// <summary>
            /// double buffering for pipelining(overlap pci-e reads and writes on all pci-e bridges)
            /// </summary>
            internal void switchBuffers()
            {
                {
                    switchCounter++;
                    object tmp = bufByte;
                    bufByte = bufByteDuplicate;
                    bufByteDuplicate = (ClArray<byte>)tmp;
                    tmp = bufChar;
                    bufChar = bufCharDuplicate;
                    bufCharDuplicate = (ClArray<char>)tmp;
                    tmp = bufInt;
                    bufInt = bufIntDuplicate;
                    bufIntDuplicate = (ClArray<int>)tmp;
                    tmp = bufUInt;
                    bufUInt = bufUIntDuplicate;
                    bufUIntDuplicate = (ClArray<uint>)tmp;
                    tmp = bufLong;
                    bufLong = bufLongDuplicate;
                    bufLongDuplicate = (ClArray<long>)tmp;
                    tmp = bufFloat;
                    bufFloat = bufFloatDuplicate;
                    bufFloatDuplicate = (ClArray<float>)tmp;
                    tmp = bufDouble;
                    bufDouble = bufDoubleDuplicate;
                    bufDoubleDuplicate = (ClArray<double>)tmp;
                    tmp = buf;
                    buf = bufDuplicate;
                    bufDuplicate = (IBufferOptimization)tmp;
                }
            }

            public int numberOfElementsPerWorkItem
            {
                get
                {
                    return buf.numberOfElementsPerWorkItem;
                }

                set
                {
                    buf.numberOfElementsPerWorkItem = value;
                    bufDuplicate.numberOfElementsPerWorkItem = value;
                }
            }

            internal ClParameterGroup nextParam(params IBufferOptimization[] bufs)
            {
                if (bufByte != null)
                    return bufByte.nextParam(bufs);
                else if (bufChar != null)
                    return bufChar.nextParam(bufs);
                else if (bufInt != null)
                    return bufInt.nextParam(bufs);
                else if (bufUInt != null)
                    return bufUInt.nextParam(bufs);
                else if (bufLong != null)
                    return bufLong.nextParam(bufs);
                else if (bufFloat != null)
                    return bufFloat.nextParam(bufs);
                else if (bufDouble != null)
                    return bufDouble.nextParam(bufs);
                else
                    return null;
            }

            internal ClParameterGroup nextParamDuplicate(params IBufferOptimization[] bufs)
            {
                if (bufByteDuplicate != null)
                    return bufByteDuplicate.nextParam(bufs);
                else if (bufCharDuplicate != null)
                    return bufCharDuplicate.nextParam(bufs);
                else if (bufIntDuplicate != null)
                    return bufIntDuplicate.nextParam(bufs);
                else if (bufUIntDuplicate != null)
                    return bufUIntDuplicate.nextParam(bufs);
                else if (bufLongDuplicate != null)
                    return bufLongDuplicate.nextParam(bufs);
                else if (bufFloatDuplicate != null)
                    return bufFloatDuplicate.nextParam(bufs);
                else if (bufDoubleDuplicate != null)
                    return bufDoubleDuplicate.nextParam(bufs);
                else
                    return null;
            }

            public bool read
            {
                get
                {
                    return buf.read;
                }

                set
                {
                    buf.read = value;
                    bufDuplicate.read = value;
                }
            }

            public bool partialRead
            {
                get
                {
                    return buf.partialRead;
                }

                set
                {
                    buf.partialRead = value;
                    bufDuplicate.partialRead = value;
                }
            }

            public bool write
            {
                get
                {
                    return buf.write;
                }

                set
                {
                    buf.write = value;
                    bufDuplicate.write = value;
                }
            }

            public object buffer()
            {
                if (bufByte != null)
                    return bufByte;
                else if (bufChar != null)
                    return bufChar;
                else if (bufInt != null)
                    return bufInt;
                else if (bufUInt != null)
                    return bufUInt;
                else if (bufLong != null)
                    return bufLong;
                else if (bufFloat != null)
                    return bufFloat;
                else if (bufDouble != null)
                    return bufDouble;
                else
                    return null;
            }



            public object switchedBuffer()
            {
                if (bufByteDuplicate != null)
                    return bufByteDuplicate;
                else if (bufCharDuplicate != null)
                    return bufCharDuplicate;
                else if (bufIntDuplicate != null)
                    return bufIntDuplicate;
                else if (bufUIntDuplicate != null)
                    return bufUIntDuplicate;
                else if (bufLongDuplicate != null)
                    return bufLongDuplicate;
                else if (bufFloatDuplicate != null)
                    return bufFloatDuplicate;
                else if (bufDoubleDuplicate != null)
                    return bufDoubleDuplicate;
                else
                    return null;
            }

        }

        /// <summary>
        /// <para>Not Implemented Yet</para>
        /// <para>for running more kernels concurrently in same GPU</para>
        /// <para>N command queues for N stages in same context and for single GPU</para>
        /// <para>ovarlapping a stage is optional. non overlapped(serialized) stages will live in same command queue</para>
        /// <para>to extort more performance out of a single gpu</para>
        /// <para>first run will work serialized to prepare non-concurrent dictionaries, next runs will work concurrent</para>
        /// <para>uses kernel name separation(not implemented yet) a##b##c##d to run a b c d concurrently, sets(after switching) all kernel arguments before that</para>
        /// </summary>
        namespace SingleGPUPipeline
        {

            /// <summary>
            /// N staged pipeline.
            /// </summary>
            public class DevicePipeline
            {

                private bool serialMode { get; set; }
                private List<DevicePipelineStage> stages { get; set; }
                private string kernelCodesToCompile { get; set; }
                private ClNumberCruncher cruncher { get; set; }
                private ClDevices singleDevice { get; set; }
                private int currentComputeQueueConcurrency{get;set;}
                /// <summary>
                /// N stages pipeline defined in a selected device
                /// </summary>
                /// <param name="selectedDevice">this can be a CPU, GPU, ...</param>
                /// <param name="kernelCodesC99">kernel string to be compiled for all stages</param>
                /// <param name="computeQueueConcurrency">max number of command queues to use asynchronously. max=16, min=1</param>
                public DevicePipeline(ClDevices selectedDevice,string kernelCodesC99,int computeQueueConcurrency=16)
                {
                    currentComputeQueueConcurrency = computeQueueConcurrency;
                    singleDevice = selectedDevice[0];
                    stages = new List<DevicePipelineStage>();
                    kernelCodesToCompile = new StringBuilder(kernelCodesC99).ToString();
                    cruncher = null;
                }

                /// <summary>
                /// <para>not implemented yet</para>
                /// <para>enables query of begin-end time span data from all operations to get an idea of efficiency gained</para>
                /// </summary>
                public bool queryTimelineOverlapPercentage { get { throw new NotImplementedException(); } set { throw new NotImplementedException(); } }

                /// <summary>
                /// <para>not implemented yet</para>
                /// <para>if a stage was totally hidden in timeline by other stages, it has 100</para>
                /// <para>if a stage was not overlapped not even a bit, it has zero value</para>
                /// <para>each element of this int array indicates a stage in pipeline with same index/position</para>
                /// </summary>
                public int[] stagesOverlappingPercentages { get { throw new NotImplementedException(); } set { throw new NotImplementedException(); } }

                /// <summary>
                /// add next stage at the end of current pipeline
                /// </summary>
                public void addStage(DevicePipelineStage stage)
                {
                    stages.Add(stage);
                    if (stages.Count > 1)
                    {
                        for (int i = 0; i < stage.buffers.Count; i++)
                        {
                            for (int j = 0; j < stages[stages.Count - 2].buffers.Count; j++)
                            {
                                if (stage.buffers[i].buf == stages[stages.Count - 2].buffers[j].buf)
                                {
                                    // to do: if buf has zeroCopy and similar flags set, bufDuplicate should get it too
                                    stage.buffers[i].bufDuplicate = stages[stages.Count - 2].buffers[j].bufDuplicate;
                                }
                            }
                        }
                    }
                }

                private int runCounter { get; set; }
                private void serialI()
                {
                    for (int i = 0; i < stages.Count; i++)
                    {
                        if (stages[i].hasInput)
                        {
                            if(!stages[i].stopHostDeviceTransmission)
                                stages[i].copyInputDataToUnusedEntrance();
                        }
                    }
                }

                private void serialO()
                {
                    for (int i = 0; i < stages.Count; i++)
                    {
                        if (stages[i].hasOutput)
                        {
                            if(!stages[i].stopHostDeviceTransmission)
                                stages[i].copyOutputDataFromUnusedExit();
                        }
                    }
                }

                private void feedSerial()
                {
                    cruncher.enqueueMode = true;
                    for (int i = 0; i < stages.Count; i++)
                    {
                        if ((stages[i].hasInput) && !stages[i].stopHostDeviceTransmission)
                        {
                            cruncher.noComputeMode = true;
                            stages[i].enableInput();
                            stages[i].regroupParameters().compute(cruncher, i, stages[i].kernelNames, stages[i].globalRange, stages[i].localRange);
                            cruncher.noComputeMode = false;
                            stages[i].disableInput();
                        }
                        stages[i].regroupParameters().compute(cruncher, i, stages[i].kernelNames, stages[i].globalRange, stages[i].localRange);
                        if ((stages[i].hasOutput) && !stages[i].stopHostDeviceTransmission)
                        {
                            cruncher.noComputeMode = true;
                            stages[i].enableOutput();
                            stages[i].regroupParameters().compute(cruncher, i, stages[i].kernelNames, stages[i].globalRange, stages[i].localRange);
                            cruncher.noComputeMode = false;
                            stages[i].disableOutput();
                        }

                    }
                    cruncher.enqueueMode = false;
                }

                private void feedParallel()
                {


                    if (runCounter == 0)
                    {
                        for (int i = 0; i < stages.Count; i++)
                        {
                            if ((i % 2) == 0)
                                stages[i].switchBuffers();
                        }
                    }

                    for (int i = 0; i < stages.Count; i++)
                    {

                        if ((stages[i].hasInput || stages[i].hasOutput) && !stages[i].stopHostDeviceTransmission)
                        {
                            cruncher.enqueueModeAsyncEnable = true;
                            cruncher.noComputeMode = true;
                            stages[i].enableInput();
                            stages[i].enableOutput();
                            stages[i].regroupParameters().compute(cruncher, i, stages[i].kernelNames, stages[i].globalRange, stages[i].localRange);
                            cruncher.noComputeMode = false;
                            stages[i].switchIOBuffers();
                            stages[i].disableInput();
                            stages[i].disableOutput();
                            cruncher.flush();
                            cruncher.enqueueModeAsyncEnable = false;

                        }

                        cruncher.enqueueModeAsyncEnable = true;
                        stages[i].regroupParameters().compute(cruncher, i, stages[i].kernelNames, stages[i].globalRange, stages[i].localRange);
                        cruncher.flush();
                        cruncher.enqueueModeAsyncEnable = false;




                    }

                    // moved from here to a separate method
                    //Parallel.For(0, stages.Count, i => {
                    //    if (stages[i].hasInput && !stages[i].stopHostDeviceTransmission)
                    //    {
                    //        stages[i].copyInputDataToUnusedEntrance();
                    //    }

                    //    if (stages[i].hasOutput && !stages[i].stopHostDeviceTransmission)
                    //    {
                    //        stages[i].copyOutputDataFromUnusedExit();
                    //    }
                    //});
                    

                    for (int i = 0; i < stages.Count; i++)
                    {
                        stages[i].switchBuffers();
                    }

                }

                private void feedBegin()
                {

                    if (cruncher == null)
                    {
                        cruncher = new ClNumberCruncher(singleDevice, kernelCodesToCompile,false, currentComputeQueueConcurrency);
                    }
                    if (!serialMode)
                        cruncher.enqueueMode = true;

                }

                private void parallelIO()
                {
                    Parallel.For(0, stages.Count, i => {
                        if (stages[i].hasInput && !stages[i].stopHostDeviceTransmission)
                        {
                            stages[i].copyInputDataToUnusedEntrance();
                        }

                        if (stages[i].hasOutput && !stages[i].stopHostDeviceTransmission)
                        {
                            stages[i].copyOutputDataFromUnusedExit();
                        }
                    });
                }

                private void feedEnd()
                {
                    if(!serialMode)
                        cruncher.enqueueMode = false;
                    runCounter++;
                }

                /// <summary>
                /// <para>pushes data to entrance of pipeline, all stages run, pops results from end point
                /// </para>
                /// <param name="data">array of input parameters(arrays)</param>
                /// </summary>
                public void feed()
                {
                    if (serialMode)
                        serialI();
                    feedBegin();
                    if (serialMode)
                        feedSerial();
                    else
                    {
                        feedParallel();
                        parallelIO();
                    }
                    
                    feedEnd();
                    if (serialMode)
                        serialO();
                }

                /// <summary>
                /// runs pipeline and executes a method given as parameter at the same time
                /// </summary>
                public void feedAsync(Delegate del)
                {
                    if (serialMode)
                        serialI();
                    feedBegin();
                    if (serialMode)
                        feedSerial();
                    else
                    {
                        feedParallel();
                        parallelIO();
                    }
                    del.DynamicInvoke();
                    feedEnd();
                    if (serialMode)
                        serialO();
                }

                /// <summary>
                /// asynchronously starts enqueuing stages and synchronizes when feedAsyncEnd is called
                /// </summary>
                public void feedAsyncBegin()
                {
                    if (serialMode)
                        serialI();
                    feedBegin();
                    if (serialMode)
                        feedSerial();
                    else
                    {
                        feedParallel();
                        parallelIO();
                    }
                }

                /// <summary>
                /// synchronizes the work started with feedAsyncEnd on host side, blocks until it finishes
                /// </summary>
                public void feedAsyncEnd()
                {
                    feedEnd();
                    if (serialMode)
                        serialO();
                }

                /// <summary>
                /// disables multi queue usage, serializes all operations, disables double buffering, result is computed immediately and ready at outputs
                /// </summary>
                public void enableSerialMode()
                {
                    serialMode = true;
                }


                /// <summary>
                /// enables multiple queues to compute all stages concurrently to maximize throughput with help of double buffering
                /// </summary>
                public void enableParallelMode()
                {
                    serialMode = false;
                }

                /// <summary>
                /// runs a C# host method asynchronously to OpenCL kernel, saves time 
                /// </summary>
                public void asyncHostWork()
                {

                }
            }

            /// <summary>
            /// runs a kernel function at each pipeline feed() call using its inputs,outputs and internal arrays
            /// </summary>
            public class DevicePipelineStage
            {
                /// <summary>
                /// <para> when set, this makes pipeline skip any future buffer read/write operations from host to device and device to host</para>
                /// <para> only for this pipeline stage instance </para>
                /// </summary>
                public bool stopHostDeviceTransmission
                {
                    get;set;
                }

                internal void debugBuffers()
                {
                    Console.WriteLine("--------------------------");

                    for (int i = 0; i < arrays.Count; i++)
                    {
                        if((arrays[i].type != DevicePipelineArrayType.INPUT) && (arrays[i].type != DevicePipelineArrayType.OUTPUT))
                        {
                            Console.WriteLine(buffers[i].debugSwitchCount()%2);
                        }
                    }
                    Console.WriteLine("--------------------------");
                }

                internal void copyInputDataToUnusedEntrance()
                {
                    for (int i = 0; i < arrays.Count; i++)
                    {
                        if (arrays[i].type == DevicePipelineArrayType.INPUT)
                        {
                            IBufferOptimization destination = null;
                            if (ioSwitchCounter % 2 ==0)
                            {
                                destination = buffers[i].bufDuplicate;
                            }
                            else
                            {
                                destination = buffersIODuplicates[i].bufDuplicate;

                            }
                            if (buffers[i].buf.GetType() == typeof(ClArray<float>))
                            {
                                var source = buffers[i].buf as ClArray<float>;
                                source.CopyTo((ClArray<float>)destination, 0);
                            }
                            else if (buffers[i].buf.GetType() == typeof(ClArray<double>))
                            {
                                var source = buffers[i].buf as ClArray<double>;
                                source.CopyTo((ClArray<double>)destination, 0);
                            }
                            else if (buffers[i].buf.GetType() == typeof(ClArray<byte>))
                            {
                                var source = buffers[i].buf as ClArray<byte>;
                                source.CopyTo((ClArray<byte>)destination, 0);
                            }
                            else if (buffers[i].buf.GetType() == typeof(ClArray<char>))
                            {
                                var source = buffers[i].buf as ClArray<char>;
                                source.CopyTo((ClArray<char>)destination, 0);
                            }
                            else if (buffers[i].buf.GetType() == typeof(ClArray<int>))
                            {
                                var source = buffers[i].buf as ClArray<int>;
                                source.CopyTo((ClArray<int>)destination, 0);
                            }
                            else if (buffers[i].buf.GetType() == typeof(ClArray<uint>))
                            {
                                var source = buffers[i].buf as ClArray<uint>;
                                source.CopyTo((ClArray<uint>)destination, 0);
                            }
                            else if (buffers[i].buf.GetType() == typeof(ClArray<long>))
                            {
                                var source = buffers[i].buf as ClArray<long>;
                                source.CopyTo((ClArray<long>)destination, 0);
                            }
                        }
                    }
                }

                internal void copyOutputDataFromUnusedExit()
                {
                    for (int i = 0; i < arrays.Count; i++)
                    {
                        if (arrays[i].type == DevicePipelineArrayType.OUTPUT)
                        {
                            IBufferOptimization source = null;
                            if (ioSwitchCounter % 2 == 0)
                            {
                                source = buffers[i].bufDuplicate;
                            }
                            else
                            {
                                source = buffersIODuplicates[i].bufDuplicate;

                            }
                            if (buffers[i].buf.GetType() == typeof(ClArray<float>))
                            {
                                var destination = buffers[i].buf as ClArray<float>;
                                destination.CopyFrom((ClArray<float>)source, 0);
                            }
                            else if (buffers[i].buf.GetType() == typeof(ClArray<double>))
                            {
                                var destination = buffers[i].buf as ClArray<double>;
                                destination.CopyFrom((ClArray<double>)source, 0);
                            }
                            else if (buffers[i].buf.GetType() == typeof(ClArray<byte>))
                            {
                                var destination = buffers[i].buf as ClArray<byte>;
                                destination.CopyFrom((ClArray<byte>)source, 0);
                            }
                            else if (buffers[i].buf.GetType() == typeof(ClArray<char>))
                            {
                                var destination = buffers[i].buf as ClArray<char>;
                                destination.CopyFrom((ClArray<char>)source, 0);
                            }
                            else if (buffers[i].buf.GetType() == typeof(ClArray<int>))
                            {
                                var destination = buffers[i].buf as ClArray<int>;
                                destination.CopyFrom((ClArray<int>)source, 0);
                            }
                            else if (buffers[i].buf.GetType() == typeof(ClArray<uint>))
                            {
                                var destination = buffers[i].buf as ClArray<uint>;
                                destination.CopyFrom((ClArray<uint>)source, 0);
                            }
                            else if (buffers[i].buf.GetType() == typeof(ClArray<long>))
                            {
                                var destination = buffers[i].buf as ClArray<long>;
                                destination.CopyFrom((ClArray<long>)source, 0);
                            }
                        }
                    }
                }

                

                /// <summary>
                /// returns true if this stage has any input array
                /// </summary>
                public bool hasInput
                {
                    get
                    {
                        for (int i = 0; i < arrays.Count; i++)
                        {
                            if (arrays[i].type == DevicePipelineArrayType.INPUT)
                                return true;
                        }
                        return false;
                    }
                }


                /// <summary>
                /// returns true if this stage has any output array
                /// </summary>
                public bool hasOutput
                {
                    get
                    {
                        for (int i = 0; i < arrays.Count; i++)
                        {
                            if (arrays[i].type == DevicePipelineArrayType.OUTPUT)
                                return true;
                        }
                        return false;
                    }
                }

                internal void enableOutput()
                {
                    for (int i = 0; i < arrays.Count; i++)
                    {
                        if (arrays[i].type == DevicePipelineArrayType.OUTPUT)
                        {
                                buffers[i].enableOutput();
                                buffersIODuplicates[i].enableOutput();
                        }
                    }
                }

                internal void enableInput()
                {
                    for (int i = 0; i < arrays.Count; i++)
                    {
                        if (arrays[i].type == DevicePipelineArrayType.INPUT)
                        {
                                buffers[i].enableInput();
                                buffersIODuplicates[i].enableInput();
                        }
                    }
                }


                internal void disableOutput()
                {
                    for (int i = 0; i < arrays.Count; i++)
                    {
                        if (arrays[i].type == DevicePipelineArrayType.OUTPUT)
                        {
                                buffers[i].disableOutput();
                                buffersIODuplicates[i].disableOutput();
                        }
                    }
                }

                internal void disableInput()
                {
                    for (int i = 0; i < arrays.Count; i++)
                    {
                        if (arrays[i].type == DevicePipelineArrayType.INPUT)
                        {
                                buffers[i].disableInput();
                                buffersIODuplicates[i].disableInput();
                        }
                    }
                }

                internal List<ClPipelineStageBuffer> buffers { get; set; }
                private List<ClPipelineStageBuffer> buffersIODuplicates { get; set; }
                internal List<DevicePipelineArray> arrays { get; set; }
                private int ioSwitchCounter { get; set; }
                internal string kernelNames { get; set; }
                internal int globalRange { get; set; }
                internal int localRange { get; set; }
                /// <summary>
                /// runs kernel by name(s) with global-local workitem numbers
                /// </summary>
                /// <param name="kernelNames_"></param>
                /// <param name="globalRange_"></param>
                /// <param name="localRange_"></param>
                public DevicePipelineStage(string kernelNames_, int globalRange_, int localRange_)
                {
                    ioSwitchCounter = 0;
                    buffers = new List<ClPipelineStageBuffer>();
                    buffersIODuplicates = new List<ClPipelineStageBuffer>();
                    arrays = new List<DevicePipelineArray>();
                    kernelNames = new StringBuilder(kernelNames_).ToString();
                    globalRange = globalRange_;
                    localRange = localRange_;
                }

                /// <summary>
                /// <para>if there is single array, outputs it, if there are multiple array, returns ClParameterGroup</para>
                /// <para>regroups in the same order they were added, to be used in compute() kernel parameters</para>
                /// </summary>
                /// <returns></returns>
                internal ICanCompute regroupParameters()
                {
                    if(buffers.Count==1)
                    {

                        if (arrays[0].type == DevicePipelineArrayType.INPUT)
                        {
                            if (ioSwitchCounter % 2 == 0)
                                return (ICanCompute)(buffers[0].bufDuplicate);
                            else
                                return (ICanCompute)(buffersIODuplicates[0].bufDuplicate);

                        }
                        else if(arrays[0].type == DevicePipelineArrayType.OUTPUT)
                        {
                            if (ioSwitchCounter % 2 == 0)
                                return (ICanCompute)(buffers[0].bufDuplicate);
                            else
                                return (ICanCompute)(buffersIODuplicates[0].bufDuplicate);
                        }
                        else
                        {
                            return (ICanCompute)(buffers[0].buf);
                        }
                    }
                    else if(buffers.Count>1)
                    {
                        ClParameterGroup gr = null;
                        if ((arrays[0].type == DevicePipelineArrayType.INPUT) || (arrays[0].type == DevicePipelineArrayType.OUTPUT))
                        {

                            if ((arrays[1].type == DevicePipelineArrayType.INPUT) || (arrays[1].type == DevicePipelineArrayType.OUTPUT))
                            {

                                if (ioSwitchCounter % 2 == 0)
                                {

                                    gr = ((ICanBind)buffers[0].bufDuplicate).nextParam(buffers[1].bufDuplicate);
                                }
                                else
                                {

                                    gr = ((ICanBind)buffersIODuplicates[0].bufDuplicate).nextParam(buffersIODuplicates[1].bufDuplicate);
                                }
                            }
                            else
                            {

                                if (ioSwitchCounter % 2 == 0)
                                {

                                    gr = ((ICanBind)buffers[0].bufDuplicate).nextParam(buffers[1].buf);
                                }
                                else
                                {

                                    gr = ((ICanBind)buffersIODuplicates[0].bufDuplicate).nextParam(buffers[1].buf);
                                }
                            }
                        }
                        else
                        {
                            if ((arrays[1].type == DevicePipelineArrayType.INPUT) || (arrays[1].type == DevicePipelineArrayType.OUTPUT))
                            {
                                if (ioSwitchCounter % 2 == 0)
                                {
                                    gr = ((ICanBind)buffers[0].buf).nextParam(buffers[1].bufDuplicate);
                                }
                                else
                                {
                                    gr = ((ICanBind)buffers[0].buf).nextParam(buffersIODuplicates[1].bufDuplicate);
                                }
                            }
                            else
                            {
                                if (ioSwitchCounter % 2 == 0)
                                {
                                    gr = ((ICanBind)buffers[0].buf).nextParam(buffers[1].buf);
                                }
                                else
                                {
                                    gr = ((ICanBind)buffers[0].buf).nextParam(buffers[1].buf);
                                }
                            }
                        }

                        for (int i=2;i<buffers.Count;i++)
                        {
                            if (arrays[i].type == DevicePipelineArrayType.INPUT)
                            {
                                if(ioSwitchCounter%2==0)
                                    gr = gr.nextParam(buffers[i].bufDuplicate);
                                else
                                    gr = gr.nextParam(buffersIODuplicates[i].bufDuplicate);


                            }
                            else if (arrays[i].type == DevicePipelineArrayType.OUTPUT)
                            {
                                if(ioSwitchCounter%2==0)
                                    gr = gr.nextParam(buffers[i].bufDuplicate);
                                else
                                    gr = gr.nextParam(buffersIODuplicates[i].bufDuplicate);
                            }
                            else
                            {
                                gr = gr.nextParam(buffers[i].buf);
                            }
                        }
                        return gr;
                    }
                    else
                    {
                        Console.WriteLine("error: no array in pipeline stage.");
                        return null;
                    }

                }


                /// <summary>
                /// switches buffers with their duplicates()
                /// </summary>
                internal void switchBuffers()
                {
                    for (int i = 0; i < buffers.Count; i++)
                    {
                        if ((arrays[i].type != DevicePipelineArrayType.OUTPUT) && (arrays[i].type != DevicePipelineArrayType.INPUT))
                        {
                            // switch transition and internal can't switch
                            if (buffers[i].bufDuplicate != null)
                            {
                                    buffers[i].switchBuffers();
                            }
                        }
                    }

                }
                

                internal void switchIOBuffers()
                {
                        ioSwitchCounter++;
                }

                

                /// <summary>
                /// <para> binds an input, output or internal array to be used by kernel</para>
                /// <para> must be bound with the same order of kernel parameters</para>
                /// </summary>
                public void bindArray(DevicePipelineArray array_)
                {
                    // to do: if previous stage array is same, use its duplicate instead of creating a new duplicate
                    // 3x arrays are created for even the transition buffers

                    ClPipelineStageBuffer newArray = null;
                    ClPipelineStageBuffer newArray2 = null;
                    if (array_.type == DevicePipelineArrayType.INPUT)
                    {
                        newArray = new ClPipelineStageBuffer(array_.array);
                        newArray2 = new ClPipelineStageBuffer(array_.array);
                        newArray.buf.readOnly = false;
                        newArray.buf.read = false;
                        newArray.buf.partialRead = false;
                        newArray.buf.write = false;
                        newArray.buf.writeAll = false;

                        newArray.bufDuplicate.readOnly = true;
                        newArray.bufDuplicate.read = true;
                        newArray.bufDuplicate.partialRead = false;
                        newArray.bufDuplicate.write = false;
                        newArray.bufDuplicate.writeAll = false;

                        newArray2.buf.readOnly = false;
                        newArray2.buf.read = false;
                        newArray2.buf.partialRead = false;
                        newArray2.buf.write = false;
                        newArray2.buf.writeAll = false;

                        newArray2.bufDuplicate.readOnly = true;
                        newArray2.bufDuplicate.read = true;
                        newArray2.bufDuplicate.partialRead = false;
                        newArray2.bufDuplicate.write = false;
                        newArray2.bufDuplicate.writeAll = false;
                    }
                    else if(array_.type == DevicePipelineArrayType.OUTPUT)
                    {
                        newArray = new ClPipelineStageBuffer(array_.array);
                        newArray2 = new ClPipelineStageBuffer(array_.array);
                        newArray.buf.writeOnly = false;
                        newArray.buf.read = false;
                        newArray.buf.partialRead = false;
                        newArray.buf.write = false;
                        newArray.buf.writeAll = false;

                        newArray.bufDuplicate.writeOnly = true;
                        newArray.bufDuplicate.read = false;
                        newArray.bufDuplicate.partialRead = false;
                        newArray.bufDuplicate.write = true;
                        newArray.bufDuplicate.writeAll = true;


                        newArray2.buf.writeOnly = false;
                        newArray2.buf.read = false;
                        newArray2.buf.partialRead = false;
                        newArray2.buf.write = false;
                        newArray2.buf.writeAll = false;

                        newArray2.bufDuplicate.writeOnly = true;
                        newArray2.bufDuplicate.read = false;
                        newArray2.bufDuplicate.partialRead = false;
                        newArray2.bufDuplicate.write = true;
                        newArray2.bufDuplicate.writeAll = true;
                    }
                    else if(array_.type == DevicePipelineArrayType.INTERNAL)
                    {
                        newArray = new ClPipelineStageBuffer(array_.array,false);
                        newArray2 = new ClPipelineStageBuffer(array_.array,false);
                        newArray.buf.read = false;
                        newArray.buf.partialRead = false;
                        newArray.buf.write = false;
                        newArray.buf.writeAll = false;

                        newArray2.buf.read = false;
                        newArray2.buf.partialRead = false;
                        newArray2.buf.write = false;
                        newArray2.buf.writeAll = false;
                    }
                    else if (array_.type == DevicePipelineArrayType.TRANSITION)
                    {
                        newArray = new ClPipelineStageBuffer(array_.array);
                        newArray2 = new ClPipelineStageBuffer(array_.array,false);
                        newArray.buf.read = false;
                        newArray.buf.partialRead = false;
                        newArray.buf.write = false;
                        newArray.buf.writeAll = false;

                        newArray.bufDuplicate.read = false;
                        newArray.bufDuplicate.partialRead = false;
                        newArray.bufDuplicate.write = false;
                        newArray.bufDuplicate.writeAll = false;


                    }
                    arrays.Add(array_);
                    buffers.Add(newArray);
                    buffersIODuplicates.Add(newArray2);
                }
            }

            /// <summary>
            /// <para>input: read-only from kernel, write-only from host (uses read, not partial read)</para>
            /// <para>output: write-only from kernel, read-only from host (uses writeAll, not write)</para>
            /// <para>internal: only kernel accesses data, not duplicated</para>
            /// <para>transition: device side data flow between two stages</para>
            /// <para>partial/full access behavior could be changed later (optionally) from ClArray instance directly</para>
            /// </summary>
            public enum DevicePipelineArrayType : int
            {
                /// <summary>
                /// <para>read-only for kernel, write-only for host, duplicated for double buffering</para>
                /// <para>gets duplicated for double buffering for efficient communication</para>
                /// </summary>
                INPUT = 0,

                /// <summary>
                /// <para>write-only for kernel, read-only for host, duplicated for double buffering</para>
                /// <para>gets duplicated for double buffering for efficient communication</para>
                /// </summary>
                OUTPUT = 1,

                /// <summary>
                /// <para>only accessed by a stage's own kernel</para>
                /// <para>doesn't get duplicated for double buffering</para> 
                /// <para>used for sequential logic or as  accumulators</para>
                /// <para></para>
                /// </summary>
                INTERNAL = 2,

                /// <summary>
                /// <para>binds two stages being an output of first one and an input of second one. </para>
                /// <para>gets duplicated for double buffering between two stages</para>
                /// </summary>
                TRANSITION = 3
            }

            /// <summary>
            /// pipeline stage arrays with behavior definition
            /// </summary>
            public class DevicePipelineArray
            {
                /// <summary>
                /// purpose of array. internal=sequential logic, input + output = combinational logic
                /// </summary>
                public DevicePipelineArrayType type { get; set; }

                /// <summary>
                /// encapsulated array
                /// </summary>
                public object array { get; set; }

                /// <summary>
                /// creates a pipeline buffer to be double-buffered for overlapped transmissions and computations
                /// </summary>
                /// <param name="type_"></param>
                /// <param name="array_">float[], byte[], ClArray, ClFloatArray, ClByteArray,...</param>
                public DevicePipelineArray(DevicePipelineArrayType type_, object array_)
                {
                    type = type_;
                    array = array_;
                }

            }
        }


        /// <summary>
        /// <para>Namespace to build a pool of workloads or workers to optimize for performance</para>
        /// <para></para>
        /// </summary>
        namespace Pool
        {

            /// <summary>
            /// <para>a piece of work to be done later</para>
            /// <para>contains necessary info to complete a array.compute() operation</para>
            /// <para>ClArray.task() ClParameterGroup.task()</para>
            /// <para>is meant to be computed later in a pool(of tasks) by a pool of devices(each device compute a single task at a time or a part of it)</para>
            /// <para>main advantage is to stop code duplication where a lot of read/write state changes are needed between compute() operations</para>
            /// <para>secondary advantage is to automate device selection for a group of tasks within pools</para>
            /// </summary>
            public class ClTask
            {
                // data to compute (single array or ClParameterGroup)
                internal ICanCompute data { get; set; }

                // frozen array states
                internal string[] readWrite { get; set; }
                internal int[] elementsPerItem { get; set; }

                // compute parameters
                internal int computeId { get; set; }
                internal string kernelNamesString { get; set; }
                internal int globalRange { get; set; }
                internal int localRange { get; set; }
                internal int ofsetGlobalRange { get; set; }
                internal bool pipeline { get; set; }
                internal bool pipelineType { get; set; }
                internal int pipelineBlobs { get; set; }

                /// <summary>
                /// not implemented yet
                /// </summary>
                public int kernelRepeats { get { throw new NotImplementedException(); } set { throw new NotImplementedException(); } }

                /// <summary>
                /// not implemented yet
                /// </summary>
                public string kernelRepeatName { get { throw new NotImplementedException(); } set { throw new NotImplementedException(); } }

                /// <summary>
                /// computes this task using the given number cruncher
                /// </summary>
                /// <param name="numberCruncher"></param>
                public void compute(ClNumberCruncher numberCruncher)
                {
                    data.compute(numberCruncher, computeId, kernelNamesString, globalRange, localRange, ofsetGlobalRange, pipeline, pipelineType, pipelineBlobs, readWrite, elementsPerItem);
                }

                /// <summary>
                /// only ClParameterGroup or a ClArray can create this
                /// </summary>
                internal ClTask(ICanCompute dataParameter, int computeIdParameter, string kernelNamesStringParameter, int globalRangeParameter,
                                int localRangeParameter = 256, int ofsetGlobalRangeParameter = 0, bool pipelineParameter = false,
                                bool pipelineTypeParameter = Cores.PIPELINE_EVENT, int pipelineBlobsParameter = 4,string[] readWriteParameter=null,int[] elementsPerItemParameter=null)
                {

                    data = dataParameter;

                    computeId = computeIdParameter;
                    kernelNamesString = new StringBuilder( kernelNamesStringParameter).ToString();
                    globalRange = globalRangeParameter;
                    localRange = localRangeParameter;
                    ofsetGlobalRange = ofsetGlobalRangeParameter;
                    pipeline = pipelineParameter;
                    pipelineType = pipelineTypeParameter;
                    pipelineBlobs = pipelineBlobsParameter;

                    readWrite = readWriteParameter;
                    elementsPerItem = elementsPerItemParameter;
                }
            }


            /// <summary>
            /// type of task group that defines execution behavior of all of its tasks
            /// </summary>
            public enum ClTaskGroupType:int
            {
                /// <summary>
                /// <para>all devices work for this task group until all tasks in it are completed</para>
                /// <para>this is default value</para>
                /// <para></para>
                /// </summary>
                TASK_COMPLETE=0,

                /// <summary>
                /// <para>all devices are free to pick tasks from this task group or any other group</para>
                /// <para>suitable for combinatorial logic based pipelines and independent workloads</para>
                /// <para>a case of nbody+image processing+fluid dynamics being in same group is an example</para>
                /// <para>the only advantage compared to non-grouped tasks is: WORK_ROUND_ROBIN type device pools will issue groups equally</para>
                /// <para>so two different workloads can be given equal priority without defining any priority value</para>
                /// </summary>
                TASK_ASYNC = 1,

                /// <summary>
                /// <para>all tasks in a group is executed in same device always</para>
                /// <para>doesn't guarantee to run on same device if repeated</para>
                /// <para>doesn't guarantee to run tasks in the order they were added</para>
                /// </summary>
                TASK_SAME_DEVICE = 2,

                /// <summary>
                /// <para>repeating same task group is done by same device always</para>
                /// <para>doesn't guarantee to run tasks in the order they were added</para>
                /// <para>suitable for pipelines</para>
                /// </summary>
                TASK_REPEAT_SAME_DEVICE = 4,

                /// <summary>
                /// <para>all tasks in group is issued into same command queue of same device</para>
                /// <para>every next task in group sees the updated bits of latest issued task in terms of memory consistency</para>
                /// <para>running commands in an in-order queue is always synchronized before next command, on device side(kernels) and host side(buffer copy)</para>
                /// <para>repeating a group doesn't guarantee same device nor same command queue</para>
                /// </summary>
                TASK_IN_ORDER = 8,

                /// <summary>
                /// <para>all tasks in group is issued into same command queue of same device</para>
                /// <para>every next task in group sees the updated bits of latest issued task in terms of memory consistency</para>
                /// <para>running commands in an in-order queue is always synchronized before next command, on device side(kernels) and host side(buffer copy)</para>
                /// <para>repeating a group guarantees same device and same command queue to run</para>
                /// <para>for WORK_ROUND_ROBIN enabled device pools, there may be a different task group's tasks between any two tasks of current group in a command queue</para>
                /// </summary>
                TASK_REPEAT_IN_ORDER = 16,

            }

            /// <summary>
            /// <para>a group of tasks to be computed with same execution behavior</para>
            /// </summary>
            public class ClTaskGroup
            {
                /// <summary>
                /// <para>a group of tasks to be computed with same execution behavior</para>
                /// </summary>
                public ClTaskGroup(ClTaskGroupType type)
                {
                    
                }

                /// <summary>
                /// adds a new task
                /// </summary>
                /// <param name="task"></param>
                public void add(ClTask task)
                {

                }

            }

            /// <summary>
            /// defines type of task pool
            /// </summary>
            public enum ClTaskPoolType:int
            {
                /// <summary>
                /// <para>devices can't choose another pool before finishing all tasks in this pool</para>
                /// <para>default value</para>
                /// </summary>
                TASK__COMPLETE = 0,

                /// <summary>
                /// any device may pick another pool to continue
                /// </summary>
                TASK_ASYNC=1,

                /// <summary>
                /// <para>a device must pick another pool to continue</para>
                /// <para>suitable for equally important task pools</para>
                /// </summary>
                TASK_SYNC = 2
            }

            /// <summary>
            /// <para>a pool of tasks to be computed by a pool of devices with optional scheduling algorithms</para>
            /// <para>re-usable with resetting inner counter</para>
            /// </summary>
            public class ClTaskPool
            {
                object syncObj { get; set; }
                int counter { get; set; }
                List<ClTask> taskList { get; set; }
                internal ClTaskPoolType type { get; set; }
                /// <summary>
                /// creaetes a pool that receives tasks
                /// </summary>
                public ClTaskPool(ClTaskPoolType typeParameter)
                {
                    type = typeParameter;
                    syncObj = new object();
                    lock (syncObj)
                    {
                        counter = 0;
                        taskList = new List<ClTask>();
                    }
                }

                /// <summary>
                /// clears task counter and makes this pool re-usable again
                /// </summary>
                public void reset()
                {
                    lock (syncObj)
                    {
                        counter = 0;
                    }
                }

                /// <summary>
                /// <para>pushes a new ClTask instance to one end of queue to compute later</para>
                /// <para>compute operations are issued from other end of queue</para>
                /// </summary>
                public void feed(ClTask task)
                {
                    bool empty = true;
                    while(empty)
                    {
                        lock(syncObj)
                        {
                            empty = (taskList == null);
                        }
                    }
                    lock(syncObj)
                    {
                        taskList.Add(task);
                    }
                }

                /// <summary>
                /// get next task in the list
                /// </summary>
                public ClTask nextTask()
                {
                    bool empty = true;
                    while (empty)
                    {
                        lock (syncObj)
                        {
                            empty = (taskList == null);
                        }
                    }
                    ClTask next = null;
                    lock (syncObj)
                    {
                        if(counter<taskList.Count && counter>=0)
                        {
                            next = taskList[counter];
                        }
                        counter++;
                        Monitor.PulseAll(syncObj);
                    }
                    return next;
                }

                /// <summary>
                /// returns number of tasks(and groups) left
                /// </summary>
                /// <returns></returns>
                public int remainingTaskGroupsOrTasks()
                {
                    bool empty = true;
                    while (empty)
                    {
                        lock (syncObj)
                        {
                            empty = (taskList == null);
                        }
                    }
                    int num = 0;
                    lock(syncObj)
                    {
                        num = taskList.Count - counter;
                        if (num < 0)
                            num = 0;
                    }
                    return num;
                }

                /// <summary>
                /// <para>pushes a new ClTaskGroup instance to one end of queue to compute later</para>
                /// <para>compute operations are issued from other end of queue</para>
                /// </summary>
                public void feed(ClTaskGroup taskGroup)
                {

                }
            }




            /// <summary>
            /// <para>to pick a specific scheduler algorithm</para>
            /// <para>WORKER_ and WORK_ prefixed types can be combined with OR</para>
            /// </summary>
            public enum ClDevicePoolType:int
            {

                /// <summary>
                /// <para>a device in pool issues a task, then next task is issued by next device only</para>
                /// <para>default value</para>
                /// </summary>
                WORKER_ROUND_ROBIN=0,

                /// <summary>
                /// <para>executes newly added tasks one by one in the order they were added</para>
                /// <para>completes task before moving to next task so its better to have multiple devices in pool</para>
                /// </summary>
                WORK_FIRST_COME_FIRST_SERVE = 1,

                /// <summary>
                /// <para>picks tasks by their global range values and their user defined compute-to-workitem ratios</para>
                /// <para>completes task before moving to next task</para>
                /// </summary>
                WORK_SHORTEST_JOB_FIRST = 2,

                /// <summary>
                /// <para>picks a group of tasks then iteratively picks tasks in a FCFS way</para>
                /// <para>then cycles from beginning and repeats until all picked tasks are complete</para>
                /// <para>executes only a single enqueued command in task before moving to next task</para>
                /// <para>quantum here is a single read/write or a single kernel</para>
                /// <para>meant to arrive finish points all tasks at the same time </para>
                /// <para>even a single device works on many tasks in parallel</para>
                /// </summary>
                WORK_ROUND_ROBIN = 4,

                /// <summary>
                /// <para>picks highest priority tasks first</para>
                /// <para>completes task before moving to next task</para>
                /// </summary>
                WORK_PRIORITY_BASED = 8,

                /// <summary>
                /// <para>all devices in pool work at the same time and synchronize on host after each task or quanta</para>
                /// <para>if there are two devices, packet size is 2, 2 tasks are issued at a time</para>
                /// </summary>
                WORKER_PACKET = 16,

                /// <summary>
                /// whenever a device becomes ready after computing a task, immediately issues another task
                /// </summary>
                WORKER_COMPUTE_AT_WILL=32,



            }

            /// <summary>
            /// <para>instead of working for same workload (as in ClNumberCruncher class)</para>
            /// <para>this container specializes to distribute work independently to differend devices</para>
            /// <para>uses a multitude of scheduling algorithms</para>
            /// </summary>
            public class ClDevicePool
            {
                object syncObj { get; set; }
                ClDevicePoolType type { get; set; }
                List<DevicePoolThread> devices { get; set; }
                string kernelCode { get; set; }
                List<ClTaskPool> taskPoolList { get; set; }
                bool running { get; set; }
                int taskPoolCounter { get; set; }
                int deviceCounter { get; set; }
                Thread poolControlThread { get; set; }
                ClTaskPool roundRobinSelectedTaskPool { get; set; }

                /// <summary>
                /// <para>creates a worker pool with a type</para>
                /// <para>any ClNumberCruncher instance added to this pool will work accordingly with the type algorithm</para>
                /// </summary>
                /// <param name="poolType"></param>
                /// <param name="kernelCodeToCompile"></param>
                public ClDevicePool(ClDevicePoolType poolType, string kernelCodeToCompile)
                {
                    type = poolType;
                    kernelCode = kernelCodeToCompile;
                    syncObj = new object();
                    devices = new List<DevicePoolThread>();
                    taskPoolList = new List<ClTaskPool>();
                    running = false;
                    taskPoolCounter = 0;
                    deviceCounter = 0;
                    roundRobinSelectedTaskPool = null;
                    ThreadStart ts = new ThreadStart(produceTasks);

                    poolControlThread = new Thread(ts);

                    poolControlThread.Start();

                }

                /// <summary>
                /// producer-consumer work flow's producer part that distributes tasks
                /// </summary>
                void produceTasks()
                {
                    bool tmp = true;
                    running = true;
                    while (tmp)
                    {
                        lock (syncObj)
                        {
                            // compute logic

                            DevicePoolThread selectedDevice = null;
                            if (type == ClDevicePoolType.WORKER_ROUND_ROBIN)
                            {

                                if (devices.Count > 0)
                                {
                                    selectedDevice = devices[deviceCounter % devices.Count];
                                    deviceCounter++;
                                }
                            }



                            ClTaskPool selectedTaskPool = null;

                            if (roundRobinSelectedTaskPool != null)
                            {
                                if (roundRobinSelectedTaskPool.remainingTaskGroupsOrTasks() > 0)
                                    selectedTaskPool = roundRobinSelectedTaskPool;
                            }
                            else
                            {
                                selectedTaskPool = taskPoolList[taskPoolCounter% taskPoolList.Count];
                                taskPoolCounter++;
                            }

                            if (selectedTaskPool != null)
                            {

                                if (selectedTaskPool.type == ClTaskPoolType.TASK__COMPLETE)
                                {
                                    if (roundRobinSelectedTaskPool == null)
                                        roundRobinSelectedTaskPool = selectedTaskPool;

                                    if ((selectedDevice != null))
                                    {
                                        ClTask selectedTask = selectedTaskPool.nextTask();
                                        if (selectedTask != null)
                                        {

                                            selectedDevice.feedTask(selectedTask);
                                        }
                                    }
                                }
                            }
                            tmp = running;
                            Monitor.PulseAll(syncObj);
                        }
                    }
                }

                /// <summary>
                /// <para>add devices to pool</para>
                /// <para>can add in the middle of computation of a task pool</para>
                /// <para>compilation of kernels can take some time</para>
                /// <para>adds a new consumer thread for each new (logical) device instance.</para> 
                /// <para>same device can be added multiple times, if there are enough resources in OpenCL side</para>
                /// </summary>
                /// <param name="devicesParameter"></param>
                public void addDevices(ClDevices devicesParameter)
                {
                    lock (syncObj)
                    {

                        var newDevice = new DevicePoolThread(new ClNumberCruncher(devicesParameter, kernelCode));

                        devices.Add(newDevice);
                        Monitor.PulseAll(syncObj);
                    }
                }

                /// <summary>
                /// <para>enqueues a new task pool to this device pool</para>
                /// <para>older pools reside until completed(their queues and containers empty)</para>
                /// <para>a device may choose a different pool for next task, depending on the task pool type</para>
                /// <para>this re-initiates producer part</para>
                /// </summary>
                /// <param name="taskPoolParameter"></param>
                public void enqueueTaskPool(ClTaskPool taskPoolParameter)
                {

                    lock (syncObj)
                    {

                        taskPoolList.Add(taskPoolParameter);
                        Monitor.PulseAll(syncObj);
                    }
                }

                /// <summary>
                /// waits until all tasks are complete
                /// </summary>
                public void finish()
                {

                    int count = 0;
                    for (int i = 0; i < taskPoolList.Count; i++)
                    {
                        lock (syncObj)
                        {
                            count += taskPoolList[i].remainingTaskGroupsOrTasks();
                        }
                    }
                    for (int i = 0; i < devices.Count; i++)
                    {
                        lock (syncObj)
                        {
                            count += devices[i].remainingTasks();
                        }
                    }
                    while (count > 0)
                    {
                        lock (syncObj)
                        {

                            Monitor.PulseAll(syncObj);
                            Monitor.Wait(syncObj);
                            count = 0;
                            for (int i = 0; i < taskPoolList.Count; i++)
                            {
                                count += taskPoolList[i].remainingTaskGroupsOrTasks();
                            }
                            for (int i = 0; i < devices.Count; i++)
                            {
                                count += devices[i].remainingTasks();
                            }
                        }
                    }

                    for (int i=0;i<devices.Count;i++)
                    {
                        lock (syncObj)
                        {
                            devices[i].dispose();
                            Monitor.PulseAll(syncObj);
                        }
                    }

                }
            }


            /// <summary>
            /// queue for producer-consumer
            /// </summary>
            class PoolTaskQueue
            {
                bool shutDown { get; set; }
                object syncObj { get; set; }
                Queue<ClTask> queue { get; set; }
                public int size { get; set; }
                public PoolTaskQueue(int sizeParameter = 100)
                {
                    syncObj = new object();
                    queue = new Queue<ClTask>();
                    size = sizeParameter;
                    shutDown = false;
                }

                public void disable()
                {
                    lock(syncObj)
                    {
                        shutDown = true;
                        Monitor.PulseAll(syncObj);
                    }
                }

                public void enable()
                {
                    lock(syncObj)
                    {
                        shutDown = false;
                        Monitor.PulseAll(syncObj);
                    }
                }

                public void push(ClTask task)
                {
                    lock (syncObj)
                    {
                        while (queue.Count >= size)
                        {
                            Monitor.Wait(syncObj);
                            if (shutDown)
                                break;
                        }

                        if(!shutDown)
                            queue.Enqueue(task);

                        Monitor.PulseAll(syncObj);
                    }
                }

                public ClTask pop()
                {
                    lock(syncObj)
                    {
                        while(queue.Count<=0)
                        {
                            Monitor.Wait(syncObj);
                            if (shutDown)
                                break;
                        }
                        if (shutDown)
                            return null;
                        return queue.Dequeue();
                    }
                }


            }

            class DevicePoolThread
            {
                Thread t { get; set; }
                object syncObj { get; set; }
                ClNumberCruncher numberCruncher { get; set; }
                Queue<ClTask> taskQueue { get; set; }
                bool computeComplete { get; set; }
                bool running { get; set; }
                bool paused { get; set; }
                public DevicePoolThread(ClNumberCruncher numberCruncherParameter)
                {
                    syncObj = new object();
                    numberCruncher = numberCruncherParameter;
                    computeComplete = true;
                    taskQueue = new Queue<ClTask>();
                    running = true;
                    paused = false;
                    ThreadStart ts = new ThreadStart(consumeTasks);
                    t = new Thread(ts);
                    t.Start();
                }

                public int remainingTasks()
                {
                    int count = 0;
                    lock (syncObj)
                    {
                        count = taskQueue.Count+(computeComplete?0:1);
                    }
                    return count;
                }

                //producer-consumer work flow's consumer part that computes distributed tasks
                void consumeTasks()
                {
                    bool working = true;
                    while (working)
                    {
                        ClTask currentTask = null;
                        lock (syncObj)
                        {
                            working = running;
                            if (taskQueue.Count>0)
                            {
                                currentTask = taskQueue.Dequeue();
                                computeComplete = false;
                            }

                            while(paused)
                            {
                                Monitor.PulseAll(syncObj);
                                Monitor.Wait(syncObj);
                            }
                        }

                        if(currentTask!=null)
                        {
                            currentTask.compute(numberCruncher);
                            lock(syncObj)
                            {
                                computeComplete = true;
                            }
                        }
                    }
                }

                public void start()
                {
                    lock(syncObj)
                    {
                        paused = false;
                        Monitor.PulseAll(syncObj);
                    }
                }

                public void pause()
                {
                    lock(syncObj)
                    {
                        paused = true;

                    }
                }

                public void dispose()
                {
                    lock(syncObj)
                    {
                        running = false;
                        paused = false;
                        Monitor.PulseAll(syncObj);
                    }
                }


                /// <summary>
                /// select a queue of a device pool(which is fed with tasks from task pools)
                /// </summary>
                public void feedTask(ClTask taskParameter)
                {
                    lock (syncObj)
                    {
                        taskQueue.Enqueue(taskParameter);
                    }
                }
            }
        }
    }
}
