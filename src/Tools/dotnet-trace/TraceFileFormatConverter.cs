// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.Diagnostics.Tracing.Stacks.Formats;

namespace Microsoft.Diagnostics.Tools.Trace
{
    internal enum TraceFileFormat { NetTrace = 1, Speedscope, Chromium };

    internal static class TraceFileFormatConverter
    {
        private static readonly IReadOnlyDictionary<TraceFileFormat, string> TraceFileFormatExtensions = new Dictionary<TraceFileFormat, string>() {
            { TraceFileFormat.NetTrace,     "nettrace" },
            { TraceFileFormat.Speedscope,   "speedscope.json" },
            { TraceFileFormat.Chromium,     "chromium.json" }
        };

        internal static string GetConvertedFilename(string fileToConvert, string outputfile, TraceFileFormat format)
        {
            if (string.IsNullOrWhiteSpace(outputfile))
            {
                outputfile = fileToConvert;
            }

            return Path.ChangeExtension(outputfile, TraceFileFormatExtensions[format]);
        }

        internal static void ConvertToFormat(TextWriter stdOut, TextWriter stdError, TraceFileFormat format, string fileToConvert, string outputFilename, bool stitchAsyncCallStacks = false)
        {
            switch (format)
            {
                case TraceFileFormat.NetTrace:
                    break;
                case TraceFileFormat.Speedscope:
                case TraceFileFormat.Chromium:
                    stdOut.WriteLine($"Processing trace data file '{fileToConvert}' to create a new {format} file '{outputFilename}'.");
                    try
                    {
                        Convert(stdOut, format, fileToConvert, outputFilename, stitchAsyncCallStacks);
                    }
                    // TODO: On a broken/truncated trace, the exception we get from TraceEvent is a plain System.Exception type because it gets caught and rethrown inside TraceEvent.
                    // We should probably modify TraceEvent to throw a better exception.
                    catch (Exception ex)
                    {
                        if (ex.ToString().Contains("Read past end of stream."))
                        {
                            stdOut.WriteLine("Detected a potentially broken trace. Continuing with best-efforts to convert the trace, but resulting speedscope file may contain broken stacks as a result.");
                            Convert(stdOut, format, fileToConvert, outputFilename, stitchAsyncCallStacks, continueOnError: true);
                        }
                        else
                        {
                            stdError.WriteLine(ex.ToString());
                        }
                    }
                    break;
                default:
                    // Validation happened way before this, so we shoud never reach this...
                    throw new DiagnosticToolException($"Invalid TraceFileFormat \"{format}\"");
            }
            stdOut.WriteLine("Conversion complete");
        }

        private static void Convert(TextWriter stdOut, TraceFileFormat format, string fileToConvert, string outputFilename, bool stitchAsyncCallStacks, bool continueOnError = false)
        {
            string etlxFilePath = TraceLog.CreateFromEventPipeDataFile(fileToConvert, null, new TraceLogOptions() { ContinueOnError = continueOnError });
            using (SymbolReader symbolReader = new(TextWriter.Null) { SymbolPath = SymbolPath.MicrosoftSymbolServerPath })
            using (TraceLog eventLog = new(etlxFilePath))
            {
                MutableTraceEventStackSource stackSource = new(eventLog)
                {
                    OnlyManagedCodeStacks = true // EventPipe currently only has managed code stacks.
                };

                // When stitchAsyncCallStacks is set, the logical async continuation frames (RuntimeAsync /
                // StateMachineAsync) captured by the async-profiler EventSource are stitched directly into each
                // sample's synchronous stack (opt-in; requires async-profiler data in the trace).
                SampleProfilerThreadTimeComputer computer = new(eventLog, symbolReader, stitchAsyncCallStacks: stitchAsyncCallStacks)
                {
                    IncludeEventSourceEvents = false // SpeedScope handles only CPU samples, events are not supported
                };
                computer.GenerateThreadTimeStacks(stackSource);

                // --async was requested but the trace carried no async-profiler data, so stitching was skipped and a
                // normal conversion was produced. Warn so the empty result isn't mistaken for a stitching failure.
                if (stitchAsyncCallStacks && !computer.AsyncStitchActive)
                {
                    stdOut.WriteLine("Warning: --async was specified but the trace contains no async-profiler data. " +
                        "Collect with the async-profiler provider (e.g. 'dotnet-trace collect --async') to " +
                        "enable async call stack stitching. Producing a normal (non-stitched) conversion.");
                }

                switch (format)
                {
                    case TraceFileFormat.Speedscope:
                        SpeedScopeStackSourceWriter.WriteStackViewAsJson(stackSource, outputFilename);
                        break;
                    case TraceFileFormat.Chromium:
                        ChromiumStackSourceWriter.WriteStackViewAsJson(stackSource, outputFilename, compress: false);
                        break;
                    default:
                        // we should never get here
                        throw new DiagnosticToolException($"Invalid TraceFileFormat \"{format}\"");
                }
            }

            if (File.Exists(etlxFilePath))
            {
                File.Delete(etlxFilePath);
            }
        }
    }
}
