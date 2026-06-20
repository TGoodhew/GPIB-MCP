using System;
using System.Diagnostics;
using System.Threading;
using GpibMcp.Instruments;
using Srq.Completion;

namespace SrqHwHarness
{
    /// <summary>
    /// Live-hardware harness for the SRQ completion pattern. Drives the REAL <see cref="CompletionWaiter"/>
    /// against a REAL instrument over NI-VISA, with no Claude Desktop and no MCP/stdio layer in the path.
    ///
    /// It deliberately reuses the exact production pieces so a green run here means the server's
    /// instrument_wait_complete tool will behave identically:
    ///   - I/O:         <see cref="VisaInstrumentManager"/> (the same class the server uses)
    ///   - statusModel: the bundled + user instrument database (<see cref="InstrumentDatabase"/>)
    ///   - resolution:  resource -> assignment -> model, same as the WaitComplete adapter
    ///   - waiter:      <see cref="CompletionWaiter.Wait"/>, traced to the console
    ///
    /// Usage:
    ///   SrqHwHarness --list
    ///   SrqHwHarness &lt;resource&gt; [operation] [--model M] [--setup "CMDS;"] [--timeout ms] [--poll ms]
    ///
    /// Examples (the bench cases from the SRQ epic):
    ///   SrqHwHarness GPIB0::18::INSTR sweepComplete --setup "CF 300MHZ;SP 100MHZ;ST 5S;"
    ///   SrqHwHarness GPIB0::18::INSTR sweepAndPeak  --model 8563E --timeout 60000
    ///   SrqHwHarness GPIB0::10::INSTR sweepComplete --model 3325A
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try { return Run(args); }
            catch (GpibOperationException gex)
            {
                Console.Error.WriteLine("GPIB/VISA error: " + gex.Detail);
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        private static int Run(string[] args)
        {
            var opt = Options.Parse(args);
            if (opt == null) { PrintUsage(); return 1; }

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;

            if (opt.List)
            {
                using (var visaList = new VisaInstrumentManager())
                {
                    var found = visaList.ListResources(null);
                    Console.WriteLine("VISA resources found: " + found.Count);
                    foreach (var r in found) Console.WriteLine("  " + r);
                }
                return 0;
            }

            // ---- resolve model + statusModel exactly like the server's WaitComplete ----
            var db = InstrumentDatabase.Load(InstrumentPaths.DatabaseDirectories(exeDir));
            var assignments = AssignmentStore.FromFile(InstrumentPaths.BindingsPath());

            string model = opt.Model ?? assignments.Get(opt.Resource);
            if (string.IsNullOrEmpty(model))
            {
                Console.Error.WriteLine("No model for " + opt.Resource +
                    ". Pass --model <name> or assign one in " + InstrumentPaths.BindingsPath() + ".");
                return 1;
            }

            InstrumentDefinition def;
            if (!db.TryGet(model, out def))
            {
                Console.Error.WriteLine("Unknown model '" + model + "' (not in the instrument database).");
                return 1;
            }

            Console.WriteLine("=== SRQ hardware harness - real instrument over NI-VISA, no Claude Desktop ===");
            Console.WriteLine("  resource : " + opt.Resource);
            Console.WriteLine("  model    : " + model + (opt.Model != null ? " (override)" : " (from assignment)"));
            Console.WriteLine("  operation: " + opt.Operation);
            Console.WriteLine("  timeout  : " + opt.TimeoutMs + " ms, poll " + opt.PollMs + " ms");
            Console.WriteLine();

            using (var visa = new VisaInstrumentManager())
            {
                if (!string.IsNullOrEmpty(opt.Setup))
                {
                    Console.WriteLine("setup -> " + opt.Setup);
                    visa.Write(opt.Resource, opt.Setup, VisaInstrumentManager.DefaultTimeoutMs);
                }

                // Same adapter the server's WaitComplete uses: Send=Write, SerialPoll=ReadStatusByte.
                var channel = new VisaStatusChannel(visa, opt.Resource);
                var watch = Stopwatch.StartNew();

                CompletionResult result = CompletionWaiter.Wait(
                    def.StatusModel, model, opt.Operation, opt.TimeoutMs, channel,
                    () => watch.ElapsedMilliseconds, Thread.Sleep,
                    pollIntervalMs: opt.PollMs,
                    trace: line => Console.WriteLine("   " + line));

                Console.WriteLine();
                Console.WriteLine("RESULT: " + result.Outcome + ", elapsed " + result.ElapsedMs +
                                  " ms, status 0x" + result.StatusByte.ToString("X2"));
                Console.WriteLine(result.Message);
                return ExitCode(result.Outcome);
            }
        }

        private static int ExitCode(CompletionOutcome outcome)
        {
            switch (outcome)
            {
                case CompletionOutcome.Completed: return 0;
                case CompletionOutcome.InstrumentError: return 2;
                case CompletionOutcome.TimedOut: return 3;
                default: return 4; // Refused / NeedsDefinition
            }
        }

        private static void PrintUsage()
        {
            Console.Error.WriteLine(
                "Usage:\n" +
                "  SrqHwHarness --list\n" +
                "  SrqHwHarness <resource> [operation] [--model M] [--setup \"CMDS;\"] [--timeout ms] [--poll ms]\n\n" +
                "  operation defaults to 'sweepComplete'; timeout 30000 ms; poll 500 ms.\n\n" +
                "Examples:\n" +
                "  SrqHwHarness GPIB0::18::INSTR sweepComplete --setup \"CF 300MHZ;SP 100MHZ;ST 5S;\"\n" +
                "  SrqHwHarness GPIB0::18::INSTR sweepAndPeak --model 8563E --timeout 60000\n" +
                "  SrqHwHarness GPIB0::10::INSTR sweepComplete --model 3325A");
        }

        /// <summary>Bridges <see cref="IStatusChannel"/> to real VISA I/O - identical to the server's adapter.</summary>
        private sealed class VisaStatusChannel : IStatusChannel
        {
            private readonly VisaInstrumentManager _visa;
            private readonly string _resource;
            public VisaStatusChannel(VisaInstrumentManager visa, string resource) { _visa = visa; _resource = resource; }
            public void Send(string command) => _visa.Write(_resource, command, VisaInstrumentManager.DefaultTimeoutMs);
            public int SerialPoll() => _visa.SerialPoll(_resource);
        }

        private sealed class Options
        {
            public bool List;
            public string Resource;
            public string Operation = "sweepComplete";
            public string Model;
            public string Setup;
            public int TimeoutMs = 30000;
            public int PollMs = 500;

            public static Options Parse(string[] args)
            {
                var o = new Options();
                int positional = 0;
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    switch (a)
                    {
                        case "--list": o.List = true; break;
                        case "--model": o.Model = Next(args, ref i); break;
                        case "--setup": o.Setup = Next(args, ref i); break;
                        case "--timeout": o.TimeoutMs = Int(Next(args, ref i), o.TimeoutMs); break;
                        case "--poll": o.PollMs = Int(Next(args, ref i), o.PollMs); break;
                        case "-h": case "--help": case "/?": return null;
                        default:
                            if (a.StartsWith("--")) return null;
                            if (positional == 0) { o.Resource = a; positional++; }
                            else if (positional == 1) { o.Operation = a; positional++; }
                            else return null;
                            break;
                    }
                }
                if (o.List) return o;
                return string.IsNullOrEmpty(o.Resource) ? null : o;
            }

            private static string Next(string[] args, ref int i) => (++i < args.Length) ? args[i] : null;
            private static int Int(string s, int fallback) =>
                int.TryParse(s, out int v) && v > 0 ? v : fallback;
        }
    }
}
