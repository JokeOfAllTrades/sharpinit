﻿using SharpInit.Platform;
using System;
using System.Collections.Generic;
using System.Text;

namespace SharpInit.Tasks
{
    /// <summary>
    /// Starts a process, optionally waits for it to exit before continuing execution.
    /// </summary>
    public class RunUnregisteredProcessTask : Task
    {
        public override string Type => "run-unregistered-process";
        public ProcessStartInfo ProcessStartInfo { get; set; }
        public IProcessHandler ProcessHandler { get; set; }
        public int ExecutionTime = -1;

        /// <summary>
        /// Starts a process, optionally waits for it to exit before continuing execution.
        /// </summary>
        /// <param name="process_handler">The IProcessHandler that will start the process.</param>
        /// <param name="psi">The ProcessStartInfo that defines the parameters of the newly started process.</param>
        /// <param name="time">If -1, continue execution immediately after starting process. 
        /// Else, waits the process to exit for this many milliseconds. If the process does not exit in the given timeframe, 
        /// the process is killed, and a ResultType of Timeout is returned.</param>
        public RunUnregisteredProcessTask(IProcessHandler process_handler, ProcessStartInfo psi, int time = -1)
        {
            ProcessHandler = process_handler;
            ProcessStartInfo = psi;
            ExecutionTime = time;
        }

        public override TaskResult Execute()
        {
            if (ProcessStartInfo == null)
                return new TaskResult(this, ResultType.Failure, "No ProcessStartInfo supplied.");

            try
            {
                var process = ProcessHandler.Start(ProcessStartInfo);

                if (ExecutionTime == -1)
                    return new TaskResult(this, ResultType.Success);
                else
                {
                    if (!process.Process.WaitForExit(ExecutionTime))
                    {
                        process.Process.Kill();
                        return new TaskResult(this, ResultType.Timeout, "The process did not exit in the given amount of time.");
                    }
                    else
                        return new TaskResult(this, ResultType.Success);
                }
            }
            catch (Exception ex)
            {
                return new TaskResult(this, ResultType.Failure, ex.Message);
            }
        }
    }
}
