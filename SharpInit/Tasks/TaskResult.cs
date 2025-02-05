﻿using System;
using System.Collections.Generic;
using System.Text;

namespace SharpInit.Tasks
{
    /// <summary>
    /// Represents the execution result of a task.
    /// </summary>
    public class TaskResult
    {
        public Task Task { get; set; }
        public ResultType Type { get; set; }
        public string Message { get; set; }

        /// <summary>
        /// Represents the execution result of a task.
        /// </summary>
        /// <param name="task">The task that this TaskResult represents.</param>
        /// <param name="result">The execution result.</param>
        /// <param name="msg">An optional human-readable message, usually provided when ResultType is not ResultType.Success.</param>
        public TaskResult(Task task, ResultType result, string msg = null)
        {
            Task = task;
            Type = result;
            Message = msg;
        }
    }

    public enum ResultType
    {
        Failure = 0x1,
        Success = 0x2,
        Ignorable = 0x4,
        SoftFailure = Failure | Ignorable,
        Timeout = Failure | 0x8,
        StopExecution = 0x16
    }
}
