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

using Cekirdekler.Hardware;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Cekirdekler
{

    /// <summary>
    /// CPU|GPU means all GPUs and all CPUs
    /// GPU|ACC means all GPUs and all ACCs
    /// GPU means only GPUs are used
    /// </summary>
    public enum AcceleratorType : int
    {
        /// <summary>
        /// only selects CPUs
        /// </summary>
        CPU = 1,

        /// <summary>
        /// only selects GPUs and iGPUs
        /// </summary>
        GPU = 2,

        /// <summary>
        /// special accelerators
        /// </summary>
        ACC = 4,

    }
    


    /// <summary>
    /// compiles kernel strings for the selected devices then computes with array(C#,C++) parameters later 
    /// </summary>
    public class ClNumberCruncher
    {


        /// <summary>
        /// <para>just to upload/download data on GPU without any compute operation</para>
        /// <para>specifically used for single gpu pipelining with enqueue mode for input-output stages overlapping</para>
        /// <para>not for driver/event pipelining </para>
        /// <para>with or without multiple gpus, it skips compute part and directly does data transmissions</para>
        /// </summary>
        public bool noComputeMode
        {
            get { if (numberCruncher != null) return numberCruncher.noComputeMode; else return false; }
            set { if (numberCruncher != null) numberCruncher.noComputeMode = value; }
        }

        /// <summary>
        /// <para>to watch command queues with finer grained resolution</para>
        /// <para>true = a marker is added to the last used command queue</para>
        /// <para>a callback is triggered when a marker is reached, to increment counter for that command queue</para>
        /// <para>total count is queried by countMarkers()</para>
        /// <para>total reached markers are queried by countMarkerCallbacks()</para>
        /// <para>so the remaining markers are countMarkers() - countMarkerCallbacks()</para>
        /// <para>high performance penalty for many repeated light workload kernels (2-3 microseconds gap becomes 150-200 microseconds)</para>
        /// </summary>
        public bool fineGrainedQueueControl
        {
            get { if (numberCruncher != null) return numberCruncher.fineGrainedQueueControl; else return false; }
            set { if (numberCruncher != null) numberCruncher.fineGrainedQueueControl = value; }
        }

        internal void flush()
        {
            if (numberCruncher != null)
            {
                ClObject.Worker.flush(numberCruncher.lastUsedCommandQueueOfFirstDevice().h());
            }
        }

        /// <summary>
        /// <para>only for single gpu(or device to device pipeline stages)</para>
        /// <para>used by enqueueMode to distribute each compute job to a different queue or not</para>
        /// <para>true=distribute each compute to a different queue</para>
        /// <para>false=use single queue for all jobs</para>
        /// </summary>
        public bool enqueueModeAsyncEnable
        {
            get { if (numberCruncher != null) return numberCruncher.enqueueModeAsyncEnable; else return false; }
            set { if (numberCruncher != null) numberCruncher.enqueueModeAsyncEnable = value; }
        }

        /// <summary>
        /// <para>true: no synchronization between host and device so "used arrays" shouldn't be accessed from host(or other devices). </para>
        /// <para>false: safe to access arrays from host(or other devices) side</para>
        /// <para>kernel argument change or any change of order of arrays for a kernel will result in an instant synchronization </para>
        /// </summary>
        public bool enqueueMode
        {
            get { if (numberCruncher != null) return numberCruncher.enqueueMode; else return false; }
            set { if (numberCruncher != null) numberCruncher.enqueueMode = value; }
        }

        /// <summary>
        /// triggers all command queues synchronizations(so they finish all enqueued commands) and completes host-device synchronizations
        /// </summary>
        public void sync()
        {

        }

        private int repeats = 1;
        /// <summary>
        /// <para>number of repeats for a kernel or a list of kernels</para> 
        /// <para>to decrease unnecessary synchronization points</para>
        /// <para>to make latency better</para>
        /// <para>disables host-to-device pipelining(which is enabled with compute() parameters)</para>
        /// <para> when value > 1, it enables repeatKernelName usage </para>
        /// </summary>
        public int repeatCount
        {
            get { return repeats; }
            set { if (value < 1) repeats = 1; else repeats = value; }
        }

        private string repeatFunction = "";

        /// <summary>
        /// <para> when repeatCount>1, this kernel is added to the end of kernel list in compute()</para>
        /// <para> enqueued once at each iteration of repeat </para>
        /// <para> enqueued with global size = local size so it costs minimal latency to alter a few variables between repeats</para>
        /// <para> when not "", disables host-to-device pipelining(which is enabled with compute() parameters)</para>
        /// <para> usaes same parameters with main kernel execution </para>
        /// </summary>
        public string repeatKernelName
        {
            get { return new StringBuilder(repeatFunction).ToString(); }
            set { if ((value == null) || (value.Equals(""))) repeatFunction = ""; else repeatFunction = new StringBuilder(value).ToString(); }
        }

        internal Cores numberCruncher {get;set;}
        internal int errorNotification { get; set; }
        internal int numberOfErrorsHappened { get; set; }
        /// <summary>
        /// outputs to console: each device's performance(and memory target type) results per compute() operation
        /// </summary>
        public bool performanceFeed { get; set; }

        /// <summary>
        /// outputs last execution performance
        /// </summary>
        public void lastComputePerformanceReport()
        {
            numberCruncher.performanceReport(numberCruncher.lastUsedComputeId);
        }

        /// <summary>
        /// to ease balancing against performance spikes, interrupts, hiccups
        /// </summary>
        public bool smoothLoadBalancer { get { return numberCruncher.smoothLoadBalancer; } set { numberCruncher.smoothLoadBalancer = value; } }

        /// <summary>
        /// <para>prepares devices and compiles kernels in the kernel string</para>
        /// <para>does optionally pipelined kernel execution load balancing between multiple devices</para>
        /// </summary>
        /// <param name="cpuGpu">AcceleratorType.CPU|AcceleratorType.GPU or similar</param>
        /// <param name="kernelString">something like: @"multi-line C# string that has multiple kernel definitions"</param>
        /// <param name="numberofCPUCoresToUseAsDeviceFission">AcceleratorType.CPU uses number of threads for an N-core CPU(between 1 and N-1)(-1 means N-1)</param>
        /// <param name="numberOfGPUsToUse">AcceleratorType.GPU uses number of GPUs equal to this parameter. Between 1 and N(-1 means N)</param>
        /// <param name="stream">devices that share RAM with CPU will not do extra copies. Devices that don't share RAM will directly access RAM and reduce number of copies</param>
        /// <param name="noPipelining">disables extra command queue allocation, can't enable driver-driven pipelining later. Useful for device to device pipelining with many stages.</param>
        public ClNumberCruncher(AcceleratorType cpuGpu, string kernelString,
                            int numberofCPUCoresToUseAsDeviceFission = -1,
                            int numberOfGPUsToUse = -1, bool stream = true, bool noPipelining=false)
        {
            bool defaultQueue = false;
            if (kernelString.Contains("enqueue_kernel("))
                defaultQueue = true;
            repeatCount = 1;
            numberOfErrorsHappened = 0;
            StringBuilder cpuGpu_ = new StringBuilder("");
            if (((int)cpuGpu & ((int)AcceleratorType.CPU)) > 0)
                cpuGpu_.Append("cpu ");
            if (((int)cpuGpu & ((int)AcceleratorType.GPU)) > 0)
                cpuGpu_.Append("gpu ");
            if (((int)cpuGpu & ((int)AcceleratorType.ACC)) > 0)
                cpuGpu_.Append("acc ");

            List<string> kernelNames_ = new List<string>();

            // extracting patterns kernel _ _ _ void _ _ name _ _ (
            string kernelVoidRegex = "(kernel[\\s]+void[\\s]+[a-zA-Z\\d_]+[^\\(])";
            Regex regex = new Regex(kernelVoidRegex);
            MatchCollection match = regex.Matches(kernelString);
            for (int i = 0; i < match.Count; i++)
            {
                // extracting name
                Regex rgx = new Regex("([\\s]+[a-zA-Z\\d_]+)");
                MatchCollection mc = rgx.Matches(match[i].Value.Trim());
                kernelNames_.Add(mc[mc.Count - 1].Value.Trim());
            }
            if (kernelNames_.Count == 0)
            {
                Console.WriteLine("Error: no kernel definitions are found in string. Kernel string: \n" + kernelString);
                errorNotification = 1;
                return;
            }
            numberCruncher = new Cores(cpuGpu_.ToString(), kernelString, kernelNames_.ToArray(), defaultQueue, 256,
                numberOfGPUsToUse, stream, numberofCPUCoresToUseAsDeviceFission, noPipelining);
            if (numberCruncher.errorCode() != 0)
            {
                errorMessage_ = numberCruncher.errorMessage();
                Console.WriteLine(numberCruncher.errorMessage());
                errorNotification = numberCruncher.errorCode();
                numberCruncher.dispose();
                numberOfErrorsHappened++;
                return;
            }
            

        }

        /// <summary>
        /// returns relative compute power of each device
        /// </summary>
        /// <returns></returns>
        public double[] normalizedComputePowersOfDevices()
        {
            if ((numberCruncher != null) && (numberCruncher.tmpThroughputs != null) && (numberCruncher.tmpThroughputs.Length > 0))
            {
                double[] result = new double[numberCruncher.tmpThroughputs.Length];
                double total = 0;
                for(int i=0;i<numberCruncher.tmpThroughputs.Length;i++)
                {
                    result[i] = numberCruncher.tmpThroughputs[i];
                    total += numberCruncher.tmpThroughputs[i];
                }
                for (int i = 0; i < numberCruncher.tmpThroughputs.Length; i++)
                    result[i] /= total;
                    return result;
            }
            else
                return null;
        }

        /// <summary>
        /// returns relative global range of each device
        /// </summary>
        /// <returns></returns>
        public double[] normalizedGlobalRangesOfDevices(int id)
        {
            if ((numberCruncher != null) && (numberCruncher.tmpThroughputs != null) && (numberCruncher.tmpThroughputs.Length > 0))
            {
                double[] result = new double[numberCruncher.tmpThroughputs.Length];
                double total = 0;
                for (int i = 0; i < numberCruncher.tmpThroughputs.Length; i++)
                {
                    result[i] = numberCruncher.globalRanges[id][i];
                    total += numberCruncher.globalRanges[id][i];
                }
                for (int i = 0; i < numberCruncher.tmpThroughputs.Length; i++)
                    result[i] /= total;
                return result;
            }
            else
                return null;
        }

        /// <summary>
        /// list of devices' names
        /// </summary>
        /// <returns></returns>
        public string[] deviceNames()
        {
            return numberCruncher.deviceNames();
        }

        /// <summary>
        /// <para>prepares devices and compiles kernels in the kernel string</para>
        /// <para>does optionally pipelined kernel execution load balancing between multiple devices</para>
        /// </summary>
        /// <param name="devicesForGPGPU">one or more devices for GPGPU</param>
        /// <param name="kernelString">something like: @"multi-line C# string that has multiple kernel definitions"</param>
        /// <param name="noPipelining">disables extra command queue allocation, can't enable driver-driven pipelining later. Useful for device to device pipelining with many stages.</param>
        /// <param name="computeQueueConcurrency">max number of command queues to send commands asynchronously, max=16, min=1</param>
        public ClNumberCruncher(ClDevices devicesForGPGPU, string kernelString,bool noPipelining=false,int computeQueueConcurrency=16)
        {
            bool defaultQueue = false;
            if (kernelString.Contains("enqueue_kernel"))
                defaultQueue = true;
            repeatCount = 1;
            numberOfErrorsHappened = 0;
            List<string> kernelNames_ = new List<string>();

            // extracting patterns kernel _ _ _ void _ _ name _ _ (
            string kernelVoidRegex = "(kernel[\\s]+void[\\s]+[a-zA-Z\\d_]+[^\\(])";
            Regex regex = new Regex(kernelVoidRegex);
            MatchCollection match = regex.Matches(kernelString);
            for (int i = 0; i < match.Count; i++)
            {
                // extracting name
                Regex rgx = new Regex("([\\s]+[a-zA-Z\\d_]+)");
                MatchCollection mc = rgx.Matches(match[i].Value.Trim());
                kernelNames_.Add(mc[mc.Count - 1].Value.Trim());
            }
            if (kernelNames_.Count == 0)
            {
                Console.WriteLine("Error: no kernel definitions are found in string. Kernel string: \n" + kernelString);
                errorNotification = 1;
                return;
            }
            numberCruncher = new Cores(devicesForGPGPU, kernelString, kernelNames_.ToArray(), defaultQueue, computeQueueConcurrency, noPipelining);
            if (numberCruncher.errorCode() != 0)
            {
                errorMessage_ = numberCruncher.errorMessage();
                Console.WriteLine(numberCruncher.errorMessage());
                errorNotification = numberCruncher.errorCode();
                numberCruncher.dispose();
                numberOfErrorsHappened++;
                return;
            }
        }

        /// <summary>
        /// <para>counts all remaining markers to reach in all command queues in all devices</para>
        /// <para>for finer grained scheduling/control</para>
        /// </summary>
        /// <returns></returns>
        public int countMarkersRemaining()
        {
            int result = 0;
            result += (numberCruncher.countMarkers()- numberCruncher.countMarkerCallbacks());
            return result;
        }

        /// <summary>
        /// number of markers reached
        /// </summary>
        /// <returns></returns>
        public int countMarkersReached()
        {
            int result = 0;
            result += numberCruncher.countMarkerCallbacks();
            return result;
        }

        private string errorMessage_;

        /// <summary>
        /// kernel compile error
        /// </summary>
        /// <returns></returns>
        public string errorMessage()
        {
            return errorMessage_;
        }

        /// <summary>
        /// not zero means error
        /// </summary>
        /// <returns></returns>
        public int errorCode()
        {
            return errorNotification;
        }

        /// <summary>
        /// releases C++ resources
        /// </summary>
        public void dispose()
        {

            if (numberCruncher != null)
                numberCruncher.dispose();
            numberCruncher = null;
        }

        /// <summary>
        /// releases C++ resources
        /// </summary>
        ~ClNumberCruncher()
        {
            if(numberCruncher!=null)
                numberCruncher.dispose();
            numberCruncher = null;
        }
        
    }
}
