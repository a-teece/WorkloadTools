﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using NLog;

using WorkloadTools.Consumer.Analysis;
using WorkloadTools.Listener;
using WorkloadTools.Util;

namespace WorkloadTools.Consumer.Replay
{
    internal class ReplayWorker : IDisposable
    {
        private const int SkippedDelayCountThreshold = 100;
        private const int ReplayOffsetSleepThresholdMs = 25;
        private const int ThreadSpinIterations = 1000;

        // Unlike the other loggers this one is not static because we
        // need unique properties for each instance of ReplayWorker.
        private readonly Logger logger;
        public bool DisplayWorkerStats { get; set; }
        public bool ConsumeResults { get; set; }
        public int QueryTimeoutSeconds { get; set; }
        public int WorkerStatsCommandCount { get; set; }
        public bool MimicApplicationName { get; set; }

        public LogLevel CommandErrorLogLevel { get; set; } = LogLevel.Error;

        public int FailRetryCount { get; set; }
        public int TimeoutRetryCount { get; set; }

        private SqlConnection Conn { get; set; }

        public SqlConnectionInfo ConnectionInfo { get; set; }

        public int ReplayIntervalSeconds { get; set; } = 0;
        public bool StopOnError { get; set; } = false;
        public string Name { get; private set; }
        public int SPID { get; set; }
        public bool IsRunning { get; private set; } = false;
        public bool RaiseErrorsToSqlEventTracing { get; set; } = true;

        public DateTime StartTime { get; set; }

        public Dictionary<string, string> DatabaseMap { get; set; } = new Dictionary<string, string>();

        private Task runner = null;
        private CancellationTokenSource tokenSource;

        public ReplayWorker(string name)
        {
            Name = name;
            logger = LogManager.GetCurrentClassLogger().WithProperty("Worker", name);
        }

        public bool HasCommands => !Commands.IsEmpty;

        public int QueueLength => Commands.Count;

        public DateTime LastCommandTime { get; private set; }

        private long commandCount = 0;
        private long previousCommandCount = 0;
        private DateTime previousCPSComputeTime = DateTime.Now;
        private readonly List<int> commandsPerSecond = new List<int>();

        private readonly ConcurrentQueue<ReplayCommand> Commands = new ConcurrentQueue<ReplayCommand>();

        public bool IsStopped { get; private set; } = false;

        private readonly SqlTransformer transformer = new SqlTransformer();

        private readonly Dictionary<int, int> preparedStatements = new Dictionary<int, int>();
        private SpinWait _spinWait = new SpinWait();

        private int continiousSkippedDelays = 0;

        private enum UserErrorType
        {
            Timeout = 82,
            Error = 83
        }

        private void InitializeConnection()
        {
            logger.Debug("Connecting to server {serverName}", ConnectionInfo.ServerName);

            ConnectionInfo.DatabaseMap = DatabaseMap;
            var connString = ConnectionInfo.ConnectionString;

            Conn = new SqlConnection(connString);
            Conn.Open();

            logger.Debug("Connected");
        }

        public void Start()
        {
            tokenSource = new CancellationTokenSource();
            var token = tokenSource.Token;

            if (runner == null)
            {
                // Given the potential for lots of Workers we need to allow over-subscription of threads using the LongRunning option.
                // "
                //     Specifies that a task will be a long-running, coarse-grained operation involving
                //     fewer, larger components than fine-grained systems. It provides a hint to the
                //     System.Threading.Tasks.TaskScheduler that oversubscription may be warranted.
                //     Oversubscription lets you create more threads than the available number of hardware
                //     threads. It also provides a hint to the task scheduler that an additional thread
                //     might be required for the task so that it does not block the forward progress
                //     of other threads or work items on the local thread-pool queue.
                // "
                runner = Task.Factory.StartNew(() => Run(), token, TaskCreationOptions.LongRunning, TaskScheduler.Current);
            }
        }

        public void Run()
        {
            IsRunning = true;
            while (!IsStopped && IsRunning)
            {
                try
                {
                    ExecuteNextCommand();
                }
                catch (Exception e)
                {
                    logger.Error(e, "Error starting Worker");
                }
            }
        }

        public void Stop()
        {
            logger.Debug("Stopping");

            IsStopped = true;
            IsRunning = false;
            tokenSource?.Cancel();

            logger.Debug("Stopped");
        }

        public void ExecuteNextCommand()
        {
            var cmd = GetNextCommand();
            if (cmd != null)
            {
                ExecuteCommand(cmd);
                commandCount++;
            }
            else
            {
                // Release the thread when out of work
                IsRunning = false;
            }
        }

        public ReplayCommand GetNextCommand()
        {
            _ = Commands.TryDequeue(out var result);

            // Previously this method would loop and use a spinWait.
            // Memory dumps taken of a large workload showed a very large number of tasks in a Scheduled state on this spin.
            // Better concurrency has been achieved by letting the task complete.
            // The ReplayConsumer will then start a task up again if more work comes to this worker in future.

            return result;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void ExecuteCommand(ReplayCommand command, int failRetryCount = 0, int timeoutRetryCount = 0)
        {
            LastCommandTime = DateTime.Now;

            if (Conn == null)
            {
                try
                {
                    ConnectionInfo.ApplicationName = "WorkloadTools-ReplayWorker";
                    if (MimicApplicationName)
                    {
                        ConnectionInfo.ApplicationName = command.ApplicationName;
                        if (string.IsNullOrEmpty(ConnectionInfo.ApplicationName))
                        {
                            ConnectionInfo.ApplicationName = "WorkloadTools-ReplayWorker";
                        }
                    }
                    InitializeConnection();
                }
                catch (SqlException se)
                {
                    logger.Error(se, "Unable to acquire the connection. Quitting the ReplayWorker");

                    return;
                }
            }

            if (Conn != null)
            {
                while (Conn.State == ConnectionState.Connecting)
                {
                    if (IsStopped)
                    {
                        break;
                    }

                    logger.Debug("Connection is in connecting state. Sleeping for 5ms");

                    Thread.Sleep(5);
                }
            }

            if (Conn == null || (Conn.State == ConnectionState.Closed) || (Conn.State == ConnectionState.Broken))
            {
                InitializeConnection();
            }

            // Extract the handle from the prepared statement
            var nst = transformer.Normalize(command.CommandText);

            // If the command comes with a replay offset, evaluate it now.
            // The offset in milliseconds is set in FileWorkloadListener.
            // The other listeners do not set this value, as they
            // already come with the original timing
            if (command.ReplayOffset > 0)
            {
                var delayMs = command.ReplayOffset - (DateTime.Now - StartTime).TotalMilliseconds;

                // Delay execution only if necessary
                if (delayMs > 0)
                {
                    // We're not skipping this delay, so reset the counter.
                    continiousSkippedDelays = 0;

                    // Each command has a requested offset from the beginning
                    // of the workload and this class does its best to respect it.
                    // If the previous commands take longer in the target environment
                    // the offset cannot be respected and the command will execute
                    // without further waits, but there is no way to recover 
                    // the delay that has built up to that point.
                    logger.Trace("Command start time is {startTime:yyyy-MM-ddTHH\\:mm\\:ss.fffffff} which is an offset of {ReplayOffset}ms from the start so waiting", command.StartTime, command.ReplayOffset);

                    var stopwatch = Stopwatch.StartNew();

                    // If we're outside of the ThreadSleepThresholdMs then sleep to give up cpu time while we wait
                    while (stopwatch.Elapsed.TotalMilliseconds < delayMs - ReplayOffsetSleepThresholdMs)
                    {
                        Thread.Sleep(ReplayOffsetSleepThresholdMs);
                    }

                    // If we're getting close to the event time then spinwait because we need higher accuracy
                    while (stopwatch.Elapsed.TotalMilliseconds < delayMs)
                    {
                        Thread.SpinWait(ThreadSpinIterations);
                    }

                    stopwatch.Stop();

                    // Highlight if the delays are inaccurate (with 100ms error margin)
                    // If there are a lot of these warnings then it may suggest either
                    // the above ReplayOffsetSleepThresholdMs/ThreadSpinIterations are
                    // too high or the replay host does not have enough CPU capacity to
                    // replay the source workload.
                    if (stopwatch.Elapsed.TotalMilliseconds > delayMs + 100)
                    {
                        logger.Warn("Requested delay was {requestedDelay}ms but actual delay was {actualDelay}ms", delayMs, stopwatch.Elapsed.TotalMilliseconds);
                    }
                }
                else if (delayMs < -10000)
                {
                    // If we're more than 10s behind then 
                    logger.Trace("Command start time is {startTime:yyyy-MM-ddTHH\\:mm\\:ss.fffffff} which is an offset of {ReplayOffset}ms from the start but replay is behind so it should have executed {delayMs}ms ago", command.StartTime, command.ReplayOffset, delayMs);
                    continiousSkippedDelays++;

                    if (continiousSkippedDelays % SkippedDelayCountThreshold == 0)
                    {
                        // If we are consistently behind and the configuration has
                        // requested SynchronizationMode we're actually doing a stress test.
                        logger.Warn("The last {skippedDelays} Commands requested a delay but replay is > 10s behind so were processed immediately which may indicate that either this tool or the target system cannot keep up with the workload", continiousSkippedDelays);
                    }
                }
            }

            if (nst.CommandType == NormalizedSqlText.CommandTypeEnum.SP_RESET_CONNECTION)
            {
                //If event is a sp_reset_connection, call a connection close and open to
                //force connection to get back to connection pool and reset it so that
                //it's clean for the next event
                Conn.Close();
                Conn.Open();

                return;
            }
            else if (nst.CommandType == NormalizedSqlText.CommandTypeEnum.SP_RESET_CONNECTION_NONPOOLED)
            {
                // If event is a nonpooled sp_reset_connection, call a ClearPool(conn)
                // to force a new connection.
                ClearPool(Conn);

                return;
            }
            else if (nst.CommandType == NormalizedSqlText.CommandTypeEnum.SP_PREPARE)
            {
                command.CommandText = nst.NormalizedText;
            }
            else if (nst.CommandType == NormalizedSqlText.CommandTypeEnum.SP_UNPREPARE || nst.CommandType == NormalizedSqlText.CommandTypeEnum.SP_EXECUTE)
            {
                // look up the statement to unprepare in the dictionary
                if (preparedStatements.ContainsKey(nst.Handle))
                {
                    // the sp_execute statement has already been "normalized"
                    // by replacing the original statement number with the § placeholder
                    command.CommandText = nst.NormalizedText.ReplaceFirst("§", preparedStatements[nst.Handle].ToString());

                    if (nst.CommandType == NormalizedSqlText.CommandTypeEnum.SP_UNPREPARE)
                    {
                        _ = preparedStatements.Remove(nst.Handle);
                    }
                }
                else
                {
                    return; // statement not found: better return
                }
            }

            try
            {
                // Try to remap the database according to the database map
                if (DatabaseMap.ContainsKey(command.Database))
                {
                    command.Database = DatabaseMap[command.Database];
                }

                if (Conn.Database != command.Database)
                {
                    logger.Debug("Changing database to {databaseName}", command.Database);

                    Conn.ChangeDatabase(command.Database);
                }

                using (var cmd = new SqlCommand(command.CommandText))
                {
                    cmd.Connection = Conn;
                    cmd.CommandTimeout = QueryTimeoutSeconds;

                    if (nst.CommandType == NormalizedSqlText.CommandTypeEnum.SP_PREPARE)
                    {
                        if (cmd.CommandText == null)
                        {
                            return;
                        }

                        var handle = -1;
                        try
                        {
                            var res = cmd.ExecuteScalar();
                            if (res != null)
                            {
                                handle = (int)res;
                                if (!preparedStatements.ContainsKey(nst.Handle))
                                {
                                    preparedStatements.Add(nst.Handle, handle);
                                }
                            }
                        }
                        catch (NullReferenceException)
                        {
                            throw;
                        }
                    }
                    else if (ConsumeResults)
                    {
                        using (var reader = cmd.ExecuteReader())
                        using (var consumer = new ResultSetConsumer(reader))
                        {
                            consumer.Consume();
                        }
                    }
                    else
                    {
                        _ = cmd.ExecuteNonQuery();
                    }
                }

                logger.Trace("SUCCES - \n{commandText}", command.CommandText);
                if (commandCount > 0 && commandCount % WorkerStatsCommandCount == 0)
                {
                    var seconds = (DateTime.Now - previousCPSComputeTime).TotalSeconds;
                    var cps = (commandCount - previousCommandCount) / ((seconds == 0) ? 1 : seconds);
                    previousCPSComputeTime = DateTime.Now;
                    previousCommandCount = commandCount;

                    if (DisplayWorkerStats)
                    {
                        commandsPerSecond.Add((int)cps);
                        cps = commandsPerSecond.Average();

                        logger.Info("{commandCount} commands executed - {pendingCommands} commands pending - Last Event Sequence: {lastEventSequence} - {cps} commands per second", commandCount, Commands.Count, command.EventSequence, (int)cps);
                    }
                }

                // Update the LastCommandTime again in case the duration of a Consume() call exceeded LastCommandTime + InactiveWorkerTerminationTimeoutSeconds
                LastCommandTime = DateTime.Now;
            }
            catch (SqlException e)
            {
                // handle timeouts
                if (e.Number == -2)
                {
                    RaiseTimeoutEvent(command.CommandText);
                }
                else
                {
                    RaiseErrorEvent(command, e.Message);
                }

                // If the workload is exepected to include lots of errors then logging at the default Error level can become really noisy!
                logger.Log(CommandErrorLogLevel, e, "Sequence[{eventSequence}] - Error: \n{commandText}", command.EventSequence, command.CommandText);

                if (StopOnError)
                {
                    ClearPool(Conn);

                    throw;
                }
                else
                {
                    if (e.Number != -2 && failRetryCount < FailRetryCount)
                    {
                        logger.Warn("Retrying Sequence[{eventSequence}] - Retrying command (current fail retry: {failRetryCount})", command.EventSequence, failRetryCount);
                        ExecuteCommand(command, ++failRetryCount, timeoutRetryCount);
                    }
                    if (e.Number == -2 && timeoutRetryCount < TimeoutRetryCount)
                    {
                        logger.Warn("Retrying Sequence[{eventSequence}] - Retrying command (current timeout retry: {timeoutRetryCount})", command.EventSequence, timeoutRetryCount);
                        ExecuteCommand(command, failRetryCount, ++timeoutRetryCount);
                    }
                }
            }
            catch (Exception e)
            {
                // If the workload is exepected to include lots of errors then logging at the default Error level can become really noisy!
                logger.Log(CommandErrorLogLevel, e, "Sequence[{eventSequence}] - Error: \n{commandText}", command.EventSequence, command.CommandText);

                ClearPool(Conn);

                if (StopOnError)
                {
                    throw;
                }
            }
        }

        private void ClearPool(SqlConnection conn)
        {
            if (conn == null)
            {
                return;
            }

            try { SqlConnection.ClearPool(conn); } catch (Exception) { /*swallow */}

            if (conn.State == ConnectionState.Open)
            {
                try { conn.Close(); } catch (Exception) { /* swallow */ }
                try { conn.Dispose(); conn = null; } catch (Exception) { /* swallow */ }
            }
        }

        private void RaiseTimeoutEvent(string commandText)
        {
            if (!RaiseErrorsToSqlEventTracing) { return; }

            RaiseErrorEvent($"WorkloadTools.Timeout[{QueryTimeoutSeconds}]", commandText, UserErrorType.Timeout);
        }

        private void RaiseErrorEvent(ReplayCommand Command, string ErrorMessage)
        {
            if (!RaiseErrorsToSqlEventTracing) { return; }

            var msg = $@"DATABASE:
{Command.Database}
SEQUENCE:
{Command.EventSequence}
MESSAGE:
{ErrorMessage}
--------------------
{Command.CommandText}
";

            RaiseErrorEvent("WorkloadTools.Replay", msg, UserErrorType.Error);
        }

        private void RaiseErrorEvent(string info, string message, UserErrorType type)
        {
            if (!RaiseErrorsToSqlEventTracing) { return; }

            // Raise a custom event. Both SqlTrace and Extended Events can capture this event.
            var sql = "EXEC sp_trace_generateevent @eventid = @eventid, @userinfo = @userinfo, @userdata = @userdata;";

            try
            {
                using (var cmd = new SqlCommand(sql))
                {
                    // Creating a new connection to raise the custom event to don't mess up
                    // with existing connection as a reset(close the connection) now would cause a 
                    // next event call to fail in case it has dependencies of objects or 
                    // user settings used in the connection
                    var connString = ConnectionInfo.ConnectionString;
                    var connErrorEvent = new SqlConnection(connString);
                    connErrorEvent.Open();

                    cmd.Connection = connErrorEvent;
                    _ = cmd.Parameters.Add(new SqlParameter("@eventid", SqlDbType.Int) { Value = type });
                    _ = cmd.Parameters.Add(new SqlParameter("@userinfo", SqlDbType.NVarChar, 128) { Value = info });
                    _ = cmd.Parameters.Add(new SqlParameter("@userdata", SqlDbType.VarBinary, 8000) { Value = Encoding.Unicode.GetBytes(message.Substring(0, message.Length > 8000 ? 8000 : message.Length)) });
                    _ = cmd.ExecuteNonQuery();

                    ClearPool(connErrorEvent);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Unable to raise error event");
            }
        }

        public void AppendCommand(ReplayCommand cmd)
        {
            Commands.Enqueue(cmd);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stop();
                try
                {
                    if (Conn != null)
                    {
                        if (Conn.State == ConnectionState.Open)
                        {
                            try { Conn.Close(); } catch (Exception) { /* swallow */ }
                            try { Conn.Dispose(); } catch (Exception) { /* swallow */ }
                        }
                        Conn = null;
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex);
                }
                try
                {
                    if (runner != null)
                    {
                        while (!(runner.IsCompleted || runner.IsFaulted || runner.IsCanceled))
                        {
                            _spinWait.SpinOnce();
                        }
                        runner.Dispose();
                        runner = null;
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex);
                }
                try
                {
                    if (tokenSource != null)
                    {
                        tokenSource.Dispose();
                        tokenSource = null;
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn(ex);
                }
                logger.Trace($"Disposed");
            }
        }
    }
}

